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

    // --- The action table (M7 plan §3 D, issue #249) ---------------------------------------------
    // Each method checks only the case's own state (open, the right status, the fields' shape) and
    // throws when it is wrong: who may act, the eventCount the screen showed and the target group's
    // existence are the application's (CaseActionRules, the endpoints). Each writes exactly one event.

    /// <summary>受理: 待受理 → 處理中; <paramref name="accountId"/> becomes the case owner (and keeps seeing
    /// the case forever, <c>CaseVisibility</c>: the event records <see cref="CaseEvent.OwnerAccountId"/>).</summary>
    public CaseEvent Accept(Guid accountId, DateTimeOffset now)
    {
        RequireStatus(CaseStatus.Pending);
        RequireId(accountId);
        Status = CaseStatus.InProgress;
        OwnerAccountId = accountId;
        AcceptedAt = now;
        var accepted = Record(CaseEventAction.Accepted, accountId, now, note: null);
        accepted.Status = Status;
        accepted.OwnerAccountId = accountId;
        return accepted;
    }

    /// <summary>待補件: 處理中 → 待補件, with what is needed (required). The due time keeps running.</summary>
    public CaseEvent RequestInfo(Guid actorAccountId, string note, DateTimeOffset now)
    {
        RequireStatus(CaseStatus.InProgress);
        var requested = Record(CaseEventAction.InfoRequested, actorAccountId, now, CheckedText(note, required: true, nameof(note)));
        Status = CaseStatus.AwaitingInfo;
        requested.Status = Status;
        return requested;
    }

    /// <summary>繼續處理 (decision J): 待補件 → 處理中, e.g. the creator answered by phone; the note is optional.</summary>
    public CaseEvent Resume(Guid actorAccountId, string? note, DateTimeOffset now)
    {
        RequireStatus(CaseStatus.AwaitingInfo);
        var resumed = Record(CaseEventAction.Resumed, actorAccountId, now, CheckedText(note, required: false, nameof(note)));
        Status = CaseStatus.InProgress;
        resumed.Status = Status;
        return resumed;
    }

    /// <summary>補充: a note on an open case. With <paramref name="resumes"/> (the creator answering a
    /// 待補件 request) the case also goes back to 處理中, recorded on this one event.</summary>
    public CaseEvent Comment(Guid actorAccountId, string note, bool resumes, DateTimeOffset now)
    {
        RequireOpen();
        if (resumes && Status != CaseStatus.AwaitingInfo)
        {
            throw new InvalidOperationException("Only a comment on a case awaiting information resumes it.");
        }

        var commented = Record(CaseEventAction.Commented, actorAccountId, now, CheckedText(note, required: true, nameof(note)));
        if (resumes)
        {
            Status = CaseStatus.InProgress;
            commented.Status = Status;
        }

        return commented;
    }

    /// <summary>完成: 處理中 or 待補件 → 已完成 with the case owner's 處理結果 (required).</summary>
    public CaseEvent Complete(Guid actorAccountId, string resolution, DateTimeOffset now)
    {
        if (Status is not (CaseStatus.InProgress or CaseStatus.AwaitingInfo))
        {
            throw new InvalidOperationException($"A case in {Status} cannot be completed.");
        }

        var checkedResolution = CheckedText(resolution, required: true, nameof(resolution))!;
        var completed = Record(CaseEventAction.Completed, actorAccountId, now, checkedResolution);
        Status = CaseStatus.Completed;
        Resolution = checkedResolution;
        CompletedAt = now;
        completed.Status = Status;
        return completed;
    }

    /// <summary>取消: any open case → 已取消. Whether a reason is required depends on who cancels
    /// (the creator before acceptance may leave it out, <c>CaseActionRules</c>).</summary>
    public CaseEvent Cancel(Guid actorAccountId, string? reason, DateTimeOffset now)
    {
        RequireOpen();
        var checkedReason = CheckedText(reason, required: false, nameof(reason));
        var cancelled = Record(CaseEventAction.Cancelled, actorAccountId, now, checkedReason);
        Status = CaseStatus.Cancelled;
        CancelReason = checkedReason;
        CancelledAt = now;
        cancelled.Status = Status;
        return cancelled;
    }

    /// <summary>轉組: to another, not archived group of the same organization → 待受理, and the case owner
    /// is cleared (the former owner keeps seeing it through the earlier <c>accepted</c> event).</summary>
    public CaseEvent Transfer(Guid actorAccountId, CaseGroup to, string? note, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(to);
        RequireOpen();
        if (to.OrganizationId != OrganizationId)
        {
            throw new ArgumentException("A case moves only to a group of its own organization.", nameof(to));
        }

        if (to.IsArchived)
        {
            throw new InvalidOperationException("A case cannot move to an archived group.");
        }

        if (to.Id == GroupId)
        {
            throw new InvalidOperationException("A transfer moves the case to another group.");
        }

        var from = GroupId;
        var transferred = Record(CaseEventAction.Transferred, actorAccountId, now, CheckedText(note, required: false, nameof(note)));
        GroupId = to.Id;
        OwnerAccountId = null;
        Status = CaseStatus.Pending;
        transferred.Status = Status;
        transferred.FromGroupId = from;
        transferred.ToGroupId = to.Id;
        return transferred;
    }

    /// <summary>調整時限: a new due time on an open case, never earlier than now (decision H).</summary>
    public CaseEvent SetDue(Guid actorAccountId, DateTimeOffset dueAt, string? note, DateTimeOffset now)
    {
        RequireOpen();
        if (dueAt < now)
        {
            throw new ArgumentOutOfRangeException(nameof(dueAt), dueAt, "A due time cannot be earlier than now.");
        }

        var changed = Record(CaseEventAction.DueChanged, actorAccountId, now, CheckedText(note, required: false, nameof(note)));
        DueAt = dueAt;
        changed.DueAt = dueAt;
        return changed;
    }

    private void RequireOpen()
    {
        if (!Status.IsOpen())
        {
            throw new InvalidOperationException("A closed case is never changed again (open a new case instead).");
        }
    }

    private void RequireStatus(CaseStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"This action needs a case in {expected}, not {Status}.");
        }
    }

    private static void RequireId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(id));
        }
    }

    /// <summary>A note, resolution or reason: trimmed, at most <see cref="CaseEvent.NoteMaxLength"/>;
    /// <see langword="null"/> when blank and not required.</summary>
    private static string? CheckedText(string? text, bool required, string parameterName)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return required ? throw new ArgumentException("This text is required.", parameterName) : null;
        }

        if (trimmed.Length > CaseEvent.NoteMaxLength)
        {
            throw new ArgumentException($"At most {CaseEvent.NoteMaxLength} characters.", parameterName);
        }

        return trimmed;
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
