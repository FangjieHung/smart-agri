using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// The reply kind a saved test case (M3.5 plan §4, issue #123) expects. Wire names deliberately
/// equal <c>SmartAgri.Application.Answers.GroundedReplyKind</c>'s (<c>company-data</c>／
/// <c>general-knowledge</c>／<c>no-result</c>) so a test run's actual reply can be compared
/// against it without translation; kept as a separate Domain enum (not a reference to
/// <c>GroundedReplyKind</c>) because Domain does not depend on Application.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantTestExpectedKind>))]
public enum AssistantTestExpectedKind
{
    [JsonStringEnumMemberName("company-data")]
    CompanyData,

    [JsonStringEnumMemberName("general-knowledge")]
    GeneralKnowledge,

    [JsonStringEnumMemberName("no-result")]
    NoResult,
}
