using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Cases;

/// <summary>
/// What a new <see cref="Case"/> links to (M7 plan §3 C, decision S). Each link is optional; a pair
/// is either both set or both empty. The thread, record and issue links are plain ids — those rows
/// may be deleted (retention, the assistant's deletion) and each read decides whether the link can
/// still be opened; only <see cref="PreviousCaseId"/> is a foreign key (cases are never deleted).
/// </summary>
public sealed record CaseLinks(
    Guid? ThreadAssistantId = null,
    Guid? ThreadId = null,
    Guid? DatabaseId = null,
    Guid? SubmissionId = null,
    Guid? AssistantIssueId = null,
    Guid? PreviousCaseId = null)
{
    public static readonly CaseLinks None = new();
}

/// <summary>
/// 案件 (case ADR; M7 plan §3 C, §4; issue #248): business work 「接下來誰要做」 with a type, the case
/// group that handles it, a case owner once accepted, a status and a due time. Kept forever; holds
/// only the title and description someone confirmed, never conversation text (plan §1 item 8).
/// </summary>
/// <remarks>
/// <para>
/// The type, group, creator, owner and previous case are same-organization composite foreign keys
/// with <c>Restrict</c>. <see cref="EventCount"/> is a concurrency token, like
/// <c>AssistantIssue</c>'s: M7-4's actions carry the <c>eventCount</c> the screen showed and two
/// concurrent actions cannot both number their event the same.
/// </para>
/// <para>
/// Who may see a case is decided in one place, <c>SmartAgri.Application.Cases.CaseVisibility</c>; the
/// fields here are what it reads (<see cref="CreatedByAccountId"/>, <see cref="GroupId"/>) together
/// with the <see cref="CaseEventAction.Accepted"/> events.
/// </para>
/// </remarks>
public sealed class Case : IOrganizationScoped
{
    public const int TitleMaxLength = 120;

    public const int DescriptionMaxLength = 4000;

    /// <summary>For EF Core materialization.</summary>
    private Case()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid TypeId { get; private set; }

    /// <summary>The current case group (changes on transfer, M7-4).</summary>
    public Guid GroupId { get; private set; }

    public CaseStatus Status { get; private set; }

    /// <summary>Trimmed, 1–<see cref="TitleMaxLength"/> characters.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Trimmed, 0–<see cref="DescriptionMaxLength"/> characters (empty when there is none).</summary>
    public string Description { get; private set; } = string.Empty;

    public CaseOrigin Origin { get; private set; }

    /// <summary>Who created it; <see langword="null"/> only for <see cref="CaseOrigin.DatabaseSubmission"/>
    /// (decision M: the submitter does not become the creator).</summary>
    public Guid? CreatedByAccountId { get; private set; }

    /// <summary>The case owner (案件負責人) since the last accept; cleared by a transfer.</summary>
    public Guid? OwnerAccountId { get; private set; }

    /// <summary>Calendar time (decision H); never earlier than the moment it was set.</summary>
    public DateTimeOffset DueAt { get; private set; }

    /// <summary>The linked thread's assistant (not a foreign key).</summary>
    public Guid? ThreadAssistantId { get; private set; }

    /// <summary>The linked conversation thread (not a foreign key: retention may delete it).</summary>
    public Guid? ThreadId { get; private set; }

    /// <summary>The linked record's database (not a foreign key).</summary>
    public Guid? DatabaseId { get; private set; }

    /// <summary>The linked database record (not a foreign key; a withdrawal keeps the row).</summary>
    public Guid? SubmissionId { get; private set; }

    /// <summary>The 處理事項 this case was opened from (M7-7; not a foreign key).</summary>
    public Guid? AssistantIssueId { get; private set; }

    /// <summary>The closed case this one continues (「另開新案」; a foreign key).</summary>
    public Guid? PreviousCaseId { get; private set; }

    /// <summary>The case owner's 處理結果 on completion (M7-4).</summary>
    public string? Resolution { get; private set; }

    public string? CancelReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>How many <see cref="CaseEvent"/>s it has (concurrency token).</summary>
    public int EventCount { get; private set; }

    /// <summary>
    /// Creates a case in <see cref="CaseStatus.Pending"/> and its <see cref="CaseEventAction.Created"/>
    /// event. The caller has already checked what needs the database: the type is active, the group is
    /// not archived, every link is one the creator may use now.
    /// </summary>
    /// <param name="createdByAccountId">Required for every origin except
    /// <see cref="CaseOrigin.DatabaseSubmission"/>, which must have none.</param>
    /// <param name="title">Already validated and trimmed.</param>
    /// <param name="description">Already validated and trimmed; empty for none.</param>
    /// <param name="dueAt">Not earlier than <paramref name="now"/> (decision H).</param>
    public static (Case Case, CaseEvent Created) Create(
        CaseOrigin origin,
        CaseType type,
        CaseGroup group,
        Guid? createdByAccountId,
        string title,
        string description,
        DateTimeOffset dueAt,
        CaseLinks links,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(links);
        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Not a declared origin.");
        }

        if (type.OrganizationId != group.OrganizationId)
        {
            throw new ArgumentException("A case's type and group belong to the same organization.", nameof(group));
        }

        if (!type.IsActive)
        {
            throw new InvalidOperationException("An inactive case type cannot be used for a new case.");
        }

        if (group.IsArchived)
        {
            throw new InvalidOperationException("A new case cannot go to an archived group.");
        }

        if ((origin == CaseOrigin.DatabaseSubmission) != (createdByAccountId is null))
        {
            throw new ArgumentException(
                "Only a case opened from a database submission has no creator, and it never has one.", nameof(createdByAccountId));
        }

        if (createdByAccountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(createdByAccountId));
        }

        if (dueAt < now)
        {
            throw new ArgumentOutOfRangeException(nameof(dueAt), dueAt, "A due time cannot be earlier than now.");
        }

        RequirePair(links.ThreadAssistantId, links.ThreadId, nameof(links));
        RequirePair(links.DatabaseId, links.SubmissionId, nameof(links));

        var created = new Case
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = type.OrganizationId,
            TypeId = type.Id,
            GroupId = group.Id,
            Status = CaseStatus.Pending,
            Title = CheckedTitle(title),
            Description = CheckedDescription(description),
            Origin = origin,
            CreatedByAccountId = createdByAccountId,
            DueAt = dueAt,
            ThreadAssistantId = links.ThreadAssistantId,
            ThreadId = links.ThreadId,
            DatabaseId = links.DatabaseId,
            SubmissionId = links.SubmissionId,
            AssistantIssueId = links.AssistantIssueId,
            PreviousCaseId = links.PreviousCaseId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var createdEvent = created.Record(CaseEventAction.Created, createdByAccountId, now, note: null);
        createdEvent.Status = created.Status;
        createdEvent.ToGroupId = created.GroupId;
        createdEvent.DueAt = created.DueAt;
        return (created, createdEvent);
    }

    /// <summary>Numbers and returns the next event; every change (M7-4) goes through here.</summary>
    private CaseEvent Record(CaseEventAction action, Guid? actorAccountId, DateTimeOffset now, string? note)
    {
        EventCount++;
        UpdatedAt = now;
        return new CaseEvent(this, EventCount, action, actorAccountId, now, note);
    }

    private static string CheckedTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (title.Length == 0 || title.Length > TitleMaxLength || title.Trim().Length != title.Length)
        {
            throw new ArgumentException($"A case title is trimmed and 1–{TitleMaxLength} characters.", nameof(title));
        }

        return title;
    }

    private static string CheckedDescription(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (description.Length > DescriptionMaxLength || description.Trim().Length != description.Length)
        {
            throw new ArgumentException($"A case description is trimmed and at most {DescriptionMaxLength} characters.", nameof(description));
        }

        return description;
    }

    private static void RequirePair(Guid? first, Guid? second, string parameterName)
    {
        if ((first is null) != (second is null) || first == Guid.Empty || second == Guid.Empty)
        {
            throw new ArgumentException("A link's two ids are both set (and not empty) or both absent.", parameterName);
        }
    }
}
