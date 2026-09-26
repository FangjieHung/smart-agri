using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Infrastructure.Knowledge.Extraction;

/// <summary>
/// XLSX text with the Open XML SDK (MIT; assistant-access ADR): one unit per visible
/// worksheet, in workbook order. Each non-empty row becomes one line of its cells as Excel
/// displays them (shared strings, rich text, number and date formats —
/// <see cref="ExcelNumberFormat"/>), joined by <c> | </c>, empty cells keeping their column's
/// place; the first non-empty row is the header, which every chunk repeats.
/// </summary>
/// <remarks>
/// Worksheets are read as a stream (SAX), and only up to
/// <see cref="ExtractionLimits.MaxSheetRows"/> data rows each and
/// <see cref="ExtractionLimits.MaxUnits"/> worksheets: a workbook of a few megabytes can
/// expand to hundreds of megabytes of XML (M2 plan §7, risk 3). Hidden worksheets, chart
/// sheets and empty worksheets are not units. Cached formula results are read as values;
/// formulas are never evaluated.
/// </remarks>
public sealed class XlsxTextExtractor : IDocumentTextExtractor
{
    public bool CanExtract(KnowledgeFileFormat format) => format == KnowledgeFileFormat.Xlsx;

    public ExtractedDocument Extract(
        KnowledgeFileFormat format,
        byte[] content,
        ExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);
        if (!CanExtract(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "Not an XLSX.");
        }

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var document = SpreadsheetDocument.Open(stream, isEditable: false);
            var workbookPart = document.WorkbookPart;
            var sheets = workbookPart?.Workbook?.Sheets
                ?? throw new DocumentExtractionException(DocumentExtractionFailure.Damaged, "The XLSX has no workbook.");

            var cells = new CellReader(
                SharedStrings(workbookPart.SharedStringTablePart, cancellationToken),
                ExcelNumberFormat.Styles.From(workbookPart.WorkbookStylesPart?.Stylesheet),
                date1904: workbookPart.Workbook.WorkbookProperties?.Date1904?.Value == true);

            var units = new List<ExtractedUnit>();
            var truncated = false;
            foreach (var sheet in sheets.Elements<Sheet>())
            {
                if (sheet.State?.Value is { } state && state != SheetStateValues.Visible)
                {
                    continue;
                }

                if (sheet.Id?.Value is not { } partId || workbookPart.GetPartById(partId) is not WorksheetPart worksheet)
                {
                    continue;
                }

                if (units.Count == limits.MaxUnits)
                {
                    truncated = true;
                    break;
                }

                if (ReadSheet(sheet.Name?.Value ?? string.Empty, worksheet, cells, limits, cancellationToken) is { } extracted)
                {
                    units.Add(ExtractedUnit.Worksheet(extracted));
                }
            }

            return new ExtractedDocument(units, truncated);
        }
        catch (Exception exception) when (exception is not (DocumentExtractionException or OperationCanceledException))
        {
            throw new DocumentExtractionException(DocumentExtractionFailure.Damaged, "The Open XML SDK could not read the XLSX.", exception);
        }
    }

    /// <summary>The worksheet's non-empty rows (header plus at most the row limit), or null
    /// when it has none.</summary>
    private static ExtractedSheet? ReadSheet(
        string name,
        WorksheetPart worksheet,
        CellReader cells,
        ExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        var rows = new List<(int Number, SortedDictionary<int, string> Cells)>();
        var truncated = false;
        var previousRow = 0;
        using (var reader = OpenXmlReader.Create(worksheet))
        {
            var positioned = reader.Read();
            while (positioned)
            {
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                {
                    positioned = reader.Read();
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Loading the row moves the reader to what follows it.
                var row = (Row)reader.LoadCurrentElement()!;
                positioned = !reader.EOF;
                var number = row.RowIndex?.Value is { } index ? (int)index : previousRow + 1;
                previousRow = number;
                var values = cells.Read(row);
                if (values.Count == 0)
                {
                    continue;
                }

                if (rows.Count > limits.MaxSheetRows)
                {
                    // The header plus MaxSheetRows data rows are read; this is one more.
                    truncated = true;
                    break;
                }

                rows.Add((number, values));
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        // Columns start at the leftmost used one, so a table placed from column B does not
        // begin every line with an empty cell.
        var firstColumn = rows.Min(row => row.Cells.Keys.First());
        var lines = rows.ConvertAll(row => SheetRow.FromCells(
            row.Number,
            Enumerable.Range(firstColumn, row.Cells.Keys.Last() - firstColumn + 1)
                .Select(column => row.Cells.GetValueOrDefault(column, string.Empty))));
        return new ExtractedSheet(name, lines[0], lines[1..], truncated);
    }

    private static List<string> SharedStrings(SharedStringTablePart? part, CancellationToken cancellationToken)
    {
        var strings = new List<string>();
        if (part is null)
        {
            return strings;
        }

        using var reader = OpenXmlReader.Create(part);
        var positioned = reader.Read();
        while (positioned)
        {
            if (reader.ElementType != typeof(SharedStringItem) || !reader.IsStartElement)
            {
                positioned = reader.Read();
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            strings.Add(StringItemText(reader.LoadCurrentElement()!));
            positioned = !reader.EOF;
        }

        return strings;
    }

    /// <summary>A shared or inline string: its plain text or its rich-text runs, never the
    /// phonetic guide (<c>rPh</c>).</summary>
    private static string StringItemText(OpenXmlElement item) =>
        string.Concat(item.ChildElements.Select(child => child switch
        {
            Text text => text.Text,
            Run run => run.Text?.Text ?? string.Empty,
            _ => string.Empty,
        }));

    /// <summary>Turns a row's cells into their displayed text, by column (1 = A).</summary>
    private sealed class CellReader
    {
        private readonly List<string> _sharedStrings;
        private readonly ExcelNumberFormat.Styles _styles;
        private readonly bool _date1904;

        public CellReader(List<string> sharedStrings, ExcelNumberFormat.Styles styles, bool date1904)
        {
            _sharedStrings = sharedStrings;
            _styles = styles;
            _date1904 = date1904;
        }

        /// <summary>The row's non-empty cells.</summary>
        public SortedDictionary<int, string> Read(Row row)
        {
            var values = new SortedDictionary<int, string>();
            var previousColumn = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var column = ColumnNumber(cell.CellReference?.Value) ?? previousColumn + 1;
                previousColumn = column;
                var text = ExtractedText.CleanLine(Display(cell));
                if (text.Length > 0)
                {
                    values[column] = text;
                }
            }

            return values;
        }

        private string Display(Cell cell)
        {
            var type = cell.DataType?.Value;
            if (type == CellValues.InlineString)
            {
                return cell.InlineString is { } inline ? StringItemText(inline) : string.Empty;
            }

            var raw = cell.CellValue?.Text;
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            if (type == CellValues.SharedString)
            {
                return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < _sharedStrings.Count
                    ? _sharedStrings[index]
                    : string.Empty;
            }

            if (type == CellValues.Boolean)
            {
                return raw == "1" ? "TRUE" : "FALSE";
            }

            if (type == CellValues.Date)
            {
                // ISO 8601 (t="d", rare): shown through the cell's format when it has one.
                return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
                    ? _styles.Format(ExcelNumberFormat.ToSerial(date, _date1904), cell.StyleIndex?.Value, _date1904, dateByDefault: true)
                    : raw;
            }

            if (type == CellValues.Error || type == CellValues.String)
            {
                return raw;
            }

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? _styles.Format(number, cell.StyleIndex?.Value, _date1904)
                : raw;
        }

        /// <summary>1 for <c>A1</c>, 28 for <c>AB7</c>; null without a reference.</summary>
        private static int? ColumnNumber(string? reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return null;
            }

            var column = 0;
            foreach (var character in reference)
            {
                if (!char.IsAsciiLetter(character))
                {
                    break;
                }

                column = (column * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
            }

            return column > 0 ? column : null;
        }
    }
}
