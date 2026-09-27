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

    /// <summary>Generating a grounded answer for an assistant conversation (M3, Slice 4).</summary>
    [JsonStringEnumMemberName("generate-answer")]
    GenerateAnswer,

    /// <summary>Generating a grounded answer for a wizard trial question, before any assistant
    /// exists (M3, Slice 8): same pipeline as <see cref="GenerateAnswer"/>, distinguished so
    /// trial calls never mix into an assistant's own usage.</summary>
    [JsonStringEnumMemberName("trial-answer")]
    TrialAnswer,
}
