using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// Which kind of question a saved test case (M3.5 plan §4, issue #123) is meant to exercise.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantTestCaseCategory>))]
public enum AssistantTestCaseCategory
{
    /// <summary>常見：a question a real user is likely to ask.</summary>
    [JsonStringEnumMemberName("common")]
    Common,

    /// <summary>例外：an edge case (ambiguous phrasing, a follow-up, etc.).</summary>
    [JsonStringEnumMemberName("exception")]
    Exception,

    /// <summary>應拒答：a question the assistant should refuse (out of scope, no data).</summary>
    [JsonStringEnumMemberName("should-refuse")]
    ShouldRefuse,
}
