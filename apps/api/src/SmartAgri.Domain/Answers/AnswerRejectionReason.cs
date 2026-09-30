using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Answers;

/// <summary>
/// Why an <see cref="AnswerOutcome"/>'s reply became <c>no-result</c>; <see langword="null"/>
/// otherwise. A persistence-layer mirror of
/// <c>SmartAgri.Application.Answers.GroundedRejectionReason</c> (Domain may not reference
/// Application) — the reason only, never the question or the model's text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnswerRejectionReason>))]
public enum AnswerRejectionReason
{
    [JsonStringEnumMemberName("below-threshold")]
    BelowThreshold,

    [JsonStringEnumMemberName("citation-out-of-range")]
    CitationOutOfRange,

    [JsonStringEnumMemberName("no-citation")]
    NoCitation,

    [JsonStringEnumMemberName("cannot-answer")]
    CannotAnswer,

    [JsonStringEnumMemberName("empty-answer")]
    EmptyAnswer,
}
