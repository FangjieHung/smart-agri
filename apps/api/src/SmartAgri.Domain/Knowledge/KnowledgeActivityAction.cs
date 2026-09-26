using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// What a <see cref="KnowledgeActivity"/> row records. Stored as the wire name (not the
/// number), so members can be reordered safely. Later slices add version and segment
/// actions (M2 plan §4).
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

    /// <summary>A file was uploaded as a new document (its version 1).</summary>
    [JsonStringEnumMemberName("document-uploaded")]
    DocumentUploaded,

    /// <summary>The owner asked for a failed version to be processed again.</summary>
    [JsonStringEnumMemberName("version-retried")]
    VersionRetried,

    /// <summary>A document was deleted with all of its versions and files. Its earlier
    /// activity rows are kept (they hold ids only), so the log still shows who uploaded
    /// what was deleted.</summary>
    [JsonStringEnumMemberName("document-deleted")]
    DocumentDeleted,

    /// <summary>The owner excluded a chunk from retrieval (a cover page, an appendix, outdated
    /// terms); <see cref="KnowledgeActivity.Detail"/> names the chunk by id only.</summary>
    [JsonStringEnumMemberName("chunk-excluded")]
    ChunkExcluded,

    /// <summary>The owner put an excluded chunk back.</summary>
    [JsonStringEnumMemberName("chunk-included")]
    ChunkIncluded,
}
