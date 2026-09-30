using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>Why an <see cref="AssistantTestRun"/> was queued (M3.5 plan §4). Only
/// <see cref="Manual"/> is produced by Slice 2 (#124, 「全部重跑」); the automatic triggers belong
/// to Slice 3 (#125).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantTestRunTrigger>))]
public enum AssistantTestRunTrigger
{
    /// <summary>The owner pressed 「全部重跑」.</summary>
    [JsonStringEnumMemberName("manual")]
    Manual,

    /// <summary>A connected knowledge base's version took effect, or a document was disabled or
    /// restored (Slice 3).</summary>
    [JsonStringEnumMemberName("knowledge-changed")]
    KnowledgeChanged,

    /// <summary>The assistant's own sources or answering rules changed (Slice 3).</summary>
    [JsonStringEnumMemberName("assistant-changed")]
    AssistantChanged,
}
