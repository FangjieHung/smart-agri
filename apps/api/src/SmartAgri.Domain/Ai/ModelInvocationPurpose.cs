using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Ai;

/// <summary>What a model was called for (<see cref="ModelInvocation.Purpose"/>, M2 plan §4).
/// Stored by wire name. M3 adds chat purposes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelInvocationPurpose>))]
public enum ModelInvocationPurpose
{
    /// <summary>Embedding document chunks: processing an uploaded version, or <c>reindex</c>.</summary>
    [JsonStringEnumMemberName("embed-document")]
    EmbedDocument,

    /// <summary>Embedding a question to search with (retrieval, Slice 9).</summary>
    [JsonStringEnumMemberName("embed-query")]
    EmbedQuery,
}
