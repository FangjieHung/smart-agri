using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>How a resolved <see cref="AssistantIssue"/> was resolved (M7 plan §4, decision N; issue
/// #252). Set when it is resolved, cleared when it is reopened, like
/// <see cref="AssistantIssue.ResolutionNote"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantIssueResolutionKind>))]
public enum AssistantIssueResolutionKind
{
    /// <summary>The assistant was fixed (every resolution before M7-7, and any resolution through
    /// <c>PATCH</c>).</summary>
    [JsonStringEnumMemberName("fixed")]
    Fixed,

    /// <summary>「非助理問題」: not the assistant's fault but business work, handed over as a case
    /// (「另開案件」); <see cref="AssistantIssue.LinkedCaseId"/> is that case.</summary>
    [JsonStringEnumMemberName("not-assistant-issue")]
    NotAssistantIssue,
}
