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
    private ExtractedUnit(
        KnowledgeUnitLocationKind kind,
        string locationLabel,
        string text,
        int? pageNumber,
        ExtractedSheet? sheet,
        IReadOnlyList<ExtractedTable>? tables = null,
        string? textOutsideTables = null)
    {
        Kind = kind;
        LocationLabel = locationLabel;
        Text = text;
        PageNumber = pageNumber;
        Sheet = sheet;
        Tables = tables ?? [];
        TextOutsideTables = textOutsideTables ?? text;
    }

    public KnowledgeUnitLocationKind Kind { get; }

    public string LocationLabel { get; }

    /// <summary>The unit's whole text, cleaned: what the preview shows.</summary>
    public string Text { get; }

    /// <summary>1-based, for a PDF page.</summary>
    public int? PageNumber { get; }

    /// <summary>The rows, for a worksheet: chunks repeat its header row.</summary>
    public ExtractedSheet? Sheet { get; }

    /// <summary>A section's tables (Markdown pipe tables, DOCX tables), in reading order: each
    /// data row becomes a chunk of its own (<see cref="KnowledgeChunkFormat.TableRows"/>).
    /// Empty for every other unit and for a section without tables.</summary>
    public IReadOnlyList<ExtractedTable> Tables { get; }

    /// <summary><see cref="Text"/> without the lines of <see cref="Tables"/>, cleaned the same
    /// way: what is chunked as text. <see cref="Text"/> itself when there are no tables.</summary>
    public string TextOutsideTables { get; }

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

    /// <summary>A DOCX or Markdown section with <paramref name="tables"/>: its
    /// <paramref name="rawText"/> is the whole section as before (what the preview shows, tables
    /// included), <paramref name="rawTextOutsideTables"/> the same without the tables' lines.</summary>
    public static ExtractedUnit Section(
        IReadOnlyList<string> headingPath,
        string rawText,
        string rawTextOutsideTables,
        IReadOnlyList<ExtractedTable> tables)
    {
        ArgumentNullException.ThrowIfNull(rawTextOutsideTables);
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count == 0)
        {
            return Section(headingPath, rawText);
        }

        return new(
            KnowledgeUnitLocationKind.Section,
            KnowledgeLocationLabels.Section(headingPath),
            ExtractedText.Clean(rawText),
            null,
            null,
            tables,
            ExtractedText.Clean(rawTextOutsideTables));
    }

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

/// <summary>
/// A table in a section: its header row's cells (the column names) and its data rows' cells,
/// each cleaned to one line (<see cref="ExtractedText.CleanLine"/>). A row may have fewer
/// cells than the header (the rest are empty) or more (those have no column name).
/// </summary>
public sealed record ExtractedTable
{
    public ExtractedTable(IReadOnlyList<string> header, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        Header = [.. header.Select(ExtractedText.CleanLine)];
        Rows = [.. rows.Select(row => (IReadOnlyList<string>)[.. row.Select(ExtractedText.CleanLine)])];
    }

    /// <summary>Between a column's name and its value in a row's text.</summary>
    public const string NameValueSeparator = "：";

    public IReadOnlyList<string> Header { get; }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>
    /// Data row <paramref name="index"/> (0-based) as a chunk's text: one line per non-empty
    /// cell, 「欄名：值」 — just the value when its column has no name. Empty when every cell is.
    /// </summary>
    public string RowText(int index)
    {
        var row = Rows[index];
        var lines = new List<string>(row.Count);
        for (var column = 0; column < row.Count; column++)
        {
            var value = row[column];
            if (value.Length == 0)
            {
                continue;
            }

            var name = column < Header.Count ? Header[column] : string.Empty;
            lines.Add(name.Length == 0 ? value : name + NameValueSeparator + value);
        }

        return string.Join('\n', lines);
    }
}
