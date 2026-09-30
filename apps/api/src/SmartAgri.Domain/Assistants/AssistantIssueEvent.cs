using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// One entry of an <see cref="AssistantIssue"/>'s handling history (處理紀錄, M3.5 plan §4).
/// Created only by <see cref="AssistantIssue"/>'s own methods, which number the entries
/// (<see cref="Ordinal"/>) so the history has a stable order even when one change records
/// several entries at the same instant.
/// </summary>
public sealed class AssistantIssueEvent : IOrganizationScoped
{
    public const int NoteMaxLength = 2000;

    /// <summary>For EF Core materialization.</summary>
    private AssistantIssueEvent()
    {
    }

    internal AssistantIssueEvent(
        AssistantIssue issue,
        int ordinal,
        AssistantIssueEventAction action,
        Guid actorAccountId,
        DateTimeOffset at,
        string? note)
    {
        if (actorAccountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(actorAccountId));
        }

        Id = Guid.CreateVersion7();
        OrganizationId = issue.OrganizationId;
        IssueId = issue.Id;
        Ordinal = ordinal;
        Action = action;
        ActorAccountId = actorAccountId;
        At = at;
        Note = note;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid IssueId { get; private set; }

    /// <summary>1-based position in the issue's history.</summary>
    public int Ordinal { get; private set; }

    public AssistantIssueEventAction Action { get; private set; }

    public Guid ActorAccountId { get; private set; }

    public DateTimeOffset At { get; private set; }

    public string? Note { get; private set; }

    /// <summary>For <see cref="AssistantIssueEventAction.Assigned"/> (and
    /// <see cref="AssistantIssueEventAction.Created"/> with an assignee): the new assignee.</summary>
    public Guid? AssigneeAccountId { get; internal set; }

    /// <summary>For <see cref="AssistantIssueEventAction.StatusChanged"/> and
    /// <see cref="AssistantIssueEventAction.Created"/>: the status afterwards.</summary>
    public AssistantIssueStatus? Status { get; internal set; }

    /// <summary>For <see cref="AssistantIssueEventAction.DueDateChanged"/> (and
    /// <see cref="AssistantIssueEventAction.Created"/> with a due date): the new due date.</summary>
    public DateTimeOffset? DueAt { get; internal set; }
}
