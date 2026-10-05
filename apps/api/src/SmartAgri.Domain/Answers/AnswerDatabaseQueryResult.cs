using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Answers;

/// <summary>
/// The result of a <see cref="AnswerReplyKind.DatabaseQuery"/> outcome (M4 #178): the four
/// categories the owner asked for, never the query, its parameters or its numbers. Mapped from
/// the reply's <c>ChatDatabaseQueryStatus</c> by <c>SmartAgri.Application.Answers.AnswerKinds</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnswerDatabaseQueryResult>))]
public enum AnswerDatabaseQueryResult
{
    /// <summary>The query ran and found records (<c>answered</c>).</summary>
    [JsonStringEnumMemberName("answered")]
    Answered,

    /// <summary>No database the asker may query through this assistant now (<c>not-available</c>).</summary>
    [JsonStringEnumMemberName("not-permitted")]
    NotPermitted,

    /// <summary>The query ran but there were too few records: none in the period
    /// (<c>no-data</c>) or too few to compare (<c>insufficient-data</c>).</summary>
    [JsonStringEnumMemberName("insufficient-records")]
    InsufficientRecords,

    /// <summary>No answer from the records: the selection model call failed, the model asked for
    /// something outside the fixed queries (<c>rejected</c>), or the query itself failed
    /// (<c>failed</c>).</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}
