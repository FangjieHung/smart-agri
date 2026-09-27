using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge.Processing;

/// <summary>What an <see cref="IDocumentTextExtractor"/> read from one file.</summary>
/// <param name="Units">In reading order.</param>
/// <param name="UnitsTruncated">The file has more units than
/// <see cref="ExtractionLimits.MaxUnits"/>; only the first ones were read.</param>
public sealed record ExtractedDocument(IReadOnlyList<ExtractedUnit> Units, bool UnitsTruncated = false);

/// <summary>
/// One page, section, whole text or worksheet as read. Built only through the factories,
/// which clean the text (<see cref="ExtractedText.Clean"/>) and label the location
/// (<see cref="KnowledgeLocationLabels"/>), so every format labels and cleans alike.
/// </summary>
public sealed class ExtractedUnit
{
    private ExtractedUnit(KnowledgeUnitLocationKind kind, string locationLabel, string text, int? pageNumber, ExtractedSheet? sheet)
    {
        Kind = kind;
        LocationLabel = locationLabel;
        Text = text;
        PageNumber = pageNumber;
        Sheet = sheet;
    }

    public KnowledgeUnitLocationKind Kind { get; }

    public string LocationLabel { get; }

    /// <summary>The unit's whole text, cleaned: what the preview shows.</summary>
    public string Text { get; }

    /// <summary>1-based, for a PDF page.</summary>
    public int? PageNumber { get; }

    /// <summary>The rows, for a worksheet: chunks repeat its header row.</summary>
    public ExtractedSheet? Sheet { get; }

    /// <summary>A PDF page (「第 3 頁」).</summary>
    public static ExtractedUnit Page(int pageNumber, string rawText)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        return new(KnowledgeUnitLocationKind.Page, KnowledgeLocationLabels.Page(pageNumber), ExtractedText.Clean(rawText), pageNumber, null);
    }

    /// <summary>A DOCX or Markdown section under <paramref name="headingPath"/> (outermost
    /// heading first; empty for the text before the first heading).</summary>
    public static ExtractedUnit Section(IReadOnlyList<string> headingPath, string rawText) =>
        new(KnowledgeUnitLocationKind.Section, KnowledgeLocationLabels.Section(headingPath), ExtractedText.Clean(rawText), null, null);

    /// <summary>A plain-text file, which has no sections (「全文」).</summary>
    public static ExtractedUnit WholeText(string rawText) =>
        new(KnowledgeUnitLocationKind.Section, KnowledgeLocationLabels.WholeText, ExtractedText.Clean(rawText), null, null);

    /// <summary>A worksheet (「工作表『配送時間』」); its text is the header row and the data
    /// rows, one per line.</summary>
    public static ExtractedUnit Worksheet(ExtractedSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var lines = sheet.DataRows.Select(row => row.Text).Prepend(sheet.Header.Text);
        return new(KnowledgeUnitLocationKind.Sheet, KnowledgeLocationLabels.Sheet(sheet.Name), string.Join('\n', lines), null, sheet);
    }
}

/// <summary>A worksheet's non-empty rows: the first is its header.</summary>
/// <param name="RowsTruncated">The sheet has more data rows than
/// <see cref="ExtractionLimits.MaxSheetRows"/>; only the first ones were read.</param>
public sealed record ExtractedSheet(string Name, SheetRow Header, IReadOnlyList<SheetRow> DataRows, bool RowsTruncated = false);

/// <summary>One row: its cells as displayed, joined with <see cref="CellSeparator"/>.</summary>
/// <param name="RowNumber">The spreadsheet's own row number (1-based).</param>
public sealed record SheetRow
{
    public const string CellSeparator = " | ";

    public SheetRow(int rowNumber, string text)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rowNumber, 1);
        RowNumber = rowNumber;
        Text = ExtractedText.CleanLine(text);
    }

    public int RowNumber { get; }

    public string Text { get; }

    /// <summary>A row from its cells in column order (empty cells keep their place).</summary>
    public static SheetRow FromCells(int rowNumber, IEnumerable<string> cells) =>
        new(rowNumber, string.Join(CellSeparator, cells.Select(ExtractedText.CleanLine)));
}
