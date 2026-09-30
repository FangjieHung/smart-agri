using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>Where an <see cref="AssistantTestRun"/> is (M3.5 plan §4): <see cref="Queued"/> →
/// <see cref="Running"/> → <see cref="Completed"/> or <see cref="Failed"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantTestRunStatus>))]
public enum AssistantTestRunStatus
{
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("running")]
    Running,

    /// <summary>Every test case was answered and judged (passing or not).</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>The run could not answer its test cases (e.g. the model was unavailable); it
    /// has no results.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}
