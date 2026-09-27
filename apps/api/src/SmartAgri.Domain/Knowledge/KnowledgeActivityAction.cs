using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// What a <see cref="KnowledgeActivity"/> row records. Stored as the wire name (not the
/// number), so members can be reordered safely. Every row names who acted; none carries
/// document content (M2 plan §4).
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

    /// <summary>A file was uploaded as a new version (2, 3, …) of an existing document;
    /// pending review like every version.</summary>
    [JsonStringEnumMemberName("version-uploaded")]
    VersionUploaded,

    /// <summary>The owner approved a version (one row per version of a batch approval); the
    /// version itself keeps when it takes effect.</summary>
    [JsonStringEnumMemberName("version-approved")]
    VersionApproved,

    /// <summary>The owner disabled a document in an emergency. The reason stays on the
    /// document while it is disabled, never in the log (it is free text).</summary>
    [JsonStringEnumMemberName("document-disabled")]
    DocumentDisabled,

    /// <summary>The owner lifted an emergency disable.</summary>
    [JsonStringEnumMemberName("document-enabled")]
    DocumentEnabled,

    /// <summary>The owner wrote a new FAQ entry (its version 1, pending review). Ids only:
    /// neither the question nor the answer.</summary>
    [JsonStringEnumMemberName("faq-created")]
    FaqCreated,

    /// <summary>The owner edited an FAQ entry: a new version, pending review, while the version
    /// in effect keeps answering. Ids only.</summary>
    [JsonStringEnumMemberName("faq-updated")]
    FaqUpdated,

    /// <summary>An FAQ entry was deleted with all of its versions, like
    /// <see cref="DocumentDeleted"/> for a document.</summary>
    [JsonStringEnumMemberName("faq-deleted")]
    FaqDeleted,
}
