using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Infrastructure.Knowledge.Extraction;

/// <summary>
/// DOCX text with the Open XML SDK (MIT; assistant-access ADR): the main document's body, in
/// order, split into sections at Heading 1–3 — so a chunk's location is its heading path
/// (「2 退換貨 › 2.1 退貨條件」) — with each table row as one line of cells joined by
/// <c> | </c>. A section with no text of its own (a heading followed directly by a
/// sub-heading) is not a unit; the text before the first heading is 「文件開頭」.
/// </summary>
/// <remarks>
/// <para>
/// A paragraph is a heading of level 1–3 when its outline level (<c>w:outlineLvl</c> 0–2) says
/// so — set on the paragraph itself, or on its style or a style that one is based on — or when
/// its style is a built-in heading by name (<c>heading 1</c>, which Word writes in every
/// language, even where the style id is localized, e.g. <c>1</c> in Traditional Chinese Word)
/// or by a localized name (「標題 1」, 「标题 1」, 「見出し 1」) or id (<c>Heading1</c>).
/// Deeper headings stay in their section's text.
/// </para>
/// <para>
/// Read: runs, tabs and line breaks, hyperlinks, fields' displayed results, inserted text,
/// content controls, text boxes. Not read: deleted or moved-away text of tracked changes,
/// field codes, symbol-font characters (<c>w:sym</c>), the fallback copy of alternate content
/// (it duplicates the choice), headers, footers, footnotes and comments.
/// </para>
/// </remarks>
public sealed partial class DocxTextExtractor : IDocumentTextExtractor
{
    private const int SectionHeadingLevels = 3;

    public bool CanExtract(KnowledgeFileFormat format) => format == KnowledgeFileFormat.Docx;

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
            throw new ArgumentOutOfRangeException(nameof(format), format, "Not a DOCX.");
        }

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var document = WordprocessingDocument.Open(stream, isEditable: false);
            var main = document.MainDocumentPart;
            var body = main?.Document?.Body
                ?? throw new DocumentExtractionException(DocumentExtractionFailure.Damaged, "The DOCX has no document body.");
            return new SectionReader(new HeadingStyles(main.StyleDefinitionsPart?.Styles), limits, cancellationToken).Read(body);
        }
        catch (Exception exception) when (exception is not (DocumentExtractionException or OperationCanceledException))
        {
            throw new DocumentExtractionException(DocumentExtractionFailure.Damaged, "The Open XML SDK could not read the DOCX.", exception);
        }
    }

    /// <summary>The paragraph's text as displayed (see the remarks for what counts).</summary>
    internal static string ParagraphText(OpenXmlElement paragraph)
    {
        var text = new StringBuilder();
        AppendText(paragraph, text);
        return text.ToString();
    }

    private static void AppendText(OpenXmlElement element, StringBuilder text)
    {
        foreach (var child in element.ChildElements)
        {
            switch (child)
            {
                case Text run:
                    text.Append(run.Text);
                    break;
                case TabChar:
                    text.Append('\t');
                    break;
                case Break or CarriageReturn:
                    text.Append('\n');
                    break;
                case NoBreakHyphen:
                    text.Append('-');
                    break;
                case Paragraph nested:
                    // A text box's (or a table cell's) paragraph: its own line.
                    AppendText(nested, text);
                    text.Append('\n');
                    break;
                case AlternateContentFallback or DeletedRun or MoveFromRun or ParagraphProperties or RunProperties:
                    break;
                default:
                    AppendText(child, text);
                    break;
            }
        }
    }

    [GeneratedRegex(@"^(?:heading|標題|标题|見出し)\s*(?<level>[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingName();

    /// <summary>Resolves outline levels (0-based) through styles and their <c>basedOn</c> chains.</summary>
    private sealed class HeadingStyles
    {
        private const int MaxBasedOnDepth = 16;

        private readonly Dictionary<string, Style> _styles = new(StringComparer.Ordinal);

        public HeadingStyles(Styles? styles)
        {
            foreach (var style in styles?.Elements<Style>() ?? [])
            {
                if (style.StyleId?.Value is { } id && style.Type?.Value == StyleValues.Paragraph)
                {
                    _styles.TryAdd(id, style);
                }
            }
        }

        /// <summary>0 for Heading 1 … 8 for Heading 9; null for body text.</summary>
        public int? OutlineLevel(Paragraph paragraph)
        {
            var properties = paragraph.ParagraphProperties;
            if (properties?.OutlineLevel?.Val?.Value is { } direct)
            {
                return direct is >= 0 and <= 8 ? direct : null;
            }

            return StyleLevel(properties?.ParagraphStyleId?.Val?.Value, 0);
        }

        private int? StyleLevel(string? styleId, int depth)
        {
            if (styleId is null || depth > MaxBasedOnDepth)
            {
                return null;
            }

            if (!_styles.TryGetValue(styleId, out var style))
            {
                return LevelFromName(styleId);
            }

            if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } level)
            {
                return level is >= 0 and <= 8 ? level : null;
            }

            return LevelFromName(style.StyleName?.Val?.Value)
                ?? LevelFromName(styleId)
                ?? StyleLevel(style.BasedOn?.Val?.Value, depth + 1);
        }

        private static int? LevelFromName(string? name)
        {
            if (name is null)
            {
                return null;
            }

            var match = HeadingName().Match(name.Replace(" ", string.Empty, StringComparison.Ordinal).Trim());
            return match.Success ? match.Groups["level"].Value[0] - '1' : null;
        }
    }

    /// <summary>Walks the body once, collecting sections.</summary>
    private sealed class SectionReader
    {
        private readonly HeadingStyles _headings;
        private readonly ExtractionLimits _limits;
        private readonly CancellationToken _cancellationToken;
        private readonly string?[] _path = new string?[SectionHeadingLevels];
        private readonly List<string> _lines = [];
        private readonly List<ExtractedUnit> _units = [];
        private bool _truncated;

        public SectionReader(HeadingStyles headings, ExtractionLimits limits, CancellationToken cancellationToken)
        {
            _headings = headings;
            _limits = limits;
            _cancellationToken = cancellationToken;
        }

        public ExtractedDocument Read(Body body)
        {
            ReadBlocks(body);
            if (!_truncated)
            {
                Flush();
            }

            return new ExtractedDocument(_units, _truncated);
        }

        private void ReadBlocks(OpenXmlElement container)
        {
            foreach (var block in container.ChildElements)
            {
                if (_truncated)
                {
                    return;
                }

                _cancellationToken.ThrowIfCancellationRequested();
                switch (block)
                {
                    case Paragraph paragraph:
                        ReadParagraph(paragraph);
                        break;
                    case Table table:
                        ReadTable(table);
                        break;
                    case SdtBlock or SdtContentBlock or CustomXmlBlock:
                        // Content controls and custom XML wrap ordinary blocks.
                        ReadBlocks(block);
                        break;
                    default:
                        // Section properties, bookmarks, alternate-format chunks: no body text.
                        break;
                }
            }
        }

        private void ReadParagraph(Paragraph paragraph)
        {
            var text = ParagraphText(paragraph);
            if (_headings.OutlineLevel(paragraph) is { } level and < SectionHeadingLevels && ExtractedText.CleanLine(text).Length > 0)
            {
                Flush();
                _path[level] = text;
                Array.Fill(_path, null, level + 1, SectionHeadingLevels - level - 1);
                return;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                _lines.Add(text);
            }
        }

        private void ReadTable(Table table)
        {
            foreach (var row in table.Elements<TableRow>())
            {
                var cells = row.Elements<TableCell>().Select(cell => ExtractedText.CleanLine(ParagraphText(cell))).ToList();
                while (cells.Count > 0 && cells[^1].Length == 0)
                {
                    cells.RemoveAt(cells.Count - 1);
                }

                if (cells.Count > 0)
                {
                    _lines.Add(string.Join(SheetRow.CellSeparator, cells));
                }
            }
        }

        private void Flush()
        {
            var text = string.Join('\n', _lines);
            _lines.Clear();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (_units.Count == _limits.MaxUnits)
            {
                _truncated = true;
                return;
            }

            _units.Add(ExtractedUnit.Section([.. _path.OfType<string>()], text));
        }
    }
}
