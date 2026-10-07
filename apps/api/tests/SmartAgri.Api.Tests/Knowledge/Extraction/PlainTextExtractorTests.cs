using System.Text;
using Shouldly;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Knowledge.Extraction;

namespace SmartAgri.Api.Tests.Knowledge.Extraction;

public class PlainTextExtractorTests
{
    private static readonly PlainTextExtractor Extractor = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Reads_txt_and_md()
    {
        Enum.GetValues<KnowledgeFileFormat>().Where(Extractor.CanExtract).ShouldBe([KnowledgeFileFormat.Text, KnowledgeFileFormat.Markdown]);
    }

    [Fact]
    public void Markdown_sections_follow_the_headings_skip_empty_ones_and_ignore_hashes_in_code()
    {
        var document = Extractor.Extract(
            KnowledgeFileFormat.Markdown, KnowledgeFixtures.Read(KnowledgeFixtures.FaqMarkdown), ExtractionLimits.Default, CancellationToken);

        document.Units.Select(unit => (unit.Kind, unit.LocationLabel, unit.Text)).ShouldBe(
        [
            (KnowledgeUnitLocationKind.Section, "常見問題", "以下整理顧客最常詢問的問題。"),
            (KnowledgeUnitLocationKind.Section, "常見問題 › 訂購與付款 › 可以使用哪些付款方式？", "信用卡、ATM 轉帳與超商代碼繳費。"),
            (KnowledgeUnitLocationKind.Section, "常見問題 › 訂購與付款 › 可以開立統一編號嗎？", "可以，結帳時填寫統一編號即可。"),
            (KnowledgeUnitLocationKind.Section, "常見問題 › 配送 › 多久會收到商品？",
                "本島約 1 至 2 天，離島約 5 天。\n\n```bash\n# 這一行在程式碼區塊內，不是標題\necho \"tracking\"\n```"),
        ]);
    }

    [Fact]
    public void A_byte_order_mark_is_allowed_and_dropped()
    {
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("退貨期限為七天。")).ToArray();

        var unit = Extractor.Extract(KnowledgeFileFormat.Text, withBom, ExtractionLimits.Default, CancellationToken).Units.ShouldHaveSingleItem();

        unit.LocationLabel.ShouldBe("全文");
        unit.Text.ShouldBe("退貨期限為七天。");
    }

    [Fact]
    public void Big5_and_utf16_are_not_utf8()
    {
        var big5 = KnowledgeFixtures.Read(KnowledgeFixtures.Big5Text);
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("退貨期限")).ToArray();

        foreach (var (format, content) in new[] { (KnowledgeFileFormat.Text, big5), (KnowledgeFileFormat.Markdown, big5), (KnowledgeFileFormat.Text, utf16) })
        {
            Should.Throw<DocumentExtractionException>(() => Extractor.Extract(format, content, ExtractionLimits.Default, CancellationToken))
                .Failure.ShouldBe(DocumentExtractionFailure.NotUtf8);
        }
    }

    [Fact]
    public void An_empty_text_file_has_no_units()
    {
        Extractor.Extract(KnowledgeFileFormat.Text, "\n \n"u8.ToArray(), ExtractionLimits.Default, CancellationToken).Units.ShouldBeEmpty();
    }

    [Fact]
    public void Deeper_headings_stay_in_the_text_and_sections_stop_at_the_unit_limit()
    {
        var markdown = "# 一\n甲\n#### 小標\n乙\n# 二\n丙\n# 三\n丁\n"u8.ToArray();

        var document = Extractor.Extract(KnowledgeFileFormat.Markdown, markdown, new ExtractionLimits(2, 10), CancellationToken);

        document.Units.Select(unit => (unit.LocationLabel, unit.Text)).ShouldBe([("一", "甲\n#### 小標\n乙"), ("二", "丙")]);
        document.UnitsTruncated.ShouldBeTrue();
    }

    // --- #301: pipe tables are also read as tables -----------------------------------------

    [Fact]
    public void Markdown_pipe_tables_are_read_as_tables_and_the_text_around_them_is_kept()
    {
        var markdown = Encoding.UTF8.GetBytes(string.Join('\n',
            "## 基本資訊",
            "門市的基本資料如下。",
            "",
            "| 項目 | 內容 |",
            "| :--- | ---: |",
            "| 地址 | 示範縣青禾鄉安和路 18 號 |",
            "| 電話 |  |",
            "營業時間 | 09:00\\|18:00",
            "",
            "以上資料隨時更新。",
            "",
            "| 單欄 |",
            "|---|",
            "| 甲 |",
            "",
            "| 只有表頭 | 沒有資料 |",
            "| --- | --- |",
            ""));

        var unit = Extractor.Extract(KnowledgeFileFormat.Markdown, markdown, ExtractionLimits.Default, CancellationToken).Units.ShouldHaveSingleItem();

        unit.LocationLabel.ShouldBe("基本資訊");
        unit.Text.ShouldStartWith("門市的基本資料如下。\n\n| 項目 | 內容 |\n| :--- | ---: |", customMessage: "the Markdown is kept as written");
        unit.TextOutsideTables.ShouldBe("門市的基本資料如下。\n\n以上資料隨時更新。", "a header-only table is no table, and no text either");
        unit.Tables.Count.ShouldBe(2);
        unit.Tables[0].Header.ShouldBe(["項目", "內容"]);
        unit.Tables[0].Rows.ShouldBe([["地址", "示範縣青禾鄉安和路 18 號"], ["電話", ""], ["營業時間", "09:00|18:00"]]);
        unit.Tables[1].Header.ShouldBe(["單欄"]);
        unit.Tables[1].Rows.ShouldBe([["甲"]]);
    }

    [Fact]
    public void Pipes_without_a_delimiter_row_or_inside_code_stay_text()
    {
        var markdown = Encoding.UTF8.GetBytes(string.Join('\n',
            "# 運費",
            "地區 | 運費",
            "本島 | 100 元",
            "",
            "| 欄 | 欄 |",
            "| --- |",
            "| 甲 | 乙 |",
            "",
            "```",
            "| a | b |",
            "| - | - |",
            "| 1 | 2 |",
            "```"));

        var unit = Extractor.Extract(KnowledgeFileFormat.Markdown, markdown, ExtractionLimits.Default, CancellationToken).Units.ShouldHaveSingleItem();

        unit.Tables.ShouldBeEmpty("no delimiter row, a delimiter row of the wrong width, or fenced code");
        unit.TextOutsideTables.ShouldBe(unit.Text);
    }

    [Fact]
    public void The_store_sheet_has_a_table_in_three_of_its_sections()
    {
        var document = Extractor.Extract(
            KnowledgeFileFormat.Markdown, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "eval", "retrieval", "files", "store-info.md")), ExtractionLimits.Default, CancellationToken);

        document.Units.Select(unit => (unit.LocationLabel, unit.Tables.Sum(table => table.Rows.Count))).ShouldBe(
        [
            ("青禾門市 AI 客服參考資料", 0),
            ("青禾門市 AI 客服參考資料 › 基本資訊", 5),
            ("青禾門市 AI 客服參考資料 › 外送與團購", 4),
            ("青禾門市 AI 客服參考資料 › 門市餐點", 3),
            ("青禾門市 AI 客服參考資料 › 停車", 0),
        ]);
        document.Units.Single(unit => unit.LocationLabel.EndsWith("門市餐點", StringComparison.Ordinal)).TextOutsideTables
            .ShouldBe("餐點都是單點，沒有套餐；可以內用或外帶。");
    }
}
