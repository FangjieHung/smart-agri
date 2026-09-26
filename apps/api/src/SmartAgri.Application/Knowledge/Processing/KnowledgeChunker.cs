using System.Text;

namespace SmartAgri.Application.Knowledge.Processing;

/// <summary>
/// Chunk sizes, in characters (Unicode scalar values, so one Chinese character is one; M2
/// plan §4: about 600, at most 1000, 100 overlapping).
/// </summary>
public sealed record ChunkingOptions
{
    public ChunkingOptions(int targetCharacters, int maxCharacters, int overlapCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetCharacters, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, targetCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapCharacters);

        // A chunk that ends early at a natural break is still at least two thirds of the
        // target, which must exceed the overlap or splitting would not move forward.
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(overlapCharacters, MinimumCharactersFor(targetCharacters));

        TargetCharacters = targetCharacters;
        MaxCharacters = maxCharacters;
        OverlapCharacters = overlapCharacters;
    }

    public static ChunkingOptions Default { get; } = new(600, 1000, 100);

    /// <summary>Where a chunk preferably ends.</summary>
    public int TargetCharacters { get; }

    /// <summary>No chunk is longer.</summary>
    public int MaxCharacters { get; }

    /// <summary>How many characters each chunk of a split text repeats from the end of the
    /// one before it.</summary>
    public int OverlapCharacters { get; }

    /// <summary>The shortest chunk a split ends at a natural break (two thirds of the target);
    /// only the last chunk of a text can be shorter.</summary>
    public int MinimumCharacters => MinimumCharactersFor(TargetCharacters);

    private static int MinimumCharactersFor(int targetCharacters) => targetCharacters * 2 / 3;
}

/// <summary>One chunk: where it is (for citations) and its text.</summary>
public sealed record KnowledgeTextChunk(string LocationLabel, string Text);

/// <summary>
/// Cuts a unit's text into retrievable chunks (M2 plan §4). Chunks never cross a unit: this
/// only ever sees one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Text</b> (pages, sections, whole text): a text of at most
/// <see cref="ChunkingOptions.MaxCharacters"/> is one chunk. A longer one is split into
/// chunks of about <see cref="ChunkingOptions.TargetCharacters"/>, cut at the most natural
/// break between <see cref="ChunkingOptions.MinimumCharacters"/> and the maximum — a blank
/// line, a line break, the end of a sentence (。！？；), a clause (，、：), then any white
/// space, nearest the target within the best kind found — or, with none, hard at the target.
/// Each chunk after the first starts exactly <see cref="ChunkingOptions.OverlapCharacters"/>
/// before the previous one ended. Chunks are exact substrings of the text.
/// </para>
/// <para>
/// <b>Worksheets</b>: whole rows (never a part of one, unless a single row is too long for a
/// chunk), each chunk starting with the header row and labelled with its rows
/// (「工作表『配送時間』第 2–30 列」). Rows are records, not prose, so they do not overlap:
/// every chunk carries the header instead.
/// </para>
/// </remarks>
public static class KnowledgeChunker
{
    private static readonly HashSet<int> SentenceEnds = [.. "。！？；!?;…".Select(character => (int)character)];
    private static readonly HashSet<int> ClosingMarks = [.. "」』）)〕】\"'”’".Select(character => (int)character)];
    private static readonly HashSet<int> ClauseEnds = [.. "，、：,:".Select(character => (int)character)];

    public static IReadOnlyList<KnowledgeTextChunk> Chunk(ExtractedUnit unit, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(options);

        return unit.Sheet is { } sheet
            ? ChunkSheet(sheet, options)
            : [.. SplitText(unit.Text, options).Select(text => new KnowledgeTextChunk(unit.LocationLabel, text))];
    }

    /// <summary>The text's chunks (see the remarks); none for a text that is only white space.</summary>
    public static IReadOnlyList<string> SplitText(string text, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var runes = RuneText.Of(text);
        if (runes.Length <= options.MaxCharacters)
        {
            return [text];
        }

        var chunks = new List<string>();
        var start = 0;
        while (runes.Length - start > options.MaxCharacters)
        {
            var end = FindEnd(runes, start, options);
            chunks.Add(runes.Substring(start, end));
            start = end - options.OverlapCharacters;
        }

        chunks.Add(runes.Substring(start, runes.Length));
        return chunks;
    }

    private static List<KnowledgeTextChunk> ChunkSheet(ExtractedSheet sheet, ChunkingOptions options)
    {
        // A header longer than half a chunk is cut, so rows always have room.
        var header = Shorten(sheet.Header.Text, options.MaxCharacters / 2);
        var headerLength = CountRunes(header);
        var rowBudget = options.MaxCharacters - headerLength - 1;
        var chunks = new List<KnowledgeTextChunk>();
        if (sheet.DataRows.Count == 0)
        {
            chunks.Add(new(KnowledgeLocationLabels.SheetRows(sheet.Name, sheet.Header.RowNumber, sheet.Header.RowNumber), header));
            return chunks;
        }

        var pending = new List<SheetRow>();
        var pendingLength = headerLength;
        foreach (var row in sheet.DataRows)
        {
            var rowLength = CountRunes(row.Text);
            if (rowLength > rowBudget)
            {
                Flush();
                var pieceOptions = new ChunkingOptions(
                    Math.Min(options.TargetCharacters, rowBudget),
                    rowBudget,
                    Math.Min(options.OverlapCharacters, (Math.Min(options.TargetCharacters, rowBudget) * 2 / 3) - 1));
                foreach (var piece in SplitText(row.Text, pieceOptions))
                {
                    chunks.Add(new(KnowledgeLocationLabels.SheetRows(sheet.Name, row.RowNumber, row.RowNumber), header + "\n" + piece));
                }

                continue;
            }

            if (pending.Count > 0 && (pendingLength >= options.TargetCharacters || pendingLength + 1 + rowLength > options.MaxCharacters))
            {
                Flush();
            }

            pending.Add(row);
            pendingLength += 1 + rowLength;
        }

        Flush();
        return chunks;

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            chunks.Add(new(
                KnowledgeLocationLabels.SheetRows(sheet.Name, pending[0].RowNumber, pending[^1].RowNumber),
                string.Join('\n', pending.Select(row => row.Text).Prepend(header))));
            pending.Clear();
            pendingLength = headerLength;
        }
    }

    /// <summary>Where the chunk starting at rune <paramref name="start"/> ends (exclusive), for
    /// a text that does not fit in one chunk from there.</summary>
    private static int FindEnd(RuneText runes, int start, ChunkingOptions options)
    {
        var preferred = start + options.TargetCharacters;

        // Break kinds from most to least natural; within the best kind found, the one nearest
        // the target wins (the earlier on a tie).
        Span<int> best = stackalloc int[BreakKinds];
        best.Fill(-1);
        for (var end = start + options.MinimumCharacters; end <= start + options.MaxCharacters; end++)
        {
            var kind = BreakKind(runes, end);
            if (kind < BreakKinds && (best[kind] < 0 || Math.Abs(end - preferred) < Math.Abs(best[kind] - preferred)))
            {
                best[kind] = end;
            }
        }

        foreach (var end in best)
        {
            if (end >= 0)
            {
                return end;
            }
        }

        return preferred;
    }

    private const int BreakKinds = 5;

    /// <summary>
    /// How natural it is to end a chunk just before rune <paramref name="end"/>: 0 before a
    /// blank line, 1 before a line break, 2 after a sentence end (and any closing marks), 3
    /// after a clause mark, 4 before other white space; <see cref="BreakKinds"/> (not a
    /// break) anywhere else, including right after white space.
    /// </summary>
    private static int BreakKind(RuneText runes, int end)
    {
        var next = runes[end];
        var previous = runes[end - 1];
        if (previous is '\n' || IsWhiteSpace(previous))
        {
            return BreakKinds;
        }

        if (next == '\n')
        {
            return runes[end + 1] == '\n' ? 0 : 1;
        }

        var last = end - 1;
        while (last > 0 && ClosingMarks.Contains(runes[last]))
        {
            last--;
        }

        if (SentenceEnds.Contains(runes[last]) || (runes[last] == '.' && IsWhiteSpace(next)))
        {
            return 2;
        }

        if (ClauseEnds.Contains(previous))
        {
            return 3;
        }

        return IsWhiteSpace(next) ? 4 : BreakKinds;
    }

    private static bool IsWhiteSpace(int codePoint) => Rune.IsValid(codePoint) && Rune.IsWhiteSpace(new Rune(codePoint));

    private static int CountRunes(string text) => RuneText.Of(text).Length;

    private static string Shorten(string text, int maxCharacters)
    {
        var runes = RuneText.Of(text);
        return runes.Length <= maxCharacters ? text : runes.Substring(0, maxCharacters - 1) + "…";
    }

    /// <summary>A text as Unicode scalar values (a lone surrogate counts as one), with where
    /// each starts in the UTF-16 string, so positions are counted in characters and cuts never
    /// split a surrogate pair.</summary>
    private sealed class RuneText
    {
        private readonly string _text;
        private readonly int[] _codePoints;
        private readonly int[] _starts;

        private RuneText(string text, int[] codePoints, int[] starts)
        {
            _text = text;
            _codePoints = codePoints;
            _starts = starts;
        }

        public int Length => _codePoints.Length;

        /// <summary>The code point at <paramref name="index"/>, or -1 outside the text.</summary>
        public int this[int index] => index >= 0 && index < _codePoints.Length ? _codePoints[index] : -1;

        public static RuneText Of(string text)
        {
            var codePoints = new List<int>(text.Length);
            var starts = new List<int>(text.Length + 1);
            var index = 0;
            while (index < text.Length)
            {
                starts.Add(index);
                if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    codePoints.Add(char.ConvertToUtf32(text[index], text[index + 1]));
                    index += 2;
                }
                else
                {
                    codePoints.Add(text[index]);
                    index++;
                }
            }

            starts.Add(text.Length);
            return new RuneText(text, [.. codePoints], [.. starts]);
        }

        /// <summary>Runes <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive).</summary>
        public string Substring(int start, int end) => _text[_starts[start].._starts[end]];
    }
}
