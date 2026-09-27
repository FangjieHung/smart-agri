using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Shouldly;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Knowledge.Extraction;

namespace SmartAgri.Api.Tests.Knowledge.Extraction;

public class DocxTextExtractorTests
{
    private static readonly DocxTextExtractor Extractor = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Reads_docx_only()
    {
        Enum.GetValues<KnowledgeFileFormat>().Where(Extractor.CanExtract).ShouldBe([KnowledgeFileFormat.Docx]);
    }

    [Fact]
    public void Sections_follow_heading_styles_names_and_outline_levels_and_tables_become_rows()
    {
        var document = Extractor.Extract(
            KnowledgeFileFormat.Docx, KnowledgeFixtures.Read(KnowledgeFixtures.ProductGuideDocx), ExtractionLimits.Default, CancellationToken);

        document.UnitsTruncated.ShouldBeFalse();
        document.Units.ShouldAllBe(unit => unit.Kind == KnowledgeUnitLocationKind.Section);
        document.Units.Select(unit => (unit.LocationLabel, unit.Text)).ShouldBe(
        [
            ("文件開頭", "安心商行 商品指南\n本指南適用於線上商店的所有商品。"),
            ("1 商品介紹", "安心商行販售產地直送的有機蔬果與米糧。"),
            ("1 商品介紹 › 1.1 有機蔬菜箱", "每箱約 3 公斤，內含當季蔬菜 6 至 8 種。\n每週二、五出貨。"),
            ("2 退換貨 › 2.1 退貨條件", "收到商品後七天內可申請退貨；生鮮蔬果恕不退貨。"),
            ("2 退換貨 › 2.1 退貨條件 › 2.1.1 退貨流程", "請先聯繫客服取得退貨編號，再將商品寄回。"),
            ("2 退換貨 › 2.2 運費", "地區 | 運費 | 免運門檻\n本島 | 100 元 | 1,500 元\n離島 | 150 元 | 3,000 元"),
            ("3 聯絡我們", "客服信箱：service@anxin.example"),
        ]);
    }

    [Fact]
    public void Tracked_deletions_field_codes_symbols_and_alternate_content_fallbacks_are_not_read()
    {
        var paragraph = new Paragraph(
            new Run(new Text("退貨") { Space = SpaceProcessingModeValues.Preserve }),
            new InsertedRun(new Run(new Text("期限"))) { Id = "1", Author = "a" },
            new DeletedRun(new Run(new DeletedText("舊條款"))) { Id = "2", Author = "a" },
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" PAGE ")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(new Text("7")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End }),
            new Run(new SymbolChar { Font = "Wingdings", Char = "F0FC" }),
            new Run(new TabChar(), new Text("天"), new Break(), new Text("內")),
            new Run(new AlternateContent(
                new AlternateContentChoice(new Text("選項")) { Requires = "wps" },
                new AlternateContentFallback(new Text("備援")))));

        var text = DocxTextExtractor.ParagraphText(paragraph);

        text.ShouldBe("退貨期限7\t天\n內選項");
    }

    [Fact]
    public void Content_controls_are_read_and_sections_stop_at_the_unit_limit()
    {
        var content = Docx(
            Heading("一"),
            new SdtBlock(new SdtContentBlock(Body("甲"))),
            Heading("二"),
            Body("乙"),
            Heading("三"),
            Body("丙"));

        var document = Extractor.Extract(KnowledgeFileFormat.Docx, content, new ExtractionLimits(2, 10), CancellationToken);

        document.Units.Select(unit => (unit.LocationLabel, unit.Text)).ShouldBe([("一", "甲"), ("二", "乙")]);
        document.UnitsTruncated.ShouldBeTrue();
    }

    [Fact]
    public void A_level_four_heading_and_localized_heading_names_behave_as_word_does()
    {
        var content = Docx(
            [
                Style("標題1", "標題 1"),
                Style("Heading4", "heading 4"),
                Style("Custom", "章節", basedOn: "標題1"),
            ],
            Heading("第一章", "標題1"),
            Heading("細節", "Heading4"),
            Body("內文"),
            Heading("第二章", "Custom"),
            Body("更多內文"));

        var document = Extractor.Extract(KnowledgeFileFormat.Docx, content, ExtractionLimits.Default, CancellationToken);

        document.Units.Select(unit => (unit.LocationLabel, unit.Text)).ShouldBe([("第一章", "細節\n內文"), ("第二章", "更多內文")]);
    }

    [Fact]
    public void A_zip_that_is_not_a_word_document_is_damaged()
    {
        Should.Throw<DocumentExtractionException>(() =>
                Extractor.Extract(KnowledgeFileFormat.Docx, KnowledgeFixtures.Read(KnowledgeFixtures.DeliveryAndPricesXlsx), ExtractionLimits.Default, CancellationToken))
            .Failure.ShouldBe(DocumentExtractionFailure.Damaged);
        Should.Throw<DocumentExtractionException>(() =>
                Extractor.Extract(KnowledgeFileFormat.Docx, [0x50, 0x4B, 0x03, 0x04, 0x00], ExtractionLimits.Default, CancellationToken))
            .Failure.ShouldBe(DocumentExtractionFailure.Damaged);
    }

    private static Paragraph Heading(string text, string styleId = "Heading1") =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = styleId }), new Run(new Text(text)));

    private static Paragraph Body(string text) => new(new Run(new Text(text)));

    private static Style Style(string id, string name, string? basedOn = null)
    {
        var style = new Style { Type = StyleValues.Paragraph, StyleId = id };
        style.Append(new StyleName { Val = name });
        if (basedOn is not null)
        {
            style.Append(new BasedOn { Val = basedOn });
        }

        return style;
    }

    private static byte[] Docx(params OpenXmlElement[] blocks) => Docx([Style("Heading1", "heading 1")], blocks);

    private static byte[] Docx(Style[] styles, params OpenXmlElement[] blocks)
    {
        using var buffer = new MemoryStream();
        using (var document = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(styles);
            main.Document = new Document(new DocumentFormat.OpenXml.Wordprocessing.Body(blocks));
        }

        return buffer.ToArray();
    }
}
