using System.Text;

namespace SmartAgri.Application.Knowledge.Processing;

/// <summary>
/// Cleans text as read from a file before anything judges, stores or chunks it, the same way
/// for every format.
/// </summary>
public static class ExtractedText
{
    /// <summary>
    /// Multi-line text: control characters other than tab and line breaks, and broken UTF-16
    /// (a lone surrogate), become U+FFFD — still garbage to the readability rules, but
    /// storable (PostgreSQL <c>text</c> refuses NUL) and visible in the preview. Line breaks
    /// become <c>\n</c>; the text is NFC-normalized (CJK compatibility ideographs become the
    /// unified ones), each line loses its trailing white space, runs of blank lines shrink to
    /// one, and the whole is trimmed.
    /// </summary>
    public static string Clean(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var sanitized = new StringBuilder(raw.Length);
        var index = 0;
        while (index < raw.Length)
        {
            if (Rune.DecodeFromUtf16(raw.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                sanitized.Append('\uFFFD');
                index += Math.Max(consumed, 1);
                continue;
            }

            index += consumed;
            switch (rune.Value)
            {
                case '\r':
                    // \r\n is one line break; a lone \r is one too.
                    if (index < raw.Length && raw[index] == '\n')
                    {
                        index++;
                    }

                    sanitized.Append('\n');
                    break;
                case '\n' or '\v' or '\f' or 0x2028 or 0x2029:
                    sanitized.Append('\n');
                    break;
                case '\t':
                    sanitized.Append('\t');
                    break;
                default:
                    if (Rune.IsControl(rune))
                    {
                        sanitized.Append('\uFFFD');
                    }
                    else
                    {
                        sanitized.Append(rune.ToString());
                    }

                    break;
            }
        }

        var lines = sanitized.ToString().Normalize(NormalizationForm.FormC).Split('\n');
        var result = new StringBuilder(sanitized.Length);
        var blankLines = 0;
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                blankLines++;
                continue;
            }

            if (result.Length > 0)
            {
                result.Append(blankLines > 0 ? "\n\n" : "\n");
            }

            result.Append(trimmed);
            blankLines = 0;
        }

        return result.ToString().Trim();
    }

    /// <summary>One line (a table cell or row, a heading): <see cref="Clean"/>, with every run
    /// of white space, line breaks included, collapsed to a single space.</summary>
    public static string CleanLine(string raw)
    {
        var cleaned = Clean(raw);
        var result = new StringBuilder(cleaned.Length);
        var inWhiteSpace = false;
        foreach (var character in cleaned)
        {
            if (char.IsWhiteSpace(character))
            {
                inWhiteSpace = true;
                continue;
            }

            if (inWhiteSpace && result.Length > 0)
            {
                result.Append(' ');
            }

            inWhiteSpace = false;
            result.Append(character);
        }

        return result.ToString();
    }
}
