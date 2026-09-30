using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>Why an <see cref="AssistantTestResult"/> did not pass (M3.5 plan §3/§4; judged by
/// <c>SmartAgri.Application.Assistants.AssistantTestJudge</c> from structure only, never from
/// the model's text).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantTestFailureReason>))]
public enum AssistantTestFailureReason
{
    /// <summary>The reply kind was not the expected one.</summary>
    [JsonStringEnumMemberName("kind-mismatch")]
    KindMismatch,

    /// <summary>A <c>company-data</c> reply, as expected, but it did not cite every expected
    /// document.</summary>
    [JsonStringEnumMemberName("missing-document")]
    MissingDocument,
}
