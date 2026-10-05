using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Answers;

/// <summary>
/// The reply kind an <see cref="AnswerOutcome"/> row carries — a persistence-layer mirror of
/// <c>SmartAgri.Application.Answers.GroundedReplyKind</c> (Domain may not reference
/// Application), the same three wire names as <see cref="Chat.ChatReplyKind"/>, plus
/// <see cref="DatabaseQuery"/> (M4 #178).
/// </summary>
/// <remarks>
/// The first three are the knowledge-base answer pipeline's kinds; their rates (no-result,
/// rejected citation) never include <see cref="DatabaseQuery"/> rows, which are counted on their
/// own (<see cref="AnswerOutcome.DatabaseQueryResult"/>). An assistant test result
/// (<c>AssistantTestResult.ActualKind</c>) is never <see cref="DatabaseQuery"/>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AnswerReplyKind>))]
public enum AnswerReplyKind
{
    [JsonStringEnumMemberName("company-data")]
    CompanyData,

    [JsonStringEnumMemberName("general-knowledge")]
    GeneralKnowledge,

    [JsonStringEnumMemberName("no-result")]
    NoResult,

    /// <summary>A conversation's fixed database query answer (M4 #149, recorded since #178); its
    /// result is <see cref="AnswerOutcome.DatabaseQueryResult"/>.</summary>
    [JsonStringEnumMemberName("database-query")]
    DatabaseQuery,
}
