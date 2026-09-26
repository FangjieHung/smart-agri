using Shouldly;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge.Processing;

public class KnowledgeVersionProcessingTests
{
    private const string PageText = "收到商品後七天內可申請退貨，商品須保持完整包裝與附件。";

    private static readonly ChunkingOptions Options = ChunkingOptions.Default;

    [Fact]
    public void Every_unit_readable_is_ready_without_an_issue_and_every_unit_is_chunked()
    {
        var processed = Process(Page(1, PageText), Page(2, PageText + "生鮮恕不退貨。"));

        processed.Status.ShouldBe(KnowledgeDocumentStatus.Ready);
        processed.Issue.ShouldBeNull();
        processed.Units.Select(unit => (unit.Ordinal, unit.LocationLabel, unit.Readable, unit.IssueCode))
            .ShouldBe([(0, "第 1 頁", true, (KnowledgeUnitIssue?)null), (1, "第 2 頁", true, null)]);
        processed.Units[1].Chunks.ShouldBe([new KnowledgeTextChunk("第 2 頁", PageText + "生鮮恕不退貨。")]);
    }

    [Fact]
    public void Some_unreadable_pages_are_partially_readable_listing_the_pages_and_get_no_chunks()
    {
        var processed = Process(
            Page(1, PageText),
            Page(2, string.Empty),
            Page(3, "第 3 頁"),
            Page(4, "\uE001\uE002\uE003\uE004\uE005\uE006\uE007\uE008\uE009\uE00A\uE00B退貨條件"),
            Page(5, PageText));

        processed.Status.ShouldBe(KnowledgeDocumentStatus.PartiallyReadable);
        processed.Issue.ShouldBe("第 2–4 頁找不到可讀文字，可能是掃描頁；目前不支援 OCR，這些頁不會用於回答");
        processed.Units.Select(unit => unit.IssueCode).ShouldBe(
            [null, KnowledgeUnitIssue.TooLittleText, KnowledgeUnitIssue.TooLittleText, KnowledgeUnitIssue.GarbledText, null]);
        processed.Units.Where(unit => !unit.Readable).ShouldAllBe(unit => unit.Chunks.Count == 0);

        // What was read is kept for the preview, even when unreadable.
        processed.Units[3].Text.ShouldStartWith("\uE001");
    }

    [Fact]
    public void No_readable_unit_fails_with_the_ocr_message_and_keeps_the_units_for_the_preview()
    {
        var processed = Process(Page(1, string.Empty), Page(2, "\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD"));

        processed.Status.ShouldBe(KnowledgeDocumentStatus.Failed);
        processed.Issue.ShouldBe("找不到可讀文字，可能是掃描檔；目前不支援 OCR");
        processed.Units.Count.ShouldBe(2);
        processed.Units.ShouldAllBe(unit => !unit.Readable && unit.Chunks.Count == 0);
    }

    [Fact]
    public void A_file_with_no_units_at_all_fails_with_the_ocr_message()
    {
        var processed = KnowledgeVersionProcessing.Process(new ExtractedDocument([]), ExtractionLimits.Default, Options);

        processed.Status.ShouldBe(KnowledgeDocumentStatus.Failed);
        processed.Issue.ShouldBe(KnowledgeProcessingIssues.NoReadableText);
        processed.Units.ShouldBeEmpty();
    }

    [Fact]
    public void Unreadable_sections_are_listed_by_their_labels()
    {
        var processed = Process(
            ExtractedUnit.Section(["1 商品介紹"], "有機蔬果。"),
            ExtractedUnit.Section(["2 符號"], "\uE001\uE002\uE003退"));

        processed.Status.ShouldBe(KnowledgeDocumentStatus.PartiallyReadable);
        processed.Issue.ShouldBe("2 符號 的文字無法辨識，不會用於回答");
    }

    [Fact]
    public void A_worksheet_cut_at_the_row_limit_stays_readable_but_the_version_is_partially_readable()
    {
        var sheet = new ExtractedSheet("商品價格", new SheetRow(1, "品項 | 售價"), [new SheetRow(2, "高麗菜 | 120")], RowsTruncated: true);

        var processed = KnowledgeVersionProcessing.Process(
            new ExtractedDocument([ExtractedUnit.Worksheet(sheet)]), new ExtractionLimits(10, 1), Options);

        processed.Status.ShouldBe(KnowledgeDocumentStatus.PartiallyReadable);
        processed.Issue.ShouldBe("工作表『商品價格』超過 1 列，只讀取前 1 列");
        var unit = processed.Units.ShouldHaveSingleItem();
        unit.Readable.ShouldBeTrue();
        unit.IssueCode.ShouldBe(KnowledgeUnitIssue.RowsTruncated);
        unit.Chunks.ShouldHaveSingleItem().Text.ShouldBe("品項 | 售價\n高麗菜 | 120");
    }

    [Fact]
    public void A_file_cut_at_the_unit_limit_is_partially_readable()
    {
        var processed = KnowledgeVersionProcessing.Process(
            new ExtractedDocument([Page(1, PageText), Page(2, PageText)], UnitsTruncated: true), new ExtractionLimits(2, 10), Options);

        processed.Status.ShouldBe(KnowledgeDocumentStatus.PartiallyReadable);
        processed.Issue.ShouldBe("檔案超過 2 頁，只讀取前 2 頁");
    }

    [Fact]
    public void Several_problems_are_joined_and_the_issue_never_exceeds_the_column()
    {
        var units = Enumerable.Range(1, 1500).Select(number => Page(number, number % 2 == 0 ? string.Empty : PageText)).ToArray();

        var processed = KnowledgeVersionProcessing.Process(new ExtractedDocument(units, UnitsTruncated: true), new ExtractionLimits(1500, 10), Options);

        processed.Status.ShouldBe(KnowledgeDocumentStatus.PartiallyReadable);
        processed.Issue!.Length.ShouldBe(KnowledgeDocumentVersion.IssueMaxLength);
        processed.Issue.ShouldStartWith("第 2、4、6、");
        processed.Issue.ShouldEndWith("…");
    }

    private static ExtractedUnit Page(int number, string text) => ExtractedUnit.Page(number, text);

    private static ProcessedVersion Process(params ExtractedUnit[] units) =>
        KnowledgeVersionProcessing.Process(new ExtractedDocument(units), ExtractionLimits.Default, Options);
}
