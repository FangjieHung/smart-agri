using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Answers;

/// <summary>
/// Where an <see cref="AnswerOutcome"/> came from (M3.5 plan §4 and §7 "營運追蹤只存「結果」，
/// 不存內容").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnswerOutcomeChannel>))]
public enum AnswerOutcomeChannel
{
    /// <summary>A real, in-platform conversation turn (<c>POST .../chat/runs</c>).</summary>
    [JsonStringEnumMemberName("chat")]
    Chat,

    /// <summary>A trial question, either a wizard draft's or an already-built assistant's
    /// (<c>POST .../trial-answers</c>).</summary>
    [JsonStringEnumMemberName("trial")]
    Trial,

    /// <summary>One question of an assistant's test-set rerun (Slice 2, #124,
    /// <c>ModelInvocationPurpose.AssistantTest</c>).</summary>
    [JsonStringEnumMemberName("test-run")]
    TestRun,

    /// <summary>An anonymous visitor's question on the assistant's website channel (M5a #196,
    /// <c>ModelInvocationPurpose.PublicAnswer</c>). Like every outcome, it stores no content and no
    /// visitor.</summary>
    [JsonStringEnumMemberName("website")]
    Website,

    /// <summary>A LINE user's question on the assistant's LINE channel (M5b #232,
    /// <c>ModelInvocationPurpose.LineAnswer</c>). No content and no LINE id.</summary>
    [JsonStringEnumMemberName("line")]
    Line,
}
