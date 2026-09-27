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
}
