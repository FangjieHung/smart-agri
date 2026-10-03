using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Chat;

/// <summary>
/// The reply kind a saved <see cref="ChatMessage"/> carries — a persistence-layer mirror of
/// <c>SmartAgri.Application.Answers.GroundedReplyKind</c> (Domain may not reference
/// Application). #77 maps between the two when it saves an assistant's turn. M4 #148 adds the two
/// form replies, which never come from the answer pipeline: <see cref="FormRequest"/> (an
/// assistant asking the member to fill in a connected database's form) and
/// <see cref="SubmissionReceipt"/> (the receipt of a consented submission). Stored as integers,
/// so new members only ever go at the end.
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

    [JsonStringEnumMemberName("form-request")]
    FormRequest,

    [JsonStringEnumMemberName("submission-receipt")]
    SubmissionReceipt,
}
