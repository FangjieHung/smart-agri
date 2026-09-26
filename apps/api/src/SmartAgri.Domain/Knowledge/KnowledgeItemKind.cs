using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// What an item in a knowledge base is: an uploaded document or a hand-written FAQ entry.
/// The serialized names must equal the frontend's <c>KnowledgeItemKind</c> union in
/// <c>knowledge-base.model.ts</c> exactly; <c>SmartAgri.Domain.Tests</c> compares them
/// against that file.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeItemKind>))]
public enum KnowledgeItemKind
{
    [JsonStringEnumMemberName("document")]
    Document,

    [JsonStringEnumMemberName("faq")]
    Faq,
}
