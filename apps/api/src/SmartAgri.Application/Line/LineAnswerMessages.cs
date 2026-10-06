using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SmartAgri.Application.Answers;

namespace SmartAgri.Application.Line;

/// <summary>
/// The LINE messages of a LINE answer (M5b plan §3 G, decision 4) and the fixed replies of LINE
/// questions. LINE renders neither Markdown nor links in a text message, so everything here is plain
/// text; the limits are LINE's (research notes §4): a text message at most
/// <see cref="TextMaxLength"/> UTF-16 code units, a Flex bubble at most <see cref="BubbleMaxBytes"/>
/// and a carousel at most <see cref="CarouselMaxBytes"/> of JSON, an <c>altText</c> at most
/// <see cref="AltTextMaxLength"/> characters.
/// </summary>
/// <remarks>
/// <para>
/// <b>The answer</b> is one text message: for <c>company-data</c> the reply text with each run of
/// citation markers (<c>[1]</c>, <c>[2][1]</c>) written as 「（來源 1）」「（來源 2、1）」 — or removed when
/// the assistant hides its sources —; for <c>general-knowledge</c> the text and then
/// <see cref="GroundedReply.GeneralKnowledgeNotice"/>; for <c>no-result</c> the refusal message and then
/// <see cref="GroundedReply.VisitorNoResultNextSteps"/> (a LINE user, like a website visitor, cannot
/// reach the assistant's manager). Markdown emphasis, headings and code marks the model may write are
/// removed. Longer than <see cref="TextMaxLength"/>: cut, never inside a surrogate pair, and ended with
/// 「…」.
/// </para>
/// <para>
/// <b>The sources</b> (only for <c>company-data</c> with citations, and only when the assistant shows
/// its citations) are a second message, a Flex carousel: one bubble per cited document in order of
/// first citation, at most <see cref="MaxBubbles"/>, each with the citation numbers it stands for, the
/// knowledge base, the document and the first citation's excerpt (at most
/// <see cref="ExcerptMaxLength"/> characters). Its <c>altText</c> (what notifications and old clients
/// show) is 「參考來源：文件 A、文件 B」. Should a bubble or the carousel exceed LINE's size limits, the
/// excerpts are shortened step by step, and only then are bubbles dropped from the end.
/// </para>
/// </remarks>
public static partial class LineAnswerMessages
{
    /// <summary>A LINE text message's limit, in UTF-16 code units.</summary>
    public const int TextMaxLength = 5_000;

    /// <summary>Bubbles in the sources carousel (LINE allows 12).</summary>
    public const int MaxBubbles = 5;

    /// <summary>A bubble's excerpt, in Unicode scalars (as <see cref="GroundedCitation.ExcerptMaxLength"/>).</summary>
    public const int ExcerptMaxLength = 200;

    /// <summary>A knowledge base or document name in a bubble, in Unicode scalars.</summary>
    public const int NameMaxLength = 100;

    /// <summary>A Flex message's <c>altText</c> limit, in UTF-16 code units.</summary>
    public const int AltTextMaxLength = 1_500;

    /// <summary>LINE's limit on one bubble's JSON (30 KB; counted here as 30,000 bytes of the JSON as it
    /// is sent, non-ASCII escaped).</summary>
    public const int BubbleMaxBytes = 30_000;

    /// <summary>LINE's limit on a carousel's JSON (50 KB; 50,000 bytes as above).</summary>
    public const int CarouselMaxBytes = 50_000;

    /// <summary>The reply when the channel is not serving (paused, suspended): no model call.</summary>
    public const string NotServingReply = "目前暫停服務";

    /// <summary>The reply, once per partition and window, when a rate limit refuses a question.</summary>
    public const string RateLimitedReply = "問題太頻繁了，請稍後再試";

    /// <summary>The reply when the model or the search could not answer (not configured, failed).</summary>
    public const string FailedReply = "目前無法回答，請稍後再試。";

    /// <summary>What ends a cut text or name.</summary>
    public const string Ellipsis = "…";

    private const string AltTextPrefix = "參考來源：";

    private static readonly int[] ExcerptSteps = [ExcerptMaxLength, 120, 60, 0];

    /// <summary>The messages answering with <paramref name="reply"/>: the text, then the sources when
    /// <paramref name="showCitations"/> and the reply cites any.</summary>
    public static IReadOnlyList<JsonObject> Build(GroundedReply reply, bool showCitations) =>
        Build(reply, showCitations, BubbleMaxBytes, CarouselMaxBytes);

    /// <summary><see cref="Build(GroundedReply, bool)"/> with other size limits (for tests of the
    /// shrinking steps).</summary>
    public static IReadOnlyList<JsonObject> Build(GroundedReply reply, bool showCitations, int bubbleMaxBytes, int carouselMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var messages = new List<JsonObject> { LineMessages.Text(AnswerText(reply, showCitations)) };
        if (showCitations
            && reply.Kind == GroundedReplyKind.CompanyData
            && Sources(reply.Citations, bubbleMaxBytes, carouselMaxBytes) is { } sources)
        {
            messages.Add(sources);
        }

        return messages;
    }

    /// <summary>The answer's text message content (see the remarks), at most
    /// <see cref="TextMaxLength"/> UTF-16 code units.</summary>
    public static string AnswerText(GroundedReply reply, bool showCitations)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var text = reply.Kind switch
        {
            GroundedReplyKind.CompanyData => showCitations ? ReadableCitations(PlainText(reply.Text)) : CitationMarkers.Strip(PlainText(reply.Text)),
            GroundedReplyKind.GeneralKnowledge => Paragraphs(PlainText(reply.Text), reply.Notice ?? GroundedReply.GeneralKnowledgeNotice),
            GroundedReplyKind.NoResult => Paragraphs([reply.Text, .. GroundedReply.VisitorNoResultNextSteps]),
            _ => throw new ArgumentOutOfRangeException(nameof(reply), reply.Kind, null),
        };

        return Truncate(text.Length == 0 ? reply.Text : text, TextMaxLength);
    }

    /// <summary>Each run of citation markers as 「（來源 n、m）」, with no space before it:
    /// 「七天內可退貨 [1][2]。」 → 「七天內可退貨（來源 1、2）。」.</summary>
    public static string ReadableCitations(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CitationRun().Replace(text, match =>
        {
            var numbers = match.Groups["n"].Captures.Select(capture => capture.Value).Distinct();
            return "（來源 " + string.Join("、", numbers) + "）";
        });
    }

    /// <summary><paramref name="text"/> if it is at most <paramref name="maxLength"/> UTF-16 code
    /// units; otherwise its beginning (never half a surrogate pair) and 「…」, exactly
    /// <paramref name="maxLength"/> units or one fewer.</summary>
    public static string Truncate(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, Ellipsis.Length + 1);
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = maxLength - Ellipsis.Length;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + Ellipsis;
    }

    /// <summary>The sources carousel, or <see langword="null"/> when there is nothing to show.</summary>
    private static JsonObject? Sources(IReadOnlyList<GroundedCitation> citations, int bubbleMaxBytes, int carouselMaxBytes)
    {
        var documents = citations
            .GroupBy(citation => citation.DocumentId)
            .Select(group => group.OrderBy(citation => citation.Ordinal).ToList())
            .OrderBy(group => group[0].Ordinal)
            .Take(MaxBubbles)
            .ToList();
        if (documents.Count == 0)
        {
            return null;
        }

        foreach (var excerptLength in ExcerptSteps)
        {
            var bubbles = documents.Select(group => Bubble(group, excerptLength)).ToList();
            while (bubbles.Count > 0)
            {
                var carousel = Carousel(documents.Take(bubbles.Count), bubbles);
                if (bubbles.All(bubble => Bytes(bubble) <= bubbleMaxBytes) && Bytes(carousel["contents"]!) <= carouselMaxBytes)
                {
                    return carousel;
                }

                if (excerptLength != 0)
                {
                    break;
                }

                // Excerpts gone and still too big: fewer bubbles.
                bubbles.RemoveAt(bubbles.Count - 1);
            }
        }

        return null;
    }

    private static JsonObject Carousel(IEnumerable<List<GroundedCitation>> documents, List<JsonObject> bubbles)
    {
        var names = string.Join("、", documents.Select(group => Shorten(group[0].DocumentName, NameMaxLength)));
        return new JsonObject
        {
            ["type"] = "flex",
            ["altText"] = Truncate(AltTextPrefix + names, AltTextMaxLength),
            ["contents"] = new JsonObject
            {
                ["type"] = "carousel",
                ["contents"] = new JsonArray([.. bubbles.Select(bubble => (JsonNode)bubble.DeepClone())]),
            },
        };
    }

    private static JsonObject Bubble(List<GroundedCitation> group, int excerptLength)
    {
        var first = group[0];
        var contents = new JsonArray
        {
            FlexText("來源 " + string.Join("、", group.Select(citation => citation.Ordinal.ToString(CultureInfo.InvariantCulture))), "xs", "#888888"),
            FlexText(Shorten(first.KnowledgeBaseName, NameMaxLength), "xs", "#888888"),
            FlexText(Shorten(first.DocumentName, NameMaxLength), "sm", null, bold: true),
        };
        if (excerptLength > 0 && Shorten(first.Excerpt, excerptLength) is { Length: > 0 } excerpt)
        {
            contents.Add(FlexText(excerpt, "sm", "#555555"));
        }

        return new JsonObject
        {
            ["type"] = "bubble",
            ["size"] = "kilo",
            ["body"] = new JsonObject
            {
                ["type"] = "box",
                ["layout"] = "vertical",
                ["spacing"] = "sm",
                ["contents"] = contents,
            },
        };
    }

    private static JsonObject FlexText(string text, string size, string? color, bool bold = false)
    {
        var node = new JsonObject
        {
            ["type"] = "text",
            // A Flex text component must not be empty.
            ["text"] = text.Length == 0 ? "—" : text,
            ["size"] = size,
            ["wrap"] = true,
        };
        if (color is not null)
        {
            node["color"] = color;
        }

        if (bold)
        {
            node["weight"] = "bold";
        }

        return node;
    }

    /// <summary>The JSON's size as sent (<c>Utf8JsonWriter</c>'s default escaping, as
    /// <c>LineMessagingClient</c> writes it).</summary>
    private static int Bytes(JsonNode node) => Encoding.UTF8.GetByteCount(node.ToJsonString());

    /// <summary>The first <paramref name="maxScalars"/> Unicode scalars of <paramref name="text"/>
    /// (trimmed), and 「…」 when cut.</summary>
    private static string Shorten(string text, int maxScalars)
    {
        var trimmed = (text ?? string.Empty).Trim();
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (count == maxScalars)
            {
                return builder.ToString().TrimEnd() + Ellipsis;
            }

            builder.Append(rune.ToString());
            count++;
        }

        return trimmed;
    }

    private static string Paragraphs(params string[] parts) =>
        string.Join("\n\n", parts.Select(part => part.Trim()).Where(part => part.Length > 0));

    /// <summary>Removes the Markdown marks LINE would show literally: <c>**</c>/<c>__</c> emphasis,
    /// <c>`</c> code marks and leading <c>#</c> heading marks.</summary>
    private static string PlainText(string text)
    {
        var withoutEmphasis = text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);
        return HeadingMark().Replace(withoutEmphasis, string.Empty).Trim();
    }

    /// <summary>One or more adjacent half-width markers (what <see cref="GroundedReply.Text"/> of a
    /// <c>company-data</c> reply carries after renumbering), with the spaces before them.</summary>
    [GeneratedRegex(@"[ \t]*(?:\[(?<n>[0-9]+)\])+")]
    private static partial Regex CitationRun();

    [GeneratedRegex(@"^[ \t]*#{1,6}[ \t]+", RegexOptions.Multiline)]
    private static partial Regex HeadingMark();
}
