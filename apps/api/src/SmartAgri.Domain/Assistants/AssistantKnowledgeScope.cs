using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// How strictly an assistant must stick to its connected sources. The serialized names must
/// equal the frontend's <c>AssistantKnowledgeScope</c> union in
/// <c>apps/admin/src/app/core/domain/assistant-draft.model.ts</c> exactly;
/// <c>SmartAgri.Domain.Tests</c> compares them against that file.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantKnowledgeScope>))]
public enum AssistantKnowledgeScope
{
    /// <summary>嚴格：只依據已連接的資料回答；below the retrieval threshold answers
    /// <c>no-result</c> without calling the model (grounded-answers ADR).</summary>
    [JsonStringEnumMemberName("company-data-only")]
    CompanyDataOnly,

    /// <summary>一般：below the threshold, the model may answer from general knowledge,
    /// clearly labeled as such.</summary>
    [JsonStringEnumMemberName("allow-general-knowledge")]
    AllowGeneralKnowledge,
}
