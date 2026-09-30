using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Answers;

/// <summary>
/// Where an <see cref="AnswerOutcome"/> came from (M3.5 plan §4 and §7 "營運追蹤只存「結果」，
/// 不存內容"). <see cref="TestRun"/> is reserved for Slice 2 (#124, "全部重跑"); nothing writes
/// it yet.
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

    /// <summary>One question of an assistant's test-set rerun (Slice 2, #124). Reserved: no
    /// caller produces it yet.</summary>
    [JsonStringEnumMemberName("test-run")]
    TestRun,
}
