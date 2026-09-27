using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// What a <see cref="KnowledgeActivity"/> row records. Stored as the wire name (not the
/// number), so members can be reordered safely. Later slices add document, version and
/// segment actions (M2 plan §4).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeActivityAction>))]
public enum KnowledgeActivityAction
{
    [JsonStringEnumMemberName("knowledge-base-created")]
    KnowledgeBaseCreated,

    /// <summary>Name and/or purpose changed; <see cref="KnowledgeActivity.Detail"/> lists
    /// which fields, not their values.</summary>
    [JsonStringEnumMemberName("knowledge-base-updated")]
    KnowledgeBaseUpdated,

    [JsonStringEnumMemberName("sharing-changed")]
    SharingChanged,

    /// <summary>The one row that outlives the knowledge base it describes: deleting a
    /// knowledge base removes all of its other activity rows and then writes this one.</summary>
    [JsonStringEnumMemberName("knowledge-base-deleted")]
    KnowledgeBaseDeleted,
}
