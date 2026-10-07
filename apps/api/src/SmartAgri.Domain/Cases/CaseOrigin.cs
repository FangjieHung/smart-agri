using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Cases;

/// <summary>Where a <see cref="Case"/> came from (case ADR「案件從哪裡來」; M7 plan §4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaseOrigin>))]
public enum CaseOrigin
{
    /// <summary>An internal account created it on the platform (M7-3).</summary>
    [JsonStringEnumMemberName("manual")]
    Manual,

    /// <summary>The assistant proposed it in a conversation and the asker confirmed (M7-9); links the thread.</summary>
    [JsonStringEnumMemberName("chat-proposal")]
    ChatProposal,

    /// <summary>Opened automatically when a database record was submitted (M7-10): <b>no creator</b>
    /// (decision M); links the record.</summary>
    [JsonStringEnumMemberName("database-submission")]
    DatabaseSubmission,

    /// <summary>「另開案件」 from a 處理事項 (M7-7); links the issue.</summary>
    [JsonStringEnumMemberName("assistant-issue")]
    AssistantIssue,
}
