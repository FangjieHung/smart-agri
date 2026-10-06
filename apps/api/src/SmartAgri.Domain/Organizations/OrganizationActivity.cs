using System.Text.Json;

namespace SmartAgri.Domain.Organizations;

/// <summary>
/// One entry in an organization's activity log (table <c>OrganizationActivities</c>, M6 plan §3 E,
/// §4): an organization-level setting changed, who changed it and when. Only ever added — there is
/// no update or delete. Shared by M6 (chat model, retention) and M7 (case teams, case types).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ActorAccountId"/> has no foreign key, like <c>KnowledgeActivity</c>'s: the row keeps
/// describing what happened whatever later becomes of the account (it is <see langword="null"/> for
/// a system action, e.g. a retention cleanup).
/// </para>
/// <para>
/// <see cref="Detail"/> holds ids, numbers, times and display names only — never conversation
/// content or anything else a person wrote.
/// </para>
/// </remarks>
public sealed class OrganizationActivity : IOrganizationScoped
{
    private static readonly JsonSerializerOptions DetailJson = new(JsonSerializerDefaults.Web);

    /// <summary>For EF Core materialization.</summary>
    private OrganizationActivity()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public OrganizationActivityAction Action { get; private set; }

    /// <summary>The account that acted; <see langword="null"/> for a system action.</summary>
    public Guid? ActorAccountId { get; private set; }

    public DateTimeOffset At { get; private set; }

    /// <summary>JSON (<c>jsonb</c>) describing the change, or <see langword="null"/>.</summary>
    public string? Detail { get; private set; }

    /// <summary>
    /// A new row. <paramref name="detail"/> is serialized with the web defaults (camelCase); pass an
    /// anonymous object or a record of ids, numbers, times and display names — nothing a person
    /// wrote. Add it to <c>AppDbContext.OrganizationActivities</c> in the same save as the change it
    /// describes.
    /// </summary>
    public static OrganizationActivity Record(
        Guid organizationId,
        OrganizationActivityAction action,
        Guid? actorAccountId,
        DateTimeOffset at,
        object? detail = null)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An activity needs its organization.", nameof(organizationId));
        }

        if (actorAccountId == Guid.Empty)
        {
            throw new ArgumentException("An actor id must not be empty (use null for a system action).", nameof(actorAccountId));
        }

        return new OrganizationActivity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Action = action,
            ActorAccountId = actorAccountId,
            At = at,
            Detail = detail is null ? null : JsonSerializer.Serialize(detail, detail.GetType(), DetailJson),
        };
    }

    /// <summary>
    /// <see cref="OrganizationActivityAction.ChatModelChanged"/>: detail
    /// <c>{ "from": { "id", "displayName" }, "to": { "id", "displayName" } }</c>. An <c>id</c> of
    /// <see langword="null"/> is the deployment default; <c>displayName</c> is the name the model had
    /// in the list at the time (<see langword="null"/> when it was no longer offered).
    /// </summary>
    public static OrganizationActivity ChatModelChanged(
        Guid organizationId,
        Guid actorAccountId,
        DateTimeOffset at,
        ChatModelChoice from,
        ChatModelChoice to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return Record(organizationId, OrganizationActivityAction.ChatModelChanged, actorAccountId, at, new { from, to });
    }

    /// <summary>
    /// <see cref="OrganizationActivityAction.RetentionChanged"/>: detail <c>{ "from", "to",
    /// "effectiveAt" }</c>, days with <see langword="null"/> for forever; <c>effectiveAt</c> is when
    /// <c>to</c> applies (<paramref name="at"/> when it applies at once).
    /// </summary>
    public static OrganizationActivity RetentionChanged(
        Guid organizationId, Guid actorAccountId, DateTimeOffset at, int? from, int? to, DateTimeOffset effectiveAt) =>
        Record(organizationId, OrganizationActivityAction.RetentionChanged, actorAccountId, at, new { from, to, effectiveAt });

    /// <summary>
    /// <see cref="OrganizationActivityAction.RetentionChangeCancelled"/>: detail <c>{ "days",
    /// "cancelledDays", "cancelledEffectiveAt" }</c> — the retention that stays, and the pending one
    /// dropped.
    /// </summary>
    public static OrganizationActivity RetentionChangeCancelled(
        Guid organizationId, Guid actorAccountId, DateTimeOffset at, int? days, int cancelledDays, DateTimeOffset cancelledEffectiveAt) =>
        Record(organizationId, OrganizationActivityAction.RetentionChangeCancelled, actorAccountId, at, new { days, cancelledDays, cancelledEffectiveAt });

    /// <summary><see cref="OrganizationActivityAction.RetentionTookEffect"/> (system): detail
    /// <c>{ "from", "to" }</c>.</summary>
    public static OrganizationActivity RetentionTookEffect(Guid organizationId, DateTimeOffset at, RetentionSwitch change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Record(organizationId, OrganizationActivityAction.RetentionTookEffect, actorAccountId: null, at, new { from = change.From, to = change.To });
    }

    /// <summary>
    /// <see cref="OrganizationActivityAction.RetentionCleanup"/> (system): detail <c>{ "days",
    /// "cutoff", "threadCount", "answerOutcomeCount" }</c>. Only for a cleanup that deleted something.
    /// </summary>
    public static OrganizationActivity RetentionCleanup(
        Guid organizationId, DateTimeOffset at, int days, DateTimeOffset cutoff, int threadCount, int answerOutcomeCount)
    {
        if (threadCount < 0 || answerOutcomeCount < 0 || threadCount + answerOutcomeCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threadCount), "A cleanup is recorded only when it deleted something.");
        }

        return Record(
            organizationId, OrganizationActivityAction.RetentionCleanup, actorAccountId: null, at,
            new { days, cutoff, threadCount, answerOutcomeCount });
    }

    /// <summary><see cref="OrganizationActivityAction.CaseGroupCreated"/>: detail <c>{ "id", "name" }</c>.</summary>
    public static OrganizationActivity CaseGroupCreated(Guid organizationId, Guid actorAccountId, DateTimeOffset at, Guid groupId, string name) =>
        Record(organizationId, OrganizationActivityAction.CaseGroupCreated, actorAccountId, at, new { id = groupId, name });

    /// <summary><see cref="OrganizationActivityAction.CaseGroupRenamed"/>: detail <c>{ "id", "name",
    /// "previousName" }</c>.</summary>
    public static OrganizationActivity CaseGroupRenamed(
        Guid organizationId, Guid actorAccountId, DateTimeOffset at, Guid groupId, string name, string previousName) =>
        Record(organizationId, OrganizationActivityAction.CaseGroupRenamed, actorAccountId, at, new { id = groupId, name, previousName });

    /// <summary><see cref="OrganizationActivityAction.CaseGroupArchived"/> or
    /// <see cref="OrganizationActivityAction.CaseGroupUnarchived"/>: detail <c>{ "id", "name" }</c>.</summary>
    public static OrganizationActivity CaseGroupArchiveChanged(
        Guid organizationId, Guid actorAccountId, DateTimeOffset at, Guid groupId, string name, bool archived) =>
        Record(
            organizationId,
            archived ? OrganizationActivityAction.CaseGroupArchived : OrganizationActivityAction.CaseGroupUnarchived,
            actorAccountId,
            at,
            new { id = groupId, name });

    /// <summary><see cref="OrganizationActivityAction.CaseTypeCreated"/>: detail <c>{ "id", "name" }</c>.</summary>
    public static OrganizationActivity CaseTypeCreated(Guid organizationId, Guid actorAccountId, DateTimeOffset at, Guid typeId, string name) =>
        Record(organizationId, OrganizationActivityAction.CaseTypeCreated, actorAccountId, at, new { id = typeId, name });

    /// <summary>
    /// <see cref="OrganizationActivityAction.CaseTypeUpdated"/>: detail <c>{ "id", "name", "changed",
    /// "isActive" }</c>; <paramref name="changed"/> lists the wire names of the fields that changed
    /// (at least one). The description itself is something a person wrote, so only the fact that it
    /// changed is recorded.
    /// </summary>
    public static OrganizationActivity CaseTypeUpdated(
        Guid organizationId, Guid actorAccountId, DateTimeOffset at, Guid typeId, string name, IReadOnlyList<string> changed, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (changed.Count == 0)
        {
            throw new ArgumentException("An update is recorded only when something changed.", nameof(changed));
        }

        return Record(organizationId, OrganizationActivityAction.CaseTypeUpdated, actorAccountId, at, new { id = typeId, name, changed, isActive });
    }
}

/// <summary>One side of a <see cref="OrganizationActivityAction.ChatModelChanged"/> detail.</summary>
/// <param name="Id">The organization's <see cref="Organization.ChatModelId"/>; <see langword="null"/>
/// for the deployment default.</param>
/// <param name="DisplayName">The model's name in the deployment's list at the time.</param>
public sealed record ChatModelChoice(string? Id, string? DisplayName);
