using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Answers;

/// <summary>
/// The reply kind an <see cref="AnswerOutcome"/> row carries — a persistence-layer mirror of
/// <c>SmartAgri.Application.Answers.GroundedReplyKind</c> (Domain may not reference
/// Application), the same three wire names as <see cref="Chat.ChatReplyKind"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnswerReplyKind>))]
public enum AnswerReplyKind
{
    [JsonStringEnumMemberName("company-data")]
    CompanyData,

    [JsonStringEnumMemberName("general-knowledge")]
    GeneralKnowledge,

    [JsonStringEnumMemberName("no-result")]
    NoResult,
}
