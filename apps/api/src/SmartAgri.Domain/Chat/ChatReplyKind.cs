using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Chat;

/// <summary>
/// The reply kind a saved <see cref="ChatMessage"/> carries — a persistence-layer mirror of
/// <c>SmartAgri.Application.Answers.GroundedReplyKind</c> (Domain may not reference
/// Application). #77 maps between the two when it saves an assistant's turn; only these three
/// ever reach storage (M3 non-goals §8: form/consent replies do not exist yet).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatReplyKind>))]
public enum ChatReplyKind
{
    [JsonStringEnumMemberName("company-data")]
    CompanyData,

    [JsonStringEnumMemberName("general-knowledge")]
    GeneralKnowledge,

    [JsonStringEnumMemberName("no-result")]
    NoResult,
}
