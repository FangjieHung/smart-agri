using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>Where an <see cref="AssistantIssue"/> came from (M3.5 plan §3/§4). New sources are
/// added as new members: the column stores the wire name, so adding one needs no data
/// migration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantIssueSource>))]
public enum AssistantIssueSource
{
    /// <summary>Created by the assistant's owner from a failed <see cref="AssistantTestResult"/>
    /// (issue #126).</summary>
    [JsonStringEnumMemberName("test-failure")]
    TestFailure,

    /// <summary>A member forwarded one question and reply from their own conversation
    /// (「轉給專人」, issue #127; created by <c>AssistantHandoffEndpoints</c> in the API).</summary>
    [JsonStringEnumMemberName("handoff")]
    Handoff,
}
