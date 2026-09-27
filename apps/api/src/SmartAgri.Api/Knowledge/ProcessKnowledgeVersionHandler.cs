using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Knowledge;

/// <summary>
/// Handles <see cref="ProcessKnowledgeVersionJob.Kind"/> (M2 plan, Slices 6 and 7; tickets #40,
/// #41): reads the version's file with the <see cref="IDocumentTextExtractor"/> for its format,
/// judges and chunks it (<see cref="KnowledgeVersionProcessing"/>), embeds every chunk
/// (<see cref="KnowledgeChunkEmbedder"/>, in batches, on behalf of the uploader), and writes the
/// units, the chunks with their vectors and the version's status in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is at least once and a run can outlive its lease, so the same job may run again —
/// even at the same time. A version that no longer exists (its document was deleted) is nothing
/// to do; one already <c>ready</c>, <c>partially-readable</c> or <c>failed</c> was finished by
/// an earlier run and is left alone (so the owner's exclusions survive). Otherwise the result
/// replaces whatever units and chunks the version has, e.g. from before a retry. The status
/// change is saved first in that transaction: <see cref="KnowledgeDocumentVersion.ProcessingStatus"/>
/// is a concurrency token, so the row is locked from then on, and a concurrent run of the same
/// job either waits and then finds the version no longer processing, or is the one that waits
/// — either way exactly one result is committed.
/// </para>
/// <para>
/// Embedding happens before that transaction (no row is locked while the model is called) and
/// before the version completes, so a version is never <c>ready</c> without vectors: a run that
/// fails to embed commits nothing but its <c>ModelInvocation</c> rows and stays
/// <c>processing</c>, and the next attempt starts over. Every chunk is embedded, as none is
/// excluded yet; the owner's later exclusions keep the vector, so including a chunk again needs
/// no model call.
/// </para>
/// <para>
/// A file that cannot be read at all (a password, not UTF-8, damaged) fails the job at once
/// with <see cref="PermanentJobFailure"/> — the bytes will not change on a retry — and its
/// message is the owner's issue (<see cref="KnowledgeProcessingIssues.ForFinalFailure"/>). An
/// embedding failure (<see cref="KnowledgeEmbeddingException"/>: the provider is down, rate
/// limited, refuses the key or is not configured) is retried by the queue, and after the last
/// attempt the version fails with the exception's message
/// (<see cref="KnowledgeProcessingIssues.EmbeddingUnavailable"/> or
/// <see cref="KnowledgeProcessingIssues.EmbeddingNotConfigured"/>). Anything else (the database,
/// a bug) is retried too and ends with a generic "retry" issue.
/// </para>
/// </remarks>
internal sealed class ProcessKnowledgeVersionHandler : IJobHandler
{
    private readonly AppDbContext _dbContext;
    private readonly IEnumerable<IDocumentTextExtractor> _extractors;
    private readonly KnowledgeChunkEmbedder _embedder;
    private readonly KnowledgeOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProcessKnowledgeVersionHandler> _logger;

    public ProcessKnowledgeVersionHandler(
        AppDbContext dbContext,
        IEnumerable<IDocumentTextExtractor> extractors,
        KnowledgeChunkEmbedder embedder,
        IOptions<KnowledgeOptions> options,
        TimeProvider clock,
        ILogger<ProcessKnowledgeVersionHandler> logger)
    {
        _dbContext = dbContext;
        _extractors = extractors;
        _embedder = embedder;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var version = await FindAsync(job, cancellationToken);
        if (version is null)
        {
            return;
        }

        if (version.ProcessingStatus == KnowledgeDocumentStatus.Queued)
        {
            version.StartProcessing(_clock.GetUtcNow());
            if (!await TrySaveVersionAsync(version, cancellationToken))
            {
                return;
            }
        }

        if (version.ProcessingStatus != KnowledgeDocumentStatus.Processing)
        {
            return;
        }

        var content = await _dbContext.KnowledgeFileContents
            .AsNoTracking()
            .Where(file => file.VersionId == version.Id)
            .Select(file => file.Bytes)
            .SingleOrDefaultAsync(cancellationToken);
        if (content is null)
        {
            // Deleted since the version was read; the version went with it.
            return;
        }

        var limits = _options.ExtractionLimits;
        var processed = KnowledgeVersionProcessing.Process(Extract(version, content, limits, cancellationToken), limits, ChunkingOptions.Default);
        var chunks = await EmbedChunksAsync(version, processed, cancellationToken);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        version.CompleteProcessing(processed.Status, processed.Issue, _clock.GetUtcNow());
        if (!await TrySaveVersionAsync(version, cancellationToken))
        {
            // Disposing the transaction rolls back; the other run's result stands.
            return;
        }

        await DeleteExtractionAsync(version.Id, cancellationToken);
        foreach (var unit in processed.Units)
        {
            _dbContext.KnowledgeExtractedUnits.Add(KnowledgeExtractedUnit.Create(
                version, unit.Ordinal, unit.Kind, unit.LocationLabel, unit.Text, unit.Readable, unit.IssueCode));
        }

        _dbContext.KnowledgeChunks.AddRange(chunks);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Knowledge version {VersionId} processed: {Status}, {UnitCount} units, {ReadableCount} readable, {ChunkCount} chunks.",
            version.Id,
            processed.Status,
            processed.Units.Count,
            processed.Units.Count(unit => unit.Readable),
            processed.Units.Sum(unit => unit.Chunks.Count));
    }

    /// <summary>Never leaves a version stuck in <c>queued</c>/<c>processing</c>, where it could
    /// not be retried. Runs in the queue's transaction that marks the job failed.</summary>
    public async Task OnFinalFailureAsync(JobContext job, string error, CancellationToken cancellationToken)
    {
        var version = await FindAsync(job, cancellationToken);
        if (version?.ProcessingStatus is not (KnowledgeDocumentStatus.Queued or KnowledgeDocumentStatus.Processing))
        {
            return;
        }

        version.MarkFailed(KnowledgeProcessingIssues.ForFinalFailure(error), _clock.GetUtcNow());
        if (await TrySaveVersionAsync(version, cancellationToken))
        {
            // What an earlier processing of a retried version left no longer describes it.
            await DeleteExtractionAsync(version.Id, cancellationToken);
        }
    }

    /// <summary>The version's chunks, each with its vector from the configured model.</summary>
    private async Task<List<KnowledgeChunk>> EmbedChunksAsync(KnowledgeDocumentVersion version, ProcessedVersion processed, CancellationToken cancellationToken)
    {
        var chunks = new List<KnowledgeChunk>();
        var texts = new List<string>();
        foreach (var unit in processed.Units)
        {
            for (var ordinal = 0; ordinal < unit.Chunks.Count; ordinal++)
            {
                var chunk = unit.Chunks[ordinal];
                chunks.Add(KnowledgeChunk.Create(version, unit.Ordinal, ordinal, chunk.LocationLabel, chunk.Text));
                texts.Add(KnowledgeEmbeddingText.For(unit.Kind, chunk.LocationLabel, chunk.Text));
            }
        }

        if (chunks.Count == 0)
        {
            return chunks;
        }

        var vectors = await _embedder.EmbedDocumentsAsync(texts, version.UploadedByAccountId, cancellationToken);
        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i].SetEmbedding(vectors[i], _embedder.Model);
        }

        return chunks;
    }

    private ExtractedDocument Extract(KnowledgeDocumentVersion version, byte[] content, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        if (!KnowledgeFileFormats.TryFromContentType(version.ContentType, out var format))
        {
            // Versions only ever store a canonical content type (KnowledgeDocumentVersion.Create).
            throw new InvalidOperationException($"Version {version.Id} has an unknown content type.");
        }

        var extractor = _extractors.SingleOrDefault(candidate => candidate.CanExtract(format))
            ?? throw new InvalidOperationException($"No single text extractor is registered for {format}.");
        try
        {
            return extractor.Extract(format, content, limits, cancellationToken);
        }
        catch (DocumentExtractionException exception)
        {
            // The job runner logs this failure with the extractor's exception inside it.
            throw new PermanentJobFailure(KnowledgeProcessingIssues.For(exception.Failure), exception);
        }
    }

    /// <summary>Saves the version's status change; false when the row no longer has the status
    /// it was read with, or is gone (someone else acted on it), in which case nothing of this
    /// run may be saved.</summary>
    private async Task<bool> TrySaveVersionAsync(KnowledgeDocumentVersion version, CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.Entry(version).State = EntityState.Detached;
            return false;
        }
    }

    private async Task DeleteExtractionAsync(Guid versionId, CancellationToken cancellationToken)
    {
        await _dbContext.KnowledgeChunks.Where(chunk => chunk.VersionId == versionId).ExecuteDeleteAsync(cancellationToken);
        await _dbContext.KnowledgeExtractedUnits.Where(unit => unit.VersionId == versionId).ExecuteDeleteAsync(cancellationToken);
    }

    private Task<KnowledgeDocumentVersion?> FindAsync(JobContext job, CancellationToken cancellationToken)
    {
        var versionId = job.ReadPayload<ProcessKnowledgeVersionJob>().VersionId;
        return _dbContext.KnowledgeDocumentVersions.SingleOrDefaultAsync(version => version.Id == versionId, cancellationToken);
    }
}
