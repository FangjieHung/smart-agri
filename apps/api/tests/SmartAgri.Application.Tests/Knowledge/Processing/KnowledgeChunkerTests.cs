using System.Globalization;
using System.Text;
using Shouldly;
using SmartAgri.Application.Knowledge.Processing;

namespace SmartAgri.Application.Tests.Knowledge.Processing;

public class KnowledgeChunkerTests
{
    private static readonly ChunkingOptions Options = ChunkingOptions.Default;

    [Fact]
    public void The_plans_sizes_are_the_defaults()
    {
        Options.TargetCharacters.ShouldBe(600);
        Options.MaxCharacters.ShouldBe(1000);
        Options.OverlapCharacters.ShouldBe(100);
        Options.MinimumCharacters.ShouldBe(400);
    }

    [Fact]
    public void A_text_up_to_the_maximum_is_one_chunk_and_one_character_more_is_two()
    {
        var atMaximum = new string('字', 1000);
        KnowledgeChunker.SplitText(atMaximum, Options).ShouldBe([atMaximum]);

        var oneMore = new string('字', 1001);
        KnowledgeChunker.SplitText(oneMore, Options).Count.ShouldBe(2);
    }

    [Fact]
    public void White_space_only_has_no_chunks()
    {
        KnowledgeChunker.SplitText(string.Empty, Options).ShouldBeEmpty();
        KnowledgeChunker.SplitText(" \n\t\u3000", Options).ShouldBeEmpty();
    }

    [Fact]
    public void Long_chinese_text_splits_into_chunks_of_at_most_1000_with_exactly_100_characters_of_overlap()
    {
        var text = ChineseProse(sentences: 260);
        text.Length.ShouldBeGreaterThan(4000);

        var chunks = KnowledgeChunker.SplitText(text, Options);

        chunks.Count.ShouldBeGreaterThan(5);
        chunks.ShouldAllBe(chunk => Runes(chunk) <= 1000);
        chunks.Take(chunks.Count - 1).ShouldAllBe(chunk => Runes(chunk) >= 400);
        for (var i = 1; i < chunks.Count; i++)
        {
            var previous = chunks[i - 1];
            chunks[i].ShouldStartWith(previous[^100..], Case.Sensitive, $"chunk {i} repeats the last 100 characters of chunk {i - 1}");
        }

        // Nothing is lost or reordered: the chunks minus their overlaps are the text.
        string.Concat(chunks.Select((chunk, index) => index == 0 ? chunk : chunk[100..])).ShouldBe(text);
    }

    [Fact]
    public void A_chunk_ends_at_a_sentence_end_near_600_rather_than_mid_sentence()
    {
        var text = ChineseProse(sentences: 120);

        var first = KnowledgeChunker.SplitText(text, Options)[0];

        first.ShouldEndWith("。");
        Runes(first).ShouldBeInRange(550, 650);
    }

    [Fact]
    public void A_paragraph_break_beats_a_nearer_sentence_end()
    {
        // Sentences everywhere, but one blank line at 450: the paragraph wins even though a
        // sentence end sits closer to 600.
        var beforeBreak = new string('甲', 449) + "。";
        var afterBreak = string.Concat(Enumerable.Repeat("乙乙乙乙乙乙乙乙乙。", 100));
        var text = beforeBreak + "\n\n" + afterBreak;

        var first = KnowledgeChunker.SplitText(text, Options)[0];

        first.ShouldBe(beforeBreak);
    }

    [Fact]
    public void Without_any_break_a_chunk_is_cut_at_exactly_600()
    {
        var text = new string('字', 2500);

        var chunks = KnowledgeChunker.SplitText(text, Options);

        chunks[0].Length.ShouldBe(600);
        chunks[1].Length.ShouldBe(600);
    }

    [Fact]
    public void Characters_are_counted_as_unicode_scalars_so_supplementary_cjk_counts_once_and_is_never_split()
    {
        // U+20000 (CJK Extension B) is two UTF-16 code units.
        const string Extension = "\U00020000";
        var thousand = string.Concat(Enumerable.Repeat(Extension, 1000));
        thousand.Length.ShouldBe(2000);
        KnowledgeChunker.SplitText(thousand, Options).ShouldBe([thousand]);

        var chunks = KnowledgeChunker.SplitText(string.Concat(Enumerable.Repeat(Extension, 2500)), Options);

        chunks[0].Length.ShouldBe(1200, "600 characters of two code units each");
        chunks.ShouldAllBe(chunk => IsWellFormed(chunk));
        chunks.ShouldAllBe(chunk => Runes(chunk) <= 1000);
        chunks[1].ShouldStartWith(chunks[0][^200..]);
    }

    [Fact]
    public void Chunks_never_cross_units()
    {
        // Two long sections of different characters: no chunk may mix them.
        var first = ExtractedUnit.Section(["一"], new string('甲', 1500));
        var second = ExtractedUnit.Section(["二"], new string('乙', 1500));

        var processed = KnowledgeVersionProcessing.Process(new ExtractedDocument([first, second]), ExtractionLimits.Default, Options);

        processed.Units[0].Chunks.ShouldAllBe(chunk => chunk.Text.All(character => character == '甲') && chunk.LocationLabel == "一");
        processed.Units[1].Chunks.ShouldAllBe(chunk => chunk.Text.All(character => character == '乙') && chunk.LocationLabel == "二");
        processed.Units.Sum(unit => unit.Chunks.Count).ShouldBe(4);
    }

    [Fact]
    public void Every_worksheet_chunk_starts_with_the_header_row_and_names_its_rows()
    {
        var header = new SheetRow(1, "品項 | 重量 | 單位 | 售價 | 單位");
        var rows = Enumerable.Range(2, 120)
            .Select(number => new SheetRow(number, string.Create(CultureInfo.InvariantCulture, $"有機蔬菜第{number}號 | 1.5 | 公斤 | {number * 10} | 元")))
            .ToList();
        var unit = ExtractedUnit.Worksheet(new ExtractedSheet("商品價格", header, rows));

        var chunks = KnowledgeChunker.Chunk(unit, Options);

        chunks.Count.ShouldBeGreaterThan(3);
        chunks.ShouldAllBe(chunk => chunk.Text.StartsWith(header.Text + "\n", StringComparison.Ordinal));
        chunks.ShouldAllBe(chunk => Runes(chunk.Text) <= 1000);
        chunks.Take(chunks.Count - 1).ShouldAllBe(chunk => Runes(chunk.Text) >= 600);

        // Consecutive, non-overlapping row ranges covering every row once, each chunk's
        // label matching the rows it holds.
        var next = 2;
        foreach (var chunk in chunks)
        {
            var lines = chunk.Text.Split('\n')[1..];
            var last = next + lines.Length - 1;
            chunk.LocationLabel.ShouldBe(next == last
                ? string.Create(CultureInfo.InvariantCulture, $"工作表『商品價格』第 {next} 列")
                : string.Create(CultureInfo.InvariantCulture, $"工作表『商品價格』第 {next}–{last} 列"));
            lines.ShouldBe([.. rows.Where(row => row.RowNumber >= next && row.RowNumber <= last).Select(row => row.Text)]);
            next = last + 1;
        }

        next.ShouldBe(122);
    }

    [Fact]
    public void A_worksheet_with_one_row_or_only_a_header_is_one_chunk_with_a_single_row_label()
    {
        var header = new SheetRow(3, "地區 | 最快到貨 | 單位");

        var oneRow = KnowledgeChunker.Chunk(
            ExtractedUnit.Worksheet(new ExtractedSheet("配送時間", header, [new SheetRow(4, "台北市 | 1 | 天")])), Options);
        oneRow.ShouldBe([new KnowledgeTextChunk("工作表『配送時間』第 4 列", "地區 | 最快到貨 | 單位\n台北市 | 1 | 天")]);

        var headerOnly = KnowledgeChunker.Chunk(ExtractedUnit.Worksheet(new ExtractedSheet("配送時間", header, [])), Options);
        headerOnly.ShouldBe([new KnowledgeTextChunk("工作表『配送時間』第 3 列", "地區 | 最快到貨 | 單位")]);
    }

    [Fact]
    public void A_row_too_long_for_one_chunk_is_split_and_every_piece_keeps_the_header_and_its_row_number()
    {
        var header = new SheetRow(1, "品項 | 說明");
        var longRow = new SheetRow(7, "有機米 | " + ChineseProse(sentences: 150));
        var unit = ExtractedUnit.Worksheet(new ExtractedSheet("商品說明", header, [new SheetRow(2, "糙米 | 產地直送"), longRow, new SheetRow(8, "白米 | 產地直送")]));

        var chunks = KnowledgeChunker.Chunk(unit, Options);

        chunks.ShouldAllBe(chunk => chunk.Text.StartsWith("品項 | 說明\n", StringComparison.Ordinal) && Runes(chunk.Text) <= 1000);
        chunks[0].LocationLabel.ShouldBe("工作表『商品說明』第 2 列");
        chunks[^1].LocationLabel.ShouldBe("工作表『商品說明』第 8 列");
        chunks.Skip(1).SkipLast(1).ShouldAllBe(chunk => chunk.LocationLabel == "工作表『商品說明』第 7 列");
        chunks.Count.ShouldBeGreaterThan(3);
    }

    [Fact]
    public void Options_refuse_an_overlap_that_would_not_let_splitting_move_forward()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ChunkingOptions(600, 1000, 400));
        Should.Throw<ArgumentOutOfRangeException>(() => new ChunkingOptions(600, 500, 100));
        Should.NotThrow(() => new ChunkingOptions(600, 1000, 399));
    }

    /// <summary>Sentences of 10–27 characters, each ending with 。 and some with ，.</summary>
    internal static string ChineseProse(int sentences)
    {
        string[] parts = ["收到商品後七天內可申請退貨", "商品須保持完整包裝與附件", "生鮮蔬果恕不接受退貨", "請於到貨當日拍照並聯繫客服", "我們將補寄或退款"];
        var text = new StringBuilder();
        for (var i = 0; i < sentences; i++)
        {
            text.Append(parts[i % parts.Length]);
            text.Append(i % 3 == 0 ? "，" + parts[(i + 2) % parts.Length] + "。" : "。");
        }

        return text.ToString();
    }

    private static int Runes(string text) => text.EnumerateRunes().Count();

    private static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
