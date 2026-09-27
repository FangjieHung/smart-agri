using System.Globalization;
using System.Text;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge.Processing;

/// <summary>
/// How a location in a file is named for people — in the extraction preview and, later, in
/// citations (ADR: document + page or section + excerpt; M2 plan §4): 「第 3 頁」, the heading
/// path 「2 退換貨 › 2.1 退貨條件」, 「工作表『配送時間』」 and, for a worksheet's chunk,
/// 「工作表『配送時間』第 2–30 列」.
/// </summary>
public static class KnowledgeLocationLabels
{
    /// <summary>Between the headings of a section's path, outermost first.</summary>
    public const string HeadingSeparator = " › ";

    /// <summary>The text before a document's first heading.</summary>
    public const string DocumentStart = "文件開頭";

    /// <summary>A plain-text file, which has no sections.</summary>
    public const string WholeText = "全文";

    /// <summary>A longer heading is cut, so that three levels always fit
    /// <see cref="KnowledgeExtractedUnit.LocationLabelMaxLength"/>.</summary>
    public const int HeadingMaxLength = 80;

    public static string Page(int pageNumber) => string.Create(CultureInfo.InvariantCulture, $"第 {pageNumber} 頁");

    /// <summary>The headings joined with <see cref="HeadingSeparator"/>, or
    /// <see cref="DocumentStart"/> when there are none. Each heading is cleaned to one line and
    /// cut at <see cref="HeadingMaxLength"/>; if the path is still too long, its outer
    /// headings give way to 「…」.</summary>
    public static string Section(IReadOnlyList<string> headingPath)
    {
        ArgumentNullException.ThrowIfNull(headingPath);

        var headings = headingPath
            .Select(ExtractedText.CleanLine)
            .Where(heading => heading.Length > 0)
            .Select(heading => Shorten(heading, HeadingMaxLength))
            .ToList();
        if (headings.Count == 0)
        {
            return DocumentStart;
        }

        var label = string.Join(HeadingSeparator, headings);
        while (label.Length > KnowledgeExtractedUnit.LocationLabelMaxLength && headings.Count > 1)
        {
            headings.RemoveAt(0);
            label = "…" + HeadingSeparator + string.Join(HeadingSeparator, headings);
        }

        return label;
    }

    public static string Sheet(string sheetName) => $"工作表『{SheetName(sheetName)}』";

    /// <summary>A worksheet chunk's rows: 「第 5 列」 or 「第 2–30 列」 (an en dash).</summary>
    public static string SheetRows(string sheetName, int firstRow, int lastRow) =>
        firstRow == lastRow
            ? string.Create(CultureInfo.InvariantCulture, $"{Sheet(sheetName)}第 {firstRow} 列")
            : string.Create(CultureInfo.InvariantCulture, $"{Sheet(sheetName)}第 {firstRow}–{lastRow} 列");

    /// <summary>Page numbers for an issue, ascending, consecutive ones as ranges:
    /// 「2、5–7、9」.</summary>
    public static string PageList(IEnumerable<int> pageNumbers)
    {
        ArgumentNullException.ThrowIfNull(pageNumbers);

        var pages = pageNumbers.Distinct().Order().ToList();
        var parts = new List<string>();
        var index = 0;
        while (index < pages.Count)
        {
            var end = index;
            while (end + 1 < pages.Count && pages[end + 1] == pages[end] + 1)
            {
                end++;
            }

            parts.Add(end == index
                ? pages[index].ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{pages[index]}–{pages[end]}"));
            index = end + 1;
        }

        return string.Join('、', parts);
    }

    private static string SheetName(string sheetName)
    {
        ArgumentNullException.ThrowIfNull(sheetName);
        var name = ExtractedText.CleanLine(sheetName);
        return Shorten(name.Length > 0 ? name : "(未命名)", HeadingMaxLength);
    }

    /// <summary>At most <paramref name="maxLength"/> UTF-16 characters, ending in 「…」 when
    /// cut, never splitting a surrogate pair.</summary>
    private static string Shorten(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = maxLength - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return new StringBuilder(text, 0, cut, maxLength).Append('…').ToString();
    }
}
