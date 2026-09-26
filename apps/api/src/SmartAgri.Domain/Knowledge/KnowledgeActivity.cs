using System.Text.Json;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// One entry in a knowledge base's activity log (table <c>KnowledgeActivities</c>, M2 plan
/// §4): who did what, and when. <see cref="Detail"/> is a small JSON object describing the
/// change; it never contains document content, and not even the knowledge base's own name
/// or purpose.
/// </summary>
/// <remarks>
/// Deliberately has no foreign key to <see cref="KnowledgeBase"/>, documents or versions: a
/// log entry must be able to describe something that no longer exists. Deleting a knowledge
/// base therefore removes its activity rows explicitly and then writes a single
/// <see cref="KnowledgeActivityAction.KnowledgeBaseDeleted"/> row; deleting a document keeps
/// the document's rows and adds a <see cref="KnowledgeActivityAction.DocumentDeleted"/> one.
/// </remarks>
public sealed class KnowledgeActivity : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private KnowledgeActivity()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    /// <summary>The document acted on, if the action concerns one.</summary>
    public Guid? DocumentId { get; private set; }

    /// <summary>The version acted on, if the action concerns one.</summary>
    public Guid? VersionId { get; private set; }

    public KnowledgeActivityAction Action { get; private set; }

    /// <summary>The account that acted; <see langword="null"/> for system actions (later
    /// slices: background processing).</summary>
    public Guid? ActorAccountId { get; private set; }

    public DateTimeOffset At { get; private set; }

    /// <summary>JSON (<c>jsonb</c>) describing the change, or <see langword="null"/>.</summary>
    public string? Detail { get; private set; }

    public static KnowledgeActivity KnowledgeBaseCreated(KnowledgeBase knowledgeBase, Guid actorAccountId, DateTimeOffset at) =>
        New(knowledgeBase, KnowledgeActivityAction.KnowledgeBaseCreated, actorAccountId, at, detail: null);

    /// <param name="changedFields">What <see cref="KnowledgeBase.ChangeDetails"/> returned:
    /// field names only, never the old or new values.</param>
    public static KnowledgeActivity KnowledgeBaseUpdated(
        KnowledgeBase knowledgeBase,
        Guid actorAccountId,
        DateTimeOffset at,
        IReadOnlyList<string> changedFields)
    {
        ArgumentNullException.ThrowIfNull(changedFields);
        return New(
            knowledgeBase,
            KnowledgeActivityAction.KnowledgeBaseUpdated,
            actorAccountId,
            at,
            JsonSerializer.Serialize(new { changed = changedFields }));
    }

    /// <summary>Records <paramref name="knowledgeBase"/>'s sharing as it is after the change.</summary>
    public static KnowledgeActivity SharingChanged(
        KnowledgeBase knowledgeBase,
        Guid actorAccountId,
        DateTimeOffset at,
        IReadOnlyList<Guid> sharedWithAccountIds)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        ArgumentNullException.ThrowIfNull(sharedWithAccountIds);
        return New(
            knowledgeBase,
            KnowledgeActivityAction.SharingChanged,
            actorAccountId,
            at,
            JsonSerializer.Serialize(new
            {
                scope = WireNames<KnowledgeSharingScope>.ToWire(knowledgeBase.SharingScope),
                sharedWithAccountIds,
                allowOriginalDownload = knowledgeBase.AllowOriginalDownload,
            }));
    }

    public static KnowledgeActivity KnowledgeBaseDeleted(KnowledgeBase knowledgeBase, Guid actorAccountId, DateTimeOffset at) =>
        New(knowledgeBase, KnowledgeActivityAction.KnowledgeBaseDeleted, actorAccountId, at, detail: null);

    /// <summary>Ids only: neither the file name nor anything read from the file.</summary>
    public static KnowledgeActivity DocumentUploaded(
        KnowledgeDocumentVersion version,
        Guid actorAccountId,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(version);
        return New(
            version.OrganizationId,
            version.KnowledgeBaseId,
            version.DocumentId,
            version.Id,
            KnowledgeActivityAction.DocumentUploaded,
            actorAccountId,
            at,
            detail: null);
    }

    public static KnowledgeActivity VersionRetried(KnowledgeDocumentVersion version, Guid actorAccountId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(version);
        return New(
            version.OrganizationId,
            version.KnowledgeBaseId,
            version.DocumentId,
            version.Id,
            KnowledgeActivityAction.VersionRetried,
            actorAccountId,
            at,
            detail: null);
    }

    /// <summary>A new version of an existing document; ids only, like
    /// <see cref="DocumentUploaded"/>.</summary>
    public static KnowledgeActivity VersionUploaded(KnowledgeDocumentVersion version, Guid actorAccountId, DateTimeOffset at) =>
        ForVersion(version, KnowledgeActivityAction.VersionUploaded, actorAccountId, at);

    /// <summary>Ids only: when it takes effect is the version's own
    /// <see cref="KnowledgeDocumentVersion.EffectiveFrom"/>, which never changes once set.</summary>
    public static KnowledgeActivity VersionApproved(KnowledgeDocumentVersion version, Guid actorAccountId, DateTimeOffset at) =>
        ForVersion(version, KnowledgeActivityAction.VersionApproved, actorAccountId, at);

    /// <summary>
    /// Also keeps the owner's reason: the document clears it when enabled again, and "why was
    /// this stopped" is what an audit asks afterwards (the business review requires operations
    /// to be traceable). It is the operator's note, not document content.
    /// </summary>
    public static KnowledgeActivity DocumentDisabled(
        KnowledgeDocument document,
        string reason,
        Guid actorAccountId,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return New(
            document.OrganizationId,
            document.KnowledgeBaseId,
            document.Id,
            versionId: null,
            KnowledgeActivityAction.DocumentDisabled,
            actorAccountId,
            at,
            JsonSerializer.Serialize(new { reason }));
    }

    /// <summary>The reason recorded by <see cref="DocumentDisabled"/>; <see langword="null"/> for
    /// every other action.</summary>
    public string? DisableReason()
    {
        if (Action != KnowledgeActivityAction.DocumentDisabled || Detail is null)
        {
            return null;
        }

        using var detail = JsonDocument.Parse(Detail);
        return detail.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
    }

    public static KnowledgeActivity DocumentEnabled(KnowledgeDocument document, Guid actorAccountId, DateTimeOffset at) =>
        ForDocument(document, KnowledgeActivityAction.DocumentEnabled, actorAccountId, at);

    /// <summary>Ids only: the document's name goes with the document.</summary>
    public static KnowledgeActivity DocumentDeleted(KnowledgeDocument document, Guid actorAccountId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(document);
        return New(
            document.OrganizationId,
            document.KnowledgeBaseId,
            document.Id,
            versionId: null,
            KnowledgeActivityAction.DocumentDeleted,
            actorAccountId,
            at,
            detail: null);
    }

    /// <summary>
    /// <see cref="KnowledgeActivityAction.ChunkExcluded"/> or
    /// <see cref="KnowledgeActivityAction.ChunkIncluded"/>, as <paramref name="chunk"/> is now.
    /// The detail is the chunk's id only, never its text.
    /// </summary>
    public static KnowledgeActivity ChunkExclusionChanged(KnowledgeChunk chunk, Guid actorAccountId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return New(
            chunk.OrganizationId,
            chunk.KnowledgeBaseId,
            chunk.DocumentId,
            chunk.VersionId,
            chunk.Excluded ? KnowledgeActivityAction.ChunkExcluded : KnowledgeActivityAction.ChunkIncluded,
            actorAccountId,
            at,
            JsonSerializer.Serialize(new { chunkId = chunk.Id }));
    }

    private static KnowledgeActivity ForVersion(
        KnowledgeDocumentVersion version,
        KnowledgeActivityAction action,
        Guid actorAccountId,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(version);
        return New(version.OrganizationId, version.KnowledgeBaseId, version.DocumentId, version.Id, action, actorAccountId, at, detail: null);
    }

    private static KnowledgeActivity ForDocument(
        KnowledgeDocument document,
        KnowledgeActivityAction action,
        Guid actorAccountId,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(document);
        return New(document.OrganizationId, document.KnowledgeBaseId, document.Id, versionId: null, action, actorAccountId, at, detail: null);
    }

    private static KnowledgeActivity New(
        KnowledgeBase knowledgeBase,
        KnowledgeActivityAction action,
        Guid actorAccountId,
        DateTimeOffset at,
        string? detail)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        return New(knowledgeBase.OrganizationId, knowledgeBase.Id, documentId: null, versionId: null, action, actorAccountId, at, detail);
    }

    private static KnowledgeActivity New(
        Guid organizationId,
        Guid knowledgeBaseId,
        Guid? documentId,
        Guid? versionId,
        KnowledgeActivityAction action,
        Guid actorAccountId,
        DateTimeOffset at,
        string? detail)
    {
        if (actorAccountId == Guid.Empty)
        {
            throw new ArgumentException("An actor id must not be empty.", nameof(actorAccountId));
        }

        return new KnowledgeActivity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            KnowledgeBaseId = knowledgeBaseId,
            DocumentId = documentId,
            VersionId = versionId,
            Action = action,
            ActorAccountId = actorAccountId,
            At = at,
            Detail = detail,
        };
    }
}
