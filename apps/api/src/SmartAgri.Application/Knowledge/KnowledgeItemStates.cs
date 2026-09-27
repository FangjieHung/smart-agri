using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>
/// How one document (or, later, FAQ entry) shows in its knowledge base's list and counts: its
/// own identity, the processing of its latest version (<see cref="Status"/>, <see cref="Issue"/>,
/// <see cref="UpdatedAt"/> — "處理狀態"), and whether it is in effect ("是否已生效").
/// </summary>
/// <param name="LatestVersionState">The latest version's review state (so "只看待確認" and batch
/// approval can work from the list).</param>
/// <param name="EffectiveVersionNumber">The document's current effective version
/// (<see cref="RetrievableChunks.CurrentEffectiveVersion"/>), or <see langword="null"/> when
/// none is: never approved yet, or only scheduled.</param>
/// <param name="Disabled">Whether the owner disabled the document in an emergency.</param>
public sealed record KnowledgeItemState(
    Guid DocumentId,
    Guid KnowledgeBaseId,
    KnowledgeItemKind Kind,
    string Name,
    DateTimeOffset CreatedAt,
    KnowledgeDocumentStatus Status,
    string? Issue,
    DateTimeOffset UpdatedAt,
    Guid LatestVersionId,
    int LatestVersionNumber,
    KnowledgeVersionState LatestVersionState,
    int? EffectiveVersionNumber,
    bool Disabled)
{
    /// <summary>Whether assistants may use the document now: it has a version in effect and is
    /// not disabled — the document-level part of <see cref="RetrievableChunks.Rule"/> (chunks may
    /// still be excluded one by one).</summary>
    public bool InEffect => EffectiveVersionNumber is not null && !Disabled;

    /// <summary>Whether the latest version waits for the owner's approval: processed readable,
    /// not yet approved.</summary>
    public bool AwaitingApproval =>
        LatestVersionState == KnowledgeVersionState.PendingReview
        && Status is KnowledgeDocumentStatus.Ready or KnowledgeDocumentStatus.PartiallyReadable;
}

/// <summary>
/// <b>The one place</b> that decides what a document shows — in the detail's <c>documents</c>,
/// and therefore in every <c>statusCounts</c>, <c>documentCount</c>, <c>faqCount</c> and
/// in-effect count (<see cref="KnowledgeBaseTally"/>).
/// </summary>
/// <remarks>
/// <para>
/// Two things, deliberately side by side (plan Slice 13: "顯示處理狀態的地方，同時顯示是否已生效"):
/// the <b>processing status</b> is the latest version's (highest
/// <see cref="KnowledgeDocumentVersion.VersionNumber"/>), so an upload's progress shows even
/// while an older version stays in effect; <b>in effect</b> comes from the eligibility rule
/// itself (<see cref="RetrievableChunks.CurrentEffectiveVersion"/> at <c>now</c>, and the
/// document not disabled), so the list can never claim a document is in use that retrieval
/// would not serve.
/// </para>
/// <para>
/// Written against <see cref="IQueryable{T}"/> only, so EF Core translates it into a single
/// query (the review state is derived from the columns it selects) and unit tests run it in
/// memory.
/// </para>
/// </remarks>
public static class KnowledgeItemStates
{
    public static IQueryable<KnowledgeItemState> Of(
        IQueryable<KnowledgeDocument> documents,
        IQueryable<KnowledgeDocumentVersion> versions,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(versions);

        var inEffect = versions.Where(RetrievableChunks.CurrentEffectiveVersion(now));

        // Each document joined with its highest-numbered version only (a join plus a correlated
        // MAX, both served by the (DocumentId, VersionNumber) index), and with its version in
        // effect if it has one — at most one per document, so the left join adds no rows. A
        // document without any version, which uploads never leave behind, has no row.
        return
            from document in documents
            join latest in versions on document.Id equals latest.DocumentId
            where latest.VersionNumber == versions
                .Where(version => version.DocumentId == document.Id)
                .Max(version => version.VersionNumber)
            join effective in inEffect on document.Id equals effective.DocumentId into effectiveVersions
            from effective in effectiveVersions.DefaultIfEmpty()
            select new KnowledgeItemState(
                document.Id,
                document.KnowledgeBaseId,
                document.Kind,
                document.Name,
                document.CreatedAt,
                latest.ProcessingStatus,
                latest.Issue,
                latest.UpdatedAt,
                latest.Id,
                latest.VersionNumber,
                KnowledgeVersionStates.Of(latest.ReviewState, latest.EffectiveFrom, effective != null && effective.Id == latest.Id, now),
                effective == null ? null : effective.VersionNumber,
                document.DisabledAt != null);
    }
}

/// <summary>A knowledge base's counts for its summary, from its items' states.</summary>
/// <param name="StatusCounts">Every processing status, including those with no item.</param>
/// <param name="InEffectCount">Items assistants may use now (<see cref="KnowledgeItemState.InEffect"/>).</param>
/// <param name="AwaitingApprovalCount">Items whose latest version waits for approval
/// (<see cref="KnowledgeItemState.AwaitingApproval"/>).</param>
/// <param name="DisabledCount">Items disabled in an emergency.</param>
/// <param name="LastItemUpdate">The latest item change, or <see langword="null"/> when the
/// knowledge base is empty; the summary's <c>updatedAt</c> is the later of this and the
/// knowledge base's own change, as in the frontend mock.</param>
public sealed record KnowledgeBaseTally(
    int DocumentCount,
    int FaqCount,
    IReadOnlyDictionary<KnowledgeDocumentStatus, int> StatusCounts,
    int InEffectCount,
    int AwaitingApprovalCount,
    int DisabledCount,
    DateTimeOffset? LastItemUpdate)
{
    public static KnowledgeBaseTally Empty { get; } = Of([]);

    /// <summary>Counts every item once: documents and FAQ entries by kind, and both by the
    /// processing status and review facts <see cref="KnowledgeItemStates"/> gave them.</summary>
    public static KnowledgeBaseTally Of(IEnumerable<KnowledgeItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var statusCounts = Enum.GetValues<KnowledgeDocumentStatus>().ToDictionary(status => status, _ => 0);
        var documents = 0;
        var faqs = 0;
        var inEffect = 0;
        var awaitingApproval = 0;
        var disabled = 0;
        DateTimeOffset? lastUpdate = null;
        foreach (var item in items)
        {
            statusCounts[item.Status]++;
            inEffect += item.InEffect ? 1 : 0;
            awaitingApproval += item.AwaitingApproval ? 1 : 0;
            disabled += item.Disabled ? 1 : 0;
            if (item.Kind == KnowledgeItemKind.Faq)
            {
                faqs++;
            }
            else
            {
                documents++;
            }

            if (lastUpdate is null || item.UpdatedAt > lastUpdate)
            {
                lastUpdate = item.UpdatedAt;
            }
        }

        return new KnowledgeBaseTally(documents, faqs, statusCounts, inEffect, awaitingApproval, disabled, lastUpdate);
    }
}
