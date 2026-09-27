using Microsoft.Extensions.VectorData;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge.Retrieval;

/// <summary>One search: a question, where to look and on whose behalf.</summary>
/// <param name="Question">Sent to the embedding model as is (after its query prefix); callers
/// validate and trim it (<see cref="KnowledgeRetrievalRules.ValidatePreview"/> for the
/// preview).</param>
/// <param name="KnowledgeBaseIds">The knowledge bases to search: one for the preview, an
/// assistant's connected ones in M3. Deciding that the caller may search them is the caller's
/// job; ids of another organization simply match nothing.</param>
/// <param name="AccountId">Who asked — recorded on the question's <c>ModelInvocation</c>.</param>
/// <param name="AssistantId">For which assistant (M3), recorded likewise.</param>
/// <param name="IncludePending">Also each document's newest pending version
/// (<see cref="RetrievableChunks.RuleIncludingPending"/>): the retrieval preview only, never
/// for answering.</param>
/// <param name="Top">Passages to return; <see langword="null"/> for the deployment's
/// <see cref="KnowledgeRetrievalSettings.Top"/>.</param>
/// <param name="MinScore">The relevance threshold; <see langword="null"/> for the deployment's
/// <see cref="KnowledgeRetrievalSettings.MinScore"/> (M3: the assistant's own, if tuned).</param>
public sealed record KnowledgeRetrievalQuery(
    string Question,
    IReadOnlyCollection<Guid> KnowledgeBaseIds,
    Guid? AccountId,
    Guid? AssistantId = null,
    bool IncludePending = false,
    int? Top = null,
    double? MinScore = null);

/// <summary>One retrieved passage and what to cite it as.</summary>
/// <param name="Text">The chunk's whole text (what an answer is grounded on; the preview shows
/// an excerpt of it).</param>
/// <param name="LocationLabel">「第 2 頁」, a heading path, or worksheet rows.</param>
/// <param name="VersionState"><see cref="KnowledgeVersionState.Effective"/>, or
/// <see cref="KnowledgeVersionState.PendingReview"/> for a pending version's passage
/// (<see cref="KnowledgeRetrievalQuery.IncludePending"/> only).</param>
/// <param name="Score">Cosine similarity to the question: higher is closer, 1 is identical.</param>
public sealed record RetrievedKnowledgePassage(
    Guid ChunkId,
    Guid KnowledgeBaseId,
    Guid DocumentId,
    string DocumentName,
    Guid VersionId,
    int VersionNumber,
    KnowledgeVersionState VersionState,
    string LocationLabel,
    string Text,
    double Score);

/// <summary>The passages found, closest first, and the threshold they were judged by.</summary>
/// <param name="Passages">The top passages whatever their score, so a person can see how close
/// the nearest ones came even when none is relevant.</param>
/// <param name="Threshold">The relevance threshold this search used.</param>
public sealed record KnowledgeRetrievalResult(IReadOnlyList<RetrievedKnowledgePassage> Passages, double Threshold)
{
    /// <summary>
    /// No passage reaches <see cref="Threshold"/> (including: nothing was found). An assistant
    /// that may only use the organization's data then answers 「查無結果」 without calling a
    /// model (grounded-answers ADR).
    /// </summary>
    public bool BelowThreshold => !Passages.Any(passage => passage.Score >= Threshold);

    /// <summary>The passages at or above <see cref="Threshold"/>, closest first: what an
    /// answer may be grounded on (M3).</summary>
    public IReadOnlyList<RetrievedKnowledgePassage> Relevant => [.. Passages.Where(passage => passage.Score >= Threshold)];
}

/// <summary>
/// <b>The</b> retrieval entry point (M2 plan Slice 9; ticket #43): the retrieval preview uses it
/// now, and M3's conversations will call the same service. It embeds the question (one
/// <c>ModelInvocation</c>, purpose <see cref="ModelInvocationPurpose.EmbedQuery"/>, attributed
/// to the account and assistant given), searches the retrievable chunks of the given knowledge
/// bases (<see cref="RetrievableChunks.InKnowledgeBases"/>: current effective versions,
/// not excluded, document not disabled, the configured model) by exact cosine similarity, and
/// returns the top passages with their document, version and location, judged against the
/// relevance threshold. It never calls a generation model.
/// </summary>
/// <remarks>
/// <para>
/// Scoped with the <see cref="VectorStoreCollection{TKey,TRecord}"/> and the embedding
/// generator: everything runs in the scope's organization, so a search can never reach another
/// organization's chunks, whatever ids it is given. Whether the caller may search those
/// knowledge bases (the owner, or an assistant connected to them) is the caller's check.
/// </para>
/// <para>
/// Failures: <see cref="KnowledgeEmbeddingException"/> when the question cannot be embedded
/// (no provider, or the provider failed); nothing is searched then. A score below the threshold
/// is not a failure: see <see cref="KnowledgeRetrievalResult.BelowThreshold"/>.
/// </para>
/// </remarks>
public sealed class KnowledgeRetriever
{
    private readonly KnowledgeChunkEmbedder _embedder;
    private readonly VectorStoreCollection<Guid, KnowledgeChunk> _chunks;
    private readonly IKnowledgeVersionSources _sources;
    private readonly KnowledgeRetrievalSettings _settings;
    private readonly TimeProvider _clock;

    public KnowledgeRetriever(
        KnowledgeChunkEmbedder embedder,
        VectorStoreCollection<Guid, KnowledgeChunk> chunks,
        IKnowledgeVersionSources sources,
        KnowledgeRetrievalSettings settings,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        _embedder = embedder;
        _chunks = chunks;
        _sources = sources;
        _settings = settings;
        _clock = clock;
    }

    /// <summary>The deployment's defaults.</summary>
    public KnowledgeRetrievalSettings Settings => _settings;

    /// <summary>Searches as <paramref name="query"/> says. With no knowledge base to search,
    /// returns nothing without calling the model.</summary>
    /// <exception cref="KnowledgeEmbeddingException">The question could not be embedded.</exception>
    public async Task<KnowledgeRetrievalResult> RetrieveAsync(KnowledgeRetrievalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Question);
        ArgumentNullException.ThrowIfNull(query.KnowledgeBaseIds);
        var top = query.Top ?? _settings.Top;
        var threshold = query.MinScore ?? _settings.MinScore;
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1, nameof(query));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(top, KnowledgeRetrievalSettings.MaxTop, nameof(query));
        if (threshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), threshold, "A minimum score is a cosine similarity of 0-1.");
        }

        if (query.KnowledgeBaseIds.Count == 0)
        {
            return new KnowledgeRetrievalResult([], threshold);
        }

        var now = _clock.GetUtcNow();
        var vector = await _embedder.EmbedQueryAsync(query.Question, query.AccountId, query.AssistantId, cancellationToken);

        var options = new VectorSearchOptions<KnowledgeChunk>
        {
            Filter = RetrievableChunks.InKnowledgeBases(query.KnowledgeBaseIds, now, _embedder.Model, query.IncludePending),
        };
        var hits = new List<VectorSearchResult<KnowledgeChunk>>(top);
        await foreach (var hit in _chunks.SearchAsync(vector, top, options, cancellationToken))
        {
            hits.Add(hit);
        }

        if (hits.Count == 0)
        {
            return new KnowledgeRetrievalResult([], threshold);
        }

        var sources = await _sources.FindAsync([.. hits.Select(hit => hit.Record.VersionId).Distinct()], now, cancellationToken);

        // A version deleted between the two reads has nothing left to cite: its passage is dropped.
        var passages = hits
            .Where(hit => sources.ContainsKey(hit.Record.VersionId))
            .Select(hit =>
            {
                var chunk = hit.Record;
                var source = sources[chunk.VersionId];
                return new RetrievedKnowledgePassage(
                    chunk.Id,
                    chunk.KnowledgeBaseId,
                    chunk.DocumentId,
                    source.DocumentName,
                    chunk.VersionId,
                    source.VersionNumber,
                    source.State,
                    chunk.LocationLabel,
                    chunk.Text,
                    hit.Score ?? throw new InvalidOperationException("The vector collection returned a result without a score."));
            })
            .ToList();
        return new KnowledgeRetrievalResult(passages, threshold);
    }
}
