using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// What one <see cref="KnowledgeExtractedUnit"/> is (M2 plan §4): a PDF page, a DOCX or
/// Markdown section (or a whole plain-text file), an XLSX worksheet, or an FAQ entry's
/// question and answer (Slice 10: one unit and one chunk per version, located 「FAQ」).
/// Chunks never cross a unit. Stored by wire name.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeUnitLocationKind>))]
public enum KnowledgeUnitLocationKind
{
    [JsonStringEnumMemberName("page")]
    Page,

    [JsonStringEnumMemberName("section")]
    Section,

    [JsonStringEnumMemberName("sheet")]
    Sheet,

    [JsonStringEnumMemberName("faq")]
    Faq,
}
