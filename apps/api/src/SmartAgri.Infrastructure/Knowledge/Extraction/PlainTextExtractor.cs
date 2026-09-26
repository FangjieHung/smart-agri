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
        string? fence = null;
        var truncated = false;

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fence is not null)
            {
                body.Add(line);
                if (ClosesFence(line, fence))
                {
                    fence = null;
                }

                continue;
            }

            if (FenceOpening().Match(line) is { Success: true } opening)
            {
                fence = opening.Groups["fence"].Value;
                body.Add(line);
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

            body.Add(line);
        }

        if (!truncated && !Flush())
        {
            truncated = true;
        }

        return new ExtractedDocument(units, truncated);

        // Ends the current section; false when it had text but there is no room for it.
        bool Flush()
        {
            var sectionText = string.Join('\n', body);
            body.Clear();
            if (string.IsNullOrWhiteSpace(sectionText))
            {
                return true;
            }

            if (units.Count == limits.MaxUnits)
            {
                return false;
            }

            units.Add(ExtractedUnit.Section([.. headings.OfType<string>()], sectionText));
            return true;
        }
    }

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
}
