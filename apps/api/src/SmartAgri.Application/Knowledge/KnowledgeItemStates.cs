using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>How one document (or, later, FAQ entry) shows in its knowledge base's list and
/// counts: its own identity plus the status of the version that represents it.</summary>
public sealed record KnowledgeItemState(
    Guid DocumentId,
    Guid KnowledgeBaseId,
    KnowledgeItemKind Kind,
    string Name,
    DateTimeOffset CreatedAt,
    KnowledgeDocumentStatus Status,
    string? Issue,
    DateTimeOffset UpdatedAt);

/// <summary>
/// <b>The one place</b> that decides which status a document shows — in the detail's
/// <c>documents</c>, and therefore in every <c>statusCounts</c>, <c>documentCount</c> and
/// <c>faqCount</c> (<see cref="KnowledgeBaseTally"/>).
/// </summary>
/// <remarks>
/// Today a document shows its latest version (highest <see cref="KnowledgeDocumentVersion.VersionNumber"/>),
/// because there is only ever version 1. Slice 8 (#42) changes this rule — together with the
/// <c>RetrievableChunks</c> specification — once versions need approval and have effective
/// dates. Written against <see cref="IQueryable{T}"/> only, so EF Core translates it into a
/// single query and unit tests run it in memory.
/// </remarks>
public static class KnowledgeItemStates
{
    public static IQueryable<KnowledgeItemState> Of(
        IQueryable<KnowledgeDocument> documents,
        IQueryable<KnowledgeDocumentVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(versions);

        // Each document joined with its highest-numbered version only: in SQL a join plus a
        // correlated MAX, both served by the (DocumentId, VersionNumber) index. A document
        // without any version — which uploads never leave behind — has no row.
        return
            from document in documents
            join latest in versions on document.Id equals latest.DocumentId
            where latest.VersionNumber == versions
                .Where(version => version.DocumentId == document.Id)
                .Max(version => version.VersionNumber)
            select new KnowledgeItemState(
                document.Id,
                document.KnowledgeBaseId,
                document.Kind,
                document.Name,
                document.CreatedAt,
                latest.ProcessingStatus,
                latest.Issue,
                latest.UpdatedAt);
    }
}

/// <summary>A knowledge base's counts for its summary, from its items' states.</summary>
/// <param name="StatusCounts">Every status, including those with no item.</param>
/// <param name="LastItemUpdate">The latest item change, or <see langword="null"/> when the
/// knowledge base is empty; the summary's <c>updatedAt</c> is the later of this and the
/// knowledge base's own change, as in the frontend mock.</param>
public sealed record KnowledgeBaseTally(
    int DocumentCount,
    int FaqCount,
    IReadOnlyDictionary<KnowledgeDocumentStatus, int> StatusCounts,
    DateTimeOffset? LastItemUpdate)
{
    public static KnowledgeBaseTally Empty { get; } = Of([]);

    /// <summary>Counts every item once: documents and FAQ entries by kind, and both by the
    /// status <see cref="KnowledgeItemStates"/> gave them.</summary>
    public static KnowledgeBaseTally Of(IEnumerable<KnowledgeItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var statusCounts = Enum.GetValues<KnowledgeDocumentStatus>().ToDictionary(status => status, _ => 0);
        var documents = 0;
        var faqs = 0;
        DateTimeOffset? lastUpdate = null;
        foreach (var item in items)
        {
            statusCounts[item.Status]++;
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

        return new KnowledgeBaseTally(documents, faqs, statusCounts, lastUpdate);
    }
}
