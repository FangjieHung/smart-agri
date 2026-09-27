using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// What one <see cref="KnowledgeExtractedUnit"/> is (M2 plan §4): a PDF page, a DOCX or
/// Markdown section (or a whole plain-text file), or an XLSX worksheet. Chunks never cross a
/// unit. Stored by wire name. Slice 10 (FAQ entries) adds its own kind.
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
}
