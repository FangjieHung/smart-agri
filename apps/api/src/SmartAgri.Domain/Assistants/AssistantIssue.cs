using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// A 處理事項 about one assistant's answer quality (M3.5 plan §3/§4, issue #126): opened from a
/// failed test result (<see cref="AssistantIssueSource.TestFailure"/>) or, from #127, from a
/// member's 「轉給專人」 (<see cref="AssistantIssueSource.Handoff"/>). Assigned to an account
/// holding <c>handle-assistant-issues</c>, moved through <see cref="AssistantIssueStatus"/>, and
/// every change is recorded as an <see cref="AssistantIssueEvent"/>, which the methods here
/// return for the caller to save alongside.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow but generic (plan §7 decision A): the source is a wire-named column, and
/// the source-specific fields are nullable snapshots, so a later source (or merging into a
/// general work item) adds columns rather than reshaping these.
/// </para>
/// <para>
/// Snapshots, not references: <see cref="TestRunId"/>/<see cref="TestResultId"/> are not
/// foreign keys, because runs (and their results) are pruned after
/// <see cref="AssistantTestRun.KeptPerAssistant"/>; <see cref="QuestionSnapshot"/>,
/// <see cref="AnswerSnapshot"/> and <see cref="TestFailureReason"/> keep what the issue is
/// about. For a handoff they will hold the member's consented copy (plan §3) — never a
/// conversation or thread id.
/// </para>
/// <para>
/// <see cref="EventCount"/> is a concurrency token: two concurrent changes cannot both number
/// their event the same.
/// </para>
/// </remarks>
public sealed class AssistantIssue : IOrganizationScoped
{
    public const int TitleMaxLength = 200;

    /// <summary>For EF Core materialization.</summary>
    private AssistantIssue()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid AssistantId { get; private set; }

    public AssistantIssueSource Source { get; private set; }

    public AssistantIssueStatus Status { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public Guid? AssigneeAccountId { get; private set; }

    /// <summary>Who created it: the owner for a test failure, the forwarding member for a
    /// handoff.</summary>
    public Guid? ReporterAccountId { get; private set; }

    public DateTimeOffset? DueAt { get; private set; }

    /// <summary>The test run the failed result belonged to (not a foreign key).</summary>
    public Guid? TestRunId { get; private set; }

    /// <summary>The failed test result (not a foreign key).</summary>
    public Guid? TestResultId { get; private set; }

    public AssistantTestFailureReason? TestFailureReason { get; private set; }

    public bool HandoffUnverified { get; private set; }

    /// <summary>The question: the test case's text for a test failure; the member's consented
    /// question for a handoff.</summary>
    public string? QuestionSnapshot { get; private set; }

    /// <summary>The assistant's answer the issue is about.</summary>
    public string? AnswerSnapshot { get; private set; }

    /// <summary>The note given when it was last resolved; cleared when reopened.</summary>
    public string? ResolutionNote { get; private set; }

    /// <summary>How it was last resolved (M7-7); <see langword="null"/> unless
    /// <see cref="AssistantIssueStatus.Resolved"/>, cleared when reopened.</summary>
    public AssistantIssueResolutionKind? ResolutionKind { get; private set; }

    /// <summary>The case opened from it (「另開案件」, M7-7): set with
    /// <see cref="AssistantIssueResolutionKind.NotAssistantIssue"/>, cleared when reopened. The case
    /// keeps its own link back (<c>Case.AssistantIssueId</c>) either way.</summary>
    public Guid? LinkedCaseId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>When it was (last) resolved; <see langword="null"/> unless
    /// <see cref="AssistantIssueStatus.Resolved"/>.</summary>
    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>How many <see cref="AssistantIssueEvent"/>s it has.</summary>
    public int EventCount { get; private set; }

    /// <summary>Opens an issue from a failed test result. The caller checks the result belongs to
    /// <paramref name="assistantId"/> and that <paramref name="assigneeAccountId"/> may be
    /// assigned. Returns the <c>created</c> event.</summary>
    public static (AssistantIssue Issue, AssistantIssueEvent Created) OpenFromTestFailure(
        Guid assistantId,
        AssistantTestRun run,
        AssistantTestResult result,
        string? title,
        Guid reporterAccountId,
        Guid? assigneeAccountId,
        DateTimeOffset? dueAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(result);
        if (run.AssistantId != assistantId || result.RunId != run.Id || result.OrganizationId != run.OrganizationId)
        {
            throw new ArgumentException("The test result must be one of this assistant's runs.", nameof(result));
        }

        if (result.Passed)
        {
            throw new ArgumentException("Only a failed test result opens an issue.", nameof(result));
        }

        RequireId(reporterAccountId, nameof(reporterAccountId));
        if (assigneeAccountId is { } assignee)
        {
            RequireId(assignee, nameof(assigneeAccountId));
        }

        var issue = new AssistantIssue
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = run.OrganizationId,
            AssistantId = assistantId,
            Source = AssistantIssueSource.TestFailure,
            Status = AssistantIssueStatus.Open,
            Title = NormalizeTitle(title) ?? DefaultTitle(result.QuestionSnapshot),
            AssigneeAccountId = assigneeAccountId,
            ReporterAccountId = reporterAccountId,
            DueAt = dueAt,
            TestRunId = run.Id,
            TestResultId = result.Id,
            TestFailureReason = result.FailureReason,
            QuestionSnapshot = result.QuestionSnapshot,
            AnswerSnapshot = result.AnswerText,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var created = issue.Record(AssistantIssueEventAction.Created, reporterAccountId, now, note: null);
        created.Status = issue.Status;
        created.AssigneeAccountId = assigneeAccountId;
        created.DueAt = dueAt;
        return (issue, created);
    }

    /// <summary>Copies exactly one consented exchange; no conversation identifier is retained.</summary>
    public static (AssistantIssue Issue, AssistantIssueEvent Created) OpenFromHandoff(
        Assistant assistant, Guid reporterAccountId, string question, string answer,
        bool unverified, Guid? assigneeAccountId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        RequireId(reporterAccountId, nameof(reporterAccountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        if (assigneeAccountId is { } assignee) RequireId(assignee, nameof(assigneeAccountId));
        var issue = new AssistantIssue
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = assistant.OrganizationId,
            AssistantId = assistant.Id,
            Source = AssistantIssueSource.Handoff,
            Status = AssistantIssueStatus.Open,
            Title = DefaultTitle(question),
            AssigneeAccountId = assigneeAccountId,
            ReporterAccountId = reporterAccountId,
            QuestionSnapshot = question,
            AnswerSnapshot = answer,
            HandoffUnverified = unverified,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var created = issue.Record(AssistantIssueEventAction.Created, reporterAccountId, now, null);
        created.Status = issue.Status;
        created.AssigneeAccountId = assigneeAccountId;
        return (issue, created);
    }

    /// <summary>Changes the assignee (<see langword="null"/> unassigns); <see langword="null"/>
    /// when it is already <paramref name="assigneeAccountId"/>.</summary>
    public AssistantIssueEvent? Assign(Guid? assigneeAccountId, Guid actorAccountId, DateTimeOffset now)
    {
        if (assigneeAccountId is { } assignee)
        {
            RequireId(assignee, nameof(assigneeAccountId));
        }

        if (AssigneeAccountId == assigneeAccountId)
        {
            return null;
        }

        AssigneeAccountId = assigneeAccountId;
        var assigned = Record(AssistantIssueEventAction.Assigned, actorAccountId, now, note: null);
        assigned.AssigneeAccountId = assigneeAccountId;
        return assigned;
    }

    /// <summary>Changes the due date (<see langword="null"/> clears it); <see langword="null"/>
    /// when unchanged.</summary>
    public AssistantIssueEvent? SetDueAt(DateTimeOffset? dueAt, Guid actorAccountId, DateTimeOffset now)
    {
        if (DueAt == dueAt)
        {
            return null;
        }

        DueAt = dueAt;
        var changed = Record(AssistantIssueEventAction.DueDateChanged, actorAccountId, now, note: null);
        changed.DueAt = dueAt;
        return changed;
    }

    /// <summary>Moves to <paramref name="status"/>, with an optional <paramref name="note"/> on the
    /// event (resolving also keeps it as <see cref="ResolutionNote"/>); <see langword="null"/>
    /// when already there.</summary>
    public AssistantIssueEvent? ChangeStatus(AssistantIssueStatus status, string? note, Guid actorAccountId, DateTimeOffset now)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Not a declared status.");
        }

        if (Status == status)
        {
            return null;
        }

        Status = status;
        if (status == AssistantIssueStatus.Resolved)
        {
            ResolvedAt = now;
            ResolutionNote = NormalizeNote(note);
            ResolutionKind = AssistantIssueResolutionKind.Fixed;
        }
        else
        {
            ResolvedAt = null;
            ResolutionNote = null;
            ResolutionKind = null;
        }

        LinkedCaseId = null;

        var changed = Record(AssistantIssueEventAction.StatusChanged, actorAccountId, now, note);
        changed.Status = status;
        return changed;
    }

    /// <summary>
    /// 「另開案件」 (M7 plan §3 G, decision N; issue #252): resolves the issue as
    /// <see cref="AssistantIssueResolutionKind.NotAssistantIssue"/> linked to
    /// <paramref name="caseId"/> and returns the <see cref="AssistantIssueEventAction.CaseOpened"/>
    /// event. The caller creates the case and saves all of it in one <c>SaveChanges</c>; a resolved
    /// issue cannot open a case.
    /// </summary>
    public AssistantIssueEvent OpenCase(Guid caseId, Guid actorAccountId, DateTimeOffset now)
    {
        RequireId(caseId, nameof(caseId));
        if (Status == AssistantIssueStatus.Resolved)
        {
            throw new InvalidOperationException("A resolved issue cannot open a case.");
        }

        Status = AssistantIssueStatus.Resolved;
        ResolvedAt = now;
        ResolutionNote = null;
        ResolutionKind = AssistantIssueResolutionKind.NotAssistantIssue;
        LinkedCaseId = caseId;
        var opened = Record(AssistantIssueEventAction.CaseOpened, actorAccountId, now, note: null);
        opened.Status = Status;
        return opened;
    }

    /// <summary>Adds a note without changing anything else.</summary>
    public AssistantIssueEvent Comment(string note, Guid actorAccountId, DateTimeOffset now)
    {
        if (NormalizeNote(note) is null)
        {
            throw new ArgumentException("A comment needs a note.", nameof(note));
        }

        return Record(AssistantIssueEventAction.Commented, actorAccountId, now, note);
    }

    private AssistantIssueEvent Record(AssistantIssueEventAction action, Guid actorAccountId, DateTimeOffset now, string? note)
    {
        EventCount++;
        UpdatedAt = now;
        return new AssistantIssueEvent(this, EventCount, action, actorAccountId, now, NormalizeNote(note));
    }

    /// <summary>The question itself, cut to <see cref="TitleMaxLength"/>.</summary>
    private static string DefaultTitle(string question)
    {
        var singleLine = string.Join(' ', question.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return singleLine.Length <= TitleMaxLength ? singleLine : singleLine[..(TitleMaxLength - 1)] + "…";
    }

    private static string? NormalizeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? null : title.Trim();

    private static string? NormalizeNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", parameterName);
        }
    }
}
