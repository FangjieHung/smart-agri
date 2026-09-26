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
/// Deliberately has no foreign key to <see cref="KnowledgeBase"/> (or, in later slices, to
/// documents and versions): a log entry must be able to describe something that no longer
/// exists. Deleting a knowledge base therefore removes its activity rows explicitly and
/// then writes a single <see cref="KnowledgeActivityAction.KnowledgeBaseDeleted"/> row.
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

    private static KnowledgeActivity New(
        KnowledgeBase knowledgeBase,
        KnowledgeActivityAction action,
        Guid actorAccountId,
        DateTimeOffset at,
        string? detail)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        if (actorAccountId == Guid.Empty)
        {
            throw new ArgumentException("An actor id must not be empty.", nameof(actorAccountId));
        }

        return new KnowledgeActivity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = knowledgeBase.OrganizationId,
            KnowledgeBaseId = knowledgeBase.Id,
            Action = action,
            ActorAccountId = actorAccountId,
            At = at,
            Detail = detail,
        };
    }
}
