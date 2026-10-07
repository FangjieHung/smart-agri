using System.Text;
using System.Text.RegularExpressions;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Infrastructure.Knowledge.Extraction;

/// <summary>
/// TXT and Markdown. Both must be valid UTF-8, with or without a byte order mark; anything
/// else (Big5, UTF-16, a binary file) is <see cref="DocumentExtractionFailure.NotUtf8"/> —
/// guessing an encoding would silently store garbage (M2 plan §4). A TXT file is one unit
/// (「全文」); Markdown is split into sections at its ATX headings (<c>#</c>, <c>##</c>,
/// <c>###</c>; deeper headings stay in the text), ignoring <c>#</c> lines inside fenced code
/// blocks. The Markdown itself is kept as written.
/// </summary>
/// <remarks>
/// A Markdown section's pipe tables (GitHub Flavored Markdown: a header row, then a delimiter
/// row of <c>---</c> cells, <c>:</c> for alignment, as many as the header has) are also read as
/// tables (<see cref="ExtractedTable"/>), so each data row is chunked on its own (#301). Rows
/// continue while lines contain a <c>|</c>; leading and trailing pipes are optional and
/// <c>\|</c> is a pipe inside a cell. Lines that look like a table without that delimiter row,
/// and anything inside a fenced code block, stay text.
/// </remarks>
public sealed partial class PlainTextExtractor : IDocumentTextExtractor
{
    private const int SectionHeadingLevels = 3;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public bool CanExtract(KnowledgeFileFormat format) => format is KnowledgeFileFormat.Text or KnowledgeFileFormat.Markdown;

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
            throw new ArgumentOutOfRangeException(nameof(format), format, "Not a plain-text format.");
        }

        var text = Decode(content);
        if (format == KnowledgeFileFormat.Text)
        {
            return new ExtractedDocument(string.IsNullOrWhiteSpace(text) ? [] : [ExtractedUnit.WholeText(text)]);
        }

        return MarkdownSections(text, limits, cancellationToken);
    }

    private static string Decode(byte[] content)
    {
        // The UTF-8 byte order mark (StrictUtf8.Preamble is empty: it never writes one).
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        var start = content.AsSpan().StartsWith(bom) ? bom.Length : 0;
        try
        {
            return StrictUtf8.GetString(content, start, content.Length - start);
        }
        catch (DecoderFallbackException exception)
        {
            throw new DocumentExtractionException(DocumentExtractionFailure.NotUtf8, "The file is not valid UTF-8.", exception);
        }
    }

    private static ExtractedDocument MarkdownSections(string text, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        var units = new List<ExtractedUnit>();
        var headings = new string?[SectionHeadingLevels];
        var body = new List<string>();
        var fenced = new List<bool>();
        string? fence = null;
        var truncated = false;

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fence is not null)
            {
                Add(line, inFence: true);
                if (ClosesFence(line, fence))
                {
                    fence = null;
                }

                continue;
            }

            if (FenceOpening().Match(line) is { Success: true } opening)
            {
                fence = opening.Groups["fence"].Value;
                Add(line, inFence: true);
                continue;
            }

            if (AtxHeading().Match(line) is { Success: true } heading && heading.Groups["marks"].Length <= SectionHeadingLevels)
            {
                if (!Flush())
                {
                    truncated = true;
                    break;
                }

                var level = heading.Groups["marks"].Length - 1;
                headings[level] = heading.Groups["text"].Value;
                Array.Fill(headings, null, level + 1, SectionHeadingLevels - level - 1);
                continue;
            }

            Add(line, inFence: false);
        }

        if (!truncated && !Flush())
        {
            truncated = true;
        }

        return new ExtractedDocument(units, truncated);

        void Add(string line, bool inFence)
        {
            body.Add(line);
            fenced.Add(inFence);
        }

        // Ends the current section; false when it had text but there is no room for it.
        bool Flush()
        {
            var sectionText = string.Join('\n', body);
            var (tables, outside) = Tables(body, fenced);
            body.Clear();
            fenced.Clear();
            if (string.IsNullOrWhiteSpace(sectionText))
            {
                return true;
            }

            if (units.Count == limits.MaxUnits)
            {
                return false;
            }

            units.Add(ExtractedUnit.Section([.. headings.OfType<string>()], sectionText, string.Join('\n', outside), tables));
            return true;
        }
    }

    /// <summary>The section's pipe tables, and its lines that are not part of one.</summary>
    private static (List<ExtractedTable> Tables, List<string> Outside) Tables(List<string> lines, List<bool> fenced)
    {
        var tables = new List<ExtractedTable>();
        var outside = new List<string>(lines.Count);
        var index = 0;
        while (index < lines.Count)
        {
            if (!fenced[index]
                && index + 1 < lines.Count
                && !fenced[index + 1]
                && TableRowCells(lines[index]) is { } header
                && IsDelimiterRow(lines[index + 1], header.Count))
            {
                var rows = new List<IReadOnlyList<string>>();
                index += 2;
                while (index < lines.Count && !fenced[index] && TableRowCells(lines[index]) is { } cells)
                {
                    rows.Add(cells);
                    index++;
                }

                if (rows.Count > 0)
                {
                    tables.Add(new ExtractedTable(header, rows));
                }

                continue;
            }

            outside.Add(lines[index]);
            index++;
        }

        return (tables, outside);
    }

    /// <summary>A table row's cells, or null for a line that cannot be one (blank, no pipe, or
    /// indented as code).</summary>
    private static List<string>? TableRowCells(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t'))
        {
            return null;
        }

        var trimmed = line.Trim();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var pipes = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var character = trimmed[i];
            if (character == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (character == '|')
            {
                pipes++;
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(character);
            }
        }

        if (pipes == 0)
        {
            return null;
        }

        cells.Add(cell.ToString().Trim());

        // The optional leading and trailing pipes leave an empty cell outside them.
        if (trimmed[0] == '|')
        {
            cells.RemoveAt(0);
        }

        if (trimmed.Length > 1 && trimmed[^1] == '|' && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells.Count > 0 ? cells : null;
    }

    private static bool IsDelimiterRow(string line, int columns) =>
        TableRowCells(line) is { } cells
        && cells.Count == columns
        && cells.All(cell => DelimiterCell().IsMatch(cell));

    private static bool ClosesFence(string line, string fence)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= fence.Length && trimmed.All(character => character == fence[0]);
    }

    /// <summary>CommonMark ATX heading: up to three spaces, 1–6 <c>#</c>, then a space or the
    /// end of the line; an optional closing run of <c>#</c> is not part of the text.</summary>
    [GeneratedRegex(@"^ {0,3}(?<marks>#{1,6})(?:[ \t]+(?<text>.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})")]
    private static partial Regex FenceOpening();

    [GeneratedRegex(@"^:?-+:?$")]
    private static partial Regex DelimiterCell();
}
