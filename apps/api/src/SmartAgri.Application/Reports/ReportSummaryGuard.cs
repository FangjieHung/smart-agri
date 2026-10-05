using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartAgri.Application.Reports;

/// <summary>What <see cref="ReportSummaryGuard.Check"/> found.</summary>
/// <param name="Unverified">The numbers in the text that the facts do not contain, as written; empty
/// when the text is acceptable.</param>
public sealed record ReportSummaryCheck(IReadOnlyList<string> Unverified)
{
    public bool IsAcceptable => Unverified.Count == 0;
}

/// <summary>
/// Keeps the model from changing the statistics (M4 #150, periodic-reports ADR: 統計數字可驗證，
/// 摘要只作輔助). The statistics are saved from the fixed query and never touched by the summary; this
/// guard decides whether the model's <i>text</i> may be shown next to them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Method.</b> Every number written in the text — Arabic digits of any script, with thousands
/// separators and decimals, so <c>1,200</c> equals <c>1200</c> — must also be written somewhere in the
/// facts the model was given (<see cref="ReportSummaryPrompt.Facts"/>: counts, sums, changes, the dates of
/// both periods). A number the facts do not contain — a recomputed percentage, a rounded or altered sum, an
/// invented count — makes the whole summary <b>discarded</b>: it is never shown, the report keeps its
/// statistics and charts, and the summary is marked <c>discarded</c> so it can be retried. A number a
/// person is told is always one the server computed.
/// </para>
/// <para>
/// A number spelled out in Chinese numerals beside a unit (<c>三筆</c>, <c>五成</c>, <c>百分之十</c>) cannot be
/// matched to the facts, so it is treated as unverifiable and discards the summary too. What the guard
/// cannot judge is whether the words describe the numbers well (up or down, which field); that is why the
/// summary is labelled as AI-written and always sits below the statistics.
/// </para>
/// </remarks>
public static partial class ReportSummaryGuard
{
    public static ReportSummaryCheck Check(string summary, string facts)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(facts);

        // A date is checked as a whole: the date parts of the facts (2026, 09, 01) must not make a
        // count of 1 or 9 look verified.
        var knownDates = IsoDate().Matches(AsciiDigits(facts)).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        var known = Numbers(IsoDate().Replace(AsciiDigits(facts), " "))
            .Select(number => number.Normalized)
            .ToHashSet(StringComparer.Ordinal);
        var withoutKnownDates = IsoDate().Replace(
            AsciiDigits(summary), match => knownDates.Contains(match.Value) ? " " : match.Value);
        var unverified = new List<string>();
        foreach (var number in Numbers(withoutKnownDates))
        {
            if (!known.Contains(number.Normalized))
            {
                unverified.Add(number.Written);
            }
        }

        foreach (Match match in SpelledOutNumber().Matches(summary))
        {
            unverified.Add(match.Value);
        }

        return new ReportSummaryCheck([.. unverified.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>The numbers written in <paramref name="text"/>: as written, and normalized (digits of
    /// every script turned into ASCII, thousands separators removed, trailing zeros of a decimal dropped).</summary>
    internal static IEnumerable<(string Written, string Normalized)> Numbers(string text)
    {
        foreach (Match match in NumberToken().Matches(AsciiDigits(text)))
        {
            yield return (match.Value, Normalize(match.Value));
        }
    }

    private static string Normalize(string written)
    {
        var plain = written.Replace(",", string.Empty, StringComparison.Ordinal);
        if (decimal.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return value.ToString("0.#############################", CultureInfo.InvariantCulture);
        }

        return plain;
    }

    /// <summary>Every Unicode decimal digit (full-width, Arabic-Indic, …) as the ASCII digit it stands for.</summary>
    private static string AsciiDigits(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            var digit = char.IsDigit(character) ? CharUnicodeInfo.GetDecimalDigitValue(character) : -1;
            result.Append(digit >= 0 ? (char)('0' + digit) : character);
        }

        return result.ToString();
    }

    [GeneratedRegex(@"\d+(?:,\d{3})*(?:\.\d+)?")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"百分之[零〇一二兩三四五六七八九十百千萬億]+|[零〇一二兩三四五六七八九十百千萬億]+(?=\s*(?:筆|元|件|次|人|成|倍|%|％|公斤))")]
    private static partial Regex SpelledOutNumber();
}
