using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// Why a <see cref="KnowledgeExtractedUnit"/> needs the owner's attention (M2 plan §4,
/// readability rules). The first two make a unit unreadable — it gets no chunks, so nothing
/// from it is ever retrieved; <see cref="RowsTruncated"/> marks a readable worksheet of which
/// only the first rows were read. Stored by wire name.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeUnitIssue>))]
public enum KnowledgeUnitIssue
{
    /// <summary>A PDF page with fewer than 10 characters of text: usually a scanned page.</summary>
    [JsonStringEnumMemberName("too-little-text")]
    TooLittleText,

    /// <summary>More than 30% of the characters are U+FFFD, private-use or control
    /// characters: usually a font without a ToUnicode mapping.</summary>
    [JsonStringEnumMemberName("garbled-text")]
    GarbledText,

    /// <summary>A worksheet with more rows than the configured limit; the rest was not read.</summary>
    [JsonStringEnumMemberName("rows-truncated")]
    RowsTruncated,
}

/// <summary>Which <see cref="KnowledgeUnitIssue"/>s make a unit unreadable.</summary>
public static class KnowledgeUnitIssues
{
    public static bool MakesUnreadable(KnowledgeUnitIssue issue) =>
        issue is KnowledgeUnitIssue.TooLittleText or KnowledgeUnitIssue.GarbledText;
}
