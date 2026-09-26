using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// Processing status of one document or FAQ entry (the five states of the design document,
/// §7). The serialized names must equal the frontend's <c>KnowledgeDocumentStatus</c> union
/// in <c>knowledge-base.model.ts</c> exactly; <c>SmartAgri.Domain.Tests</c> compares them
/// against that file. Stored per version (<see cref="KnowledgeDocumentVersion.ProcessingStatus"/>,
/// by wire name); a document shows its latest version's.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeDocumentStatus>))]
public enum KnowledgeDocumentStatus
{
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("processing")]
    Processing,

    [JsonStringEnumMemberName("ready")]
    Ready,

    [JsonStringEnumMemberName("partially-readable")]
    PartiallyReadable,

    [JsonStringEnumMemberName("failed")]
    Failed,
}
