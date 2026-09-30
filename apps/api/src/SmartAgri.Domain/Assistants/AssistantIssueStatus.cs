using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>Where an <see cref="AssistantIssue"/> is (M3.5 plan §3): <see cref="Open"/> →
/// <see cref="InProgress"/> → <see cref="Resolved"/>. Any status may be set from any other
/// (a resolved issue can be reopened).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantIssueStatus>))]
public enum AssistantIssueStatus
{
    [JsonStringEnumMemberName("open")]
    Open,

    [JsonStringEnumMemberName("in-progress")]
    InProgress,

    [JsonStringEnumMemberName("resolved")]
    Resolved,
}
