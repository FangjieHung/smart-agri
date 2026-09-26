using System.Globalization;
using System.Text;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge.Processing;

/// <summary>
/// Whether the text read from one unit can be used (M2 plan §4, readability rules). A PDF
/// page with fewer than <see cref="MinimumPageCharacters"/> characters — usually a scanned
/// page — or any unit whose characters are more than 30% garbage — U+FFFD, private-use or
/// control characters, usually a font without a ToUnicode mapping — is unreadable.
/// </summary>
/// <remarks>
/// Characters are counted as Unicode scalar values (a CJK Extension B character is one),
/// white space excluded. The minimum applies to PDF pages only: a short DOCX or Markdown
/// section (「無。」) is fine, and a section with no text at all is never extracted. These
/// rules catch garbage, not a wrong reading order (M2 plan §7, risk 2) — the preview and the
/// retrieval evaluation are there for that.
/// </remarks>
public static class KnowledgeReadability
{
    public const int MinimumPageCharacters = 10;

    /// <summary>Garbage may be at most this share of a unit's characters (30%).</summary>
    public const double MaximumGarbledShare = 0.3;

    /// <summary>Why the unit is unreadable, or <see langword="null"/> when it is readable.</summary>
    public static KnowledgeUnitIssue? Judge(KnowledgeUnitLocationKind kind, string text)
    {
        var (characters, garbled) = Count(text);
        if (characters == 0 || (kind == KnowledgeUnitLocationKind.Page && characters < MinimumPageCharacters))
        {
            return KnowledgeUnitIssue.TooLittleText;
        }

        // garbled / characters > 30%, in integers.
        return garbled * 10 > characters * 3 ? KnowledgeUnitIssue.GarbledText : null;
    }

    /// <summary>Non-white-space characters, and how many of them are garbage (a lone
    /// surrogate counts as one garbage character).</summary>
    public static (int Characters, int Garbled) Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var characters = 0;
        var garbled = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                characters++;
                garbled++;
                index += Math.Max(consumed, 1);
                continue;
            }

            index += consumed;
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            characters++;
            if (IsGarbage(rune))
            {
                garbled++;
            }
        }

        return (characters, garbled);
    }

    /// <summary>U+FFFD (the replacement character), a private-use character (what a font's
    /// own codes look like without a ToUnicode mapping) or a control character.</summary>
    public static bool IsGarbage(Rune rune) =>
        rune.Value == 0xFFFD
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.PrivateUse or UnicodeCategory.Control;
}
