using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Jobs;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Knowledge.Evaluation;

/// <summary>What happened to one version of a set's document.</summary>
/// <param name="SetVersion">Its position in the set's document, 1 for the first.</param>
/// <param name="VersionId">The version in the database; <see langword="null"/> when the document
/// was skipped (<see cref="Skipped"/>).</param>
/// <param name="Uploaded">Uploaded by this import (otherwise it was already there).</param>
/// <param name="Approved">Approved by this import.</param>
/// <param name="Skipped">Why it was left alone: a document of the same name that is not the set's.</param>
internal sealed record ImportedVersion(
    string KnowledgeBaseName,
    string DocumentName,
    int SetVersion,
    Guid? VersionId,
    bool Uploaded,
    bool Approved,
    KnowledgeDocumentStatus? Status,
    KnowledgeReviewState? ReviewState,
    string? Issue,
    string? Skipped = null);

/// <summary>What <see cref="KnowledgeSetImporter.ImportAsync"/> did.</summary>
/// <param name="KnowledgeBaseIds">The set's knowledge bases in the organization, by name.</param>
internal sealed record KnowledgeSetImport(IReadOnlyDictionary<string, Guid> KnowledgeBaseIds, IReadOnlyList<ImportedVersion> Versions)
{
    public int Uploaded => Versions.Count(version => version.Uploaded);

    public int Approved => Versions.Count(version => version.Approved);

    /// <summary>Versions that are not approved: processing failed or has not finished, or the
    /// document was skipped.</summary>
    public IReadOnlyList<ImportedVersion> NotApproved =>
        [.. Versions.Where(version => version.ReviewState != KnowledgeReviewState.Approved)];
}

/// <summary>
/// Puts a <see cref="RetrievalEvalSet"/>'s knowledge bases and documents into an organization
/// through the normal pipeline (M2 plan Slice 16; ticket #50), for <c>eval-retrieval</c> and the
/// development seeder's demo knowledge: every file is <b>uploaded</b> exactly as
/// <c>POST .../documents</c> and <c>POST .../documents/{docId}/versions</c> store one — the same
/// upload rules, document, version, original file, activity row and processing job in one save —
/// then the job queue <b>processes</b> the versions (<see cref="JobRunner"/>, what the worker
/// runs), and each processed version is <b>approved</b> as <c>POST .../versions/approve</c> does,
/// in the set's order, so a document's last version is in effect and the earlier ones archived.
/// </summary>
/// <remarks>
/// <para>
/// Only fills in what is missing, so importing again changes nothing: a knowledge base is found
/// by its owner and name, a document by its name, a version by its content (SHA-256). A document
/// of the same name none of whose versions is one of the set's is somebody else's and is left
/// alone. An approved version is never touched again, and an earlier version is never approved
/// after a later one (that would put the older text back in effect).
/// </para>
/// <para>
/// It reads and writes through contexts pinned to the organization
/// (<see cref="FixedOrganizationContext"/>, as <see cref="SmartAgri.Infrastructure.Seeding.DevelopmentSeeder"/>
/// and <c>reindex</c> do), so the organization filter and write guard apply as in a request.
/// </para>
/// <para>
/// One-shot subcommands and <c>migrate</c> run without the job worker, so this runs the queue
/// itself until the versions are processed or <c>processingTimeout</c> has passed, and approves
/// what is ready by then; the rest stays pending review (a failed embedding call is retried by
/// the queue with its usual backoff). <see cref="JobRunner.RunUntilIdleAsync"/> runs whatever is
/// due in the database, as the worker would. A running Api's worker may take the jobs instead:
/// this waits for their outcome the same way.
/// </para>
/// </remarks>
internal sealed class KnowledgeSetImporter
{
    /// <summary>How often the versions' status is checked while processing is not finished.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly JobRunner _runner;
    private readonly KnowledgeOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<KnowledgeSetImporter> _logger;

    public KnowledgeSetImporter(
        DbContextOptions<AppDbContext> dbContextOptions,
        JobRunner runner,
        IOptions<KnowledgeOptions> options,
        TimeProvider clock,
        ILogger<KnowledgeSetImporter> logger)
    {
        _dbContextOptions = dbContextOptions;
        _runner = runner;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Imports <paramref name="knowledgeBases"/> into <paramref name="organizationId"/>,
    /// owned by (and uploaded and approved as) <paramref name="ownerAccountId"/>, waiting at most
    /// <paramref name="processingTimeout"/> for processing (<see cref="TimeSpan.Zero"/>: run the
    /// queue once).</summary>
    public async Task<KnowledgeSetImport> ImportAsync(
        Guid organizationId,
        Guid ownerAccountId,
        IReadOnlyList<EvalKnowledgeBase> knowledgeBases,
        TimeSpan processingTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBases);

        var knowledgeBaseIds = new Dictionary<string, Guid>();
        var uploads = new List<(EvalKnowledgeBase KnowledgeBase, EvalDocument Document, IReadOnlyList<(Guid? Id, bool Uploaded)> Versions, string? Skipped)>();
        var batchId = Guid.CreateVersion7();
        await using (var dbContext = ForOrganization(organizationId))
        {
            foreach (var entry in knowledgeBases)
            {
                var knowledgeBase = await EnsureKnowledgeBaseAsync(dbContext, organizationId, ownerAccountId, entry, cancellationToken);
                knowledgeBaseIds[entry.Name] = knowledgeBase.Id;
                foreach (var document in entry.Documents)
                {
                    var (versions, skipped) = await UploadAsync(dbContext, knowledgeBase, ownerAccountId, document, batchId, cancellationToken);
                    uploads.Add((entry, document, versions, skipped));
                }
            }
        }

        var ids = uploads.SelectMany(upload => upload.Versions).Where(version => version.Id is not null).Select(version => version.Id!.Value).ToList();
        await ProcessAsync(organizationId, ids, processingTimeout, cancellationToken);

        var results = new List<ImportedVersion>();
        await using (var dbContext = ForOrganization(organizationId))
        {
            foreach (var (knowledgeBase, document, versions, skipped) in uploads)
            {
                results.AddRange(await ApproveAsync(dbContext, ownerAccountId, knowledgeBase.Name, document, versions, skipped, cancellationToken));
            }
        }

        return new KnowledgeSetImport(knowledgeBaseIds, results);
    }

    private AppDbContext ForOrganization(Guid organizationId) => new(_dbContextOptions, new FixedOrganizationContext(organizationId));

    /// <summary>The owner's knowledge base of that name (the oldest, should there be several), or
    /// a new one as <c>POST /api/v1/knowledge-bases</c> creates it.</summary>
    private async Task<KnowledgeBase> EnsureKnowledgeBaseAsync(
        AppDbContext dbContext,
        Guid organizationId,
        Guid ownerAccountId,
        EvalKnowledgeBase entry,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.KnowledgeBases
            .Where(knowledgeBase => knowledgeBase.OwnerAccountId == ownerAccountId && knowledgeBase.Name == entry.Name)
            .OrderBy(knowledgeBase => knowledgeBase.CreatedAt)
            .ThenBy(knowledgeBase => knowledgeBase.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = _clock.GetUtcNow();
        var created = KnowledgeBase.Create(organizationId, ownerAccountId, entry.Name, entry.Purpose, now);
        dbContext.KnowledgeBases.Add(created);
        dbContext.KnowledgeActivities.Add(KnowledgeActivity.KnowledgeBaseCreated(created, ownerAccountId, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Created knowledge base {KnowledgeBaseName} for the knowledge set.", entry.Name);
        return created;
    }

    /// <summary>Uploads the versions of <paramref name="document"/> that the knowledge base does
    /// not have yet; returns every version's id in the set's order.</summary>
    private async Task<(IReadOnlyList<(Guid? Id, bool Uploaded)> Versions, string? Skipped)> UploadAsync(
        AppDbContext dbContext,
        KnowledgeBase knowledgeBase,
        Guid ownerAccountId,
        EvalDocument document,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var hashes = document.Versions.Select(version => version.Sha256).ToList();
        var existing = await dbContext.KnowledgeDocumentVersions
            .AsNoTracking()
            .Where(version => version.KnowledgeBaseId == knowledgeBase.Id && hashes.Contains(version.Sha256))
            .Select(version => new { version.Id, version.Sha256, version.DocumentId })
            .ToListAsync(cancellationToken);

        // Tracked: KnowledgeDocumentVersion.Create links a new version to it (see UploadVersionAsync).
        var stored = await dbContext.KnowledgeDocuments
            .SingleOrDefaultAsync(candidate => candidate.KnowledgeBaseId == knowledgeBase.Id && candidate.Name == document.Name, cancellationToken);
        var skipped =
            stored is not null && !existing.Any(version => version.DocumentId == stored.Id)
                ? $"「{knowledgeBase.Name}」已經有一份名為「{document.Name}」的文件，但它的版本都不是題庫的檔案"
            : existing.Any(version => version.DocumentId != stored?.Id)
                ? $"「{knowledgeBase.Name}」的另一份文件已經有「{document.Name}」某個版本的內容"
            : null;
        if (skipped is not null)
        {
            _logger.LogWarning("Skipped document {DocumentName} in {KnowledgeBaseName}: {Reason}.", document.Name, knowledgeBase.Name, skipped);
            return ([.. document.Versions.Select(_ => ((Guid?)null, false))], skipped + "，所以不動它。");
        }

        var versions = new List<(Guid? Id, bool Uploaded)>();
        foreach (var entry in document.Versions)
        {
            if (existing.FirstOrDefault(version => version.Sha256 == entry.Sha256) is { } found)
            {
                versions.Add((found.Id, false));
                continue;
            }

            // Checked when the set was loaded; checked again, as an upload is, in case the limit is lower here.
            var inspection = KnowledgeUploadRules.Inspect(entry.FileName, entry.Content, _options.MaxFileBytes);
            if (!inspection.IsAccepted)
            {
                throw new InvalidOperationException($"「{entry.File}」上傳時被拒絕：{inspection.Rejection.Message}");
            }

            var file = inspection.Value;
            var now = _clock.GetUtcNow();
            KnowledgeDocumentVersion version;
            if (stored is null)
            {
                // POST .../documents (KnowledgeDocumentEndpoints.UploadAsync).
                if (await KnowledgeDocumentEndpoints.FindDuplicateAsync(dbContext, knowledgeBase.Id, file, cancellationToken) is { } duplicate)
                {
                    throw new InvalidOperationException($"「{entry.File}」上傳時被拒絕：{duplicate.Message}");
                }

                stored = KnowledgeDocument.CreateUploaded(knowledgeBase, file.FileName, now);
                version = KnowledgeDocumentVersion.Create(
                    stored, versionNumber: 1, file.FileName, file.ContentType, file.SizeBytes, file.Sha256, ownerAccountId, batchId, now);
                dbContext.KnowledgeDocuments.Add(stored);
                dbContext.KnowledgeDocumentVersions.Add(version);
                dbContext.KnowledgeActivities.Add(KnowledgeActivity.DocumentUploaded(version, ownerAccountId, now));
            }
            else
            {
                // POST .../documents/{docId}/versions (KnowledgeDocumentEndpoints.UploadVersionAsync); the
                // content is new to the knowledge base (it was looked up above).
                var latest = await dbContext.KnowledgeDocumentVersions
                    .Where(candidate => candidate.DocumentId == stored.Id)
                    .MaxAsync(candidate => (int?)candidate.VersionNumber, cancellationToken);
                version = KnowledgeDocumentVersion.Create(
                    stored, (latest ?? 0) + 1, file.FileName, file.ContentType, file.SizeBytes, file.Sha256, ownerAccountId, batchId, now);
                dbContext.KnowledgeDocumentVersions.Add(version);
                dbContext.KnowledgeActivities.Add(KnowledgeActivity.VersionUploaded(version, ownerAccountId, now));
            }

            dbContext.KnowledgeFileContents.Add(new KnowledgeFileContent(version, entry.Content));
            dbContext.BackgroundJobs.Add(KnowledgeDocumentEndpoints.EnqueueProcessing(version, now));
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Uploaded {FileName} as version {VersionNumber} of {DocumentName} in {KnowledgeBaseName}.",
                file.FileName,
                version.VersionNumber,
                stored.Name,
                knowledgeBase.Name);
            versions.Add((version.Id, true));
        }

        return (versions, null);
    }

    /// <summary>Runs the job queue until every version in <paramref name="versionIds"/> is
    /// processed (<c>ready</c>, <c>partially-readable</c> or <c>failed</c>) or the timeout passed.</summary>
    private async Task ProcessAsync(Guid organizationId, IReadOnlyList<Guid> versionIds, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow() + timeout;
        while (true)
        {
            await _runner.RunUntilIdleAsync(cancellationToken);

            int unfinished;
            await using (var dbContext = ForOrganization(organizationId))
            {
                unfinished = await dbContext.KnowledgeDocumentVersions
                    .CountAsync(
                        version => versionIds.Contains(version.Id)
                            && (version.ProcessingStatus == KnowledgeDocumentStatus.Queued || version.ProcessingStatus == KnowledgeDocumentStatus.Processing),
                        cancellationToken);
            }

            if (unfinished == 0 || _clock.GetUtcNow() >= deadline)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>Approves the document's processed versions in the set's order (each its own
    /// save, so each takes effect after the one before), as <c>POST .../versions/approve</c>
    /// does. Stops at a version still being processed, so that a later version never goes into
    /// effect before an earlier one; a failed version is passed over.</summary>
    private async Task<IReadOnlyList<ImportedVersion>> ApproveAsync(
        AppDbContext dbContext,
        Guid ownerAccountId,
        string knowledgeBaseName,
        EvalDocument document,
        IReadOnlyList<(Guid? Id, bool Uploaded)> versions,
        string? skipped,
        CancellationToken cancellationToken)
    {
        if (skipped is not null)
        {
            return [.. versions.Select((version, index) => new ImportedVersion(
                knowledgeBaseName, document.Name, index + 1, null, false, false, null, null, null, skipped))];
        }

        var ids = versions.Select(version => version.Id!.Value).ToList();

        // Tracked: approved in place, ReviewState and ProcessingStatus being concurrency tokens.
        var stored = await dbContext.KnowledgeDocumentVersions
            .Where(version => ids.Contains(version.Id))
            .ToDictionaryAsync(version => version.Id, cancellationToken);
        var approved = new HashSet<Guid>();
        var blocked = false;
        for (var index = 0; index < ids.Count; index++)
        {
            var version = stored[ids[index]];
            var laterApproved = ids.Skip(index + 1).Any(id => stored[id].ReviewState == KnowledgeReviewState.Approved);
            if (blocked || laterApproved || !version.CanBeApproved)
            {
                blocked |= version.ReviewState == KnowledgeReviewState.PendingReview
                    && version.ProcessingStatus is KnowledgeDocumentStatus.Queued or KnowledgeDocumentStatus.Processing;
                continue;
            }

            // The same refusal rules as the endpoint; CanBeApproved already implies they pass.
            if (KnowledgeReviewRules.ApprovalRefusal(version) is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            var now = _clock.GetUtcNow();
            version.Approve(ownerAccountId, now, now);
            dbContext.KnowledgeActivities.Add(KnowledgeActivity.VersionApproved(version, ownerAccountId, now));
            await dbContext.SaveChangesAsync(cancellationToken);
            approved.Add(version.Id);
        }

        return [.. ids.Select((id, index) => new ImportedVersion(
            knowledgeBaseName,
            document.Name,
            index + 1,
            id,
            versions[index].Uploaded,
            approved.Contains(id),
            stored[id].ProcessingStatus,
            stored[id].ReviewState,
            stored[id].Issue))];
    }
}
