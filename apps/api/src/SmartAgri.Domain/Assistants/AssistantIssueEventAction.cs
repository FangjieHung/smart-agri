using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>What an <see cref="AssistantIssueEvent"/> records (M3.5 plan §4, plus
/// <see cref="DueDateChanged"/> so every change to an issue leaves a record).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantIssueEventAction>))]
public enum AssistantIssueEventAction
{
    [JsonStringEnumMemberName("created")]
    Created,

    /// <summary>The assignee changed; <see cref="AssistantIssueEvent.AssigneeAccountId"/> is the
    /// new one (<see langword="null"/> when unassigned).</summary>
    [JsonStringEnumMemberName("assigned")]
    Assigned,

    /// <summary><see cref="AssistantIssueEvent.Status"/> is the new status.</summary>
    [JsonStringEnumMemberName("status-changed")]
    StatusChanged,

    /// <summary>A note without any other change.</summary>
    [JsonStringEnumMemberName("commented")]
    Commented,

    /// <summary><see cref="AssistantIssueEvent.DueAt"/> is the new due date
    /// (<see langword="null"/> when cleared).</summary>
    [JsonStringEnumMemberName("due-date-changed")]
    DueDateChanged,

    /// <summary>「另開案件」 (M7-7, issue #252): a case was opened from the issue and the issue was
    /// resolved as <see cref="AssistantIssueResolutionKind.NotAssistantIssue"/>;
    /// <see cref="AssistantIssueEvent.Status"/> is <see cref="AssistantIssueStatus.Resolved"/>.</summary>
    [JsonStringEnumMemberName("case-opened")]
    CaseOpened,
}
