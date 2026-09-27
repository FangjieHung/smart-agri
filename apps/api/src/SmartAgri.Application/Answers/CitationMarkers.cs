using System.Text;
using System.Text.RegularExpressions;
using SmartAgri.Application.Ai;

namespace SmartAgri.Application.Answers;

/// <summary>What <see cref="CitationMarkers.Analyze"/> found in an answer.</summary>
/// <param name="Cited">Every valid passage number (1…k) the answer cites, each once, in the
/// order it is first cited.</param>
/// <param name="OutOfRange">Whether any marker names a number outside 1…k.</param>
/// <param name="CannotAnswer">Whether the answer contains
/// <see cref="ChatAnswerMarkers.CannotAnswer"/>.</param>
public sealed record CitationAnalysis(IReadOnlyList<int> Cited, bool OutOfRange, bool CannotAnswer);

/// <summary>
/// Reads the passage citations a model writes into an answer (M3 plan §3, step 5). Lenient about
/// form, strict about numbers (plan §7 risk 2): half-width <c>[n]</c>, full-width <c>［n］</c> and
/// lenticular <c>【n】</c> brackets, full-width digits, spaces inside, and lists such as
/// <c>[1, 2]</c>, <c>[1，2]</c> or <c>[1、2]</c> are all citations; a number outside 1…k is
/// never accepted.
/// </summary>
/// <remarks>
/// Always applied to the whole answer text, never to a single streamed chunk, so a marker split
/// across chunks (<c>[</c> then <c>1]</c>) is read like any other.
/// </remarks>
public static partial class CitationMarkers
{
    /// <summary>More digits than this is not a passage number (and would overflow).</summary>
    private const int MaxDigits = 6;

    /// <summary>Removes every citation marker (and <see cref="ChatAnswerMarkers.CannotAnswer"/>)
    /// from <paramref name="text"/>: what a <c>general-knowledge</c> reply shows, and what earlier
    /// turns are reduced to before they reach the model again (their numbers belonged to another
    /// retrieval).</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var withoutCannotAnswer = text.Replace(ChatAnswerMarkers.CannotAnswer, string.Empty, StringComparison.Ordinal);
        return MarkerWithLeadingSpace().Replace(withoutCannotAnswer, string.Empty).Trim();
    }

    /// <summary>Every citation in <paramref name="text"/>, judged against
    /// <paramref name="passageCount"/> passages (k).</summary>
    public static CitationAnalysis Analyze(string text, int passageCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(passageCount);
        var cited = new List<int>();
        var outOfRange = false;
        foreach (Match match in Marker().Matches(text))
        {
            foreach (var number in Numbers(match))
            {
                if (number < 1 || number > passageCount)
                {
                    outOfRange = true;
                }
                else if (!cited.Contains(number))
                {
                    cited.Add(number);
                }
            }
        }

        return new CitationAnalysis(cited, outOfRange, text.Contains(ChatAnswerMarkers.CannotAnswer, StringComparison.Ordinal));
    }

    /// <summary>
    /// <paramref name="text"/> with every marker rewritten in half-width brackets through
    /// <paramref name="ordinals"/> (passage number → citation ordinal), one <c>[m]</c> per cited
    /// number: <c>【2】…[1, 2]</c> with 2→1, 1→2 becomes <c>[1]…[2][1]</c>. Only for an answer
    /// whose <see cref="Analyze"/> found nothing out of range.
    /// </summary>
    public static string Renumber(string text, IReadOnlyDictionary<int, int> ordinals)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ordinals);
        return Marker().Replace(text, match =>
        {
            var builder = new StringBuilder();
            foreach (var number in Numbers(match))
            {
                builder.Append('[').Append(ordinals[number]).Append(']');
            }

            return builder.ToString();
        });
    }

    private static IEnumerable<int> Numbers(Match match)
    {
        foreach (Capture capture in match.Groups["n"].Captures)
        {
            var digits = new StringBuilder(capture.Length);
            foreach (var character in capture.Value)
            {
                digits.Append(character is >= '０' and <= '９' ? (char)('0' + (character - '０')) : character);
            }

            var value = digits.ToString().TrimStart('0');
            yield return value.Length == 0 ? 0 : value.Length > MaxDigits ? int.MaxValue : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>An opening bracket, one or more numbers separated by commas or 「、」, a closing
    /// bracket (any of the three bracket styles, mixed or not).</summary>
    [GeneratedRegex(@"[\[［【][ \t　]*(?<n>[0-9０-９]+)(?:[ \t　]*[,，、][ \t　]*(?<n>[0-9０-９]+))*[ \t　]*[\]］】]")]
    private static partial Regex Marker();

    /// <summary><see cref="Marker"/> with the spaces before it, so 「七天 [1]。」 strips to 「七天。」.</summary>
    [GeneratedRegex(@"[ \t]*[\[［【][ \t　]*[0-9０-９]+(?:[ \t　]*[,，、][ \t　]*[0-9０-９]+)*[ \t　]*[\]］】]")]
    private static partial Regex MarkerWithLeadingSpace();
}
