using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Knowledge.Extraction;

namespace SmartAgri.Api.Tests.Knowledge.Extraction;

public class PdfTextExtractorTests
{
    private static readonly PdfTextExtractor Extractor = new(NullLogger<PdfTextExtractor>.Instance);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Reads_pdf_only()
    {
        Enum.GetValues<KnowledgeFileFormat>().Where(Extractor.CanExtract).ShouldBe([KnowledgeFileFormat.Pdf]);
    }

    [Fact]
    public void A_chinese_pdf_is_one_unit_per_page_with_its_text_line_by_line()
    {
        var document = Extract(KnowledgeFixtures.ReturnPolicyPdf);

        document.UnitsTruncated.ShouldBeFalse();
        document.Units.Select(unit => (unit.Kind, unit.LocationLabel, unit.PageNumber)).ShouldBe(
        [
            (KnowledgeUnitLocationKind.Page, "第 1 頁", (int?)1),
            (KnowledgeUnitLocationKind.Page, "第 2 頁", 2),
            (KnowledgeUnitLocationKind.Page, "第 3 頁", 3),
        ]);
        document.Units[0].Text.ShouldBe(KnowledgeFixtures.ReturnPolicyPage1);
        document.Units[1].Text.ShouldBe(KnowledgeFixtures.ReturnPolicyPage2);
        document.Units[2].Text.ShouldStartWith("二、退款方式\n");
    }

    [Fact]
    public void An_image_only_page_has_no_text()
    {
        var document = Extract(KnowledgeFixtures.ScannedPagePdf);

        document.Units.Count.ShouldBe(3);
        document.Units[0].Text.ShouldStartWith("安心商行 配送說明");
        document.Units[1].Text.ShouldBeEmpty();
        document.Units[2].Text.ShouldStartWith("離島地區");
    }

    [Fact]
    public void A_pdf_that_needs_a_password_is_encrypted()
    {
        var failure = Should.Throw<DocumentExtractionException>(() => Extract(KnowledgeFixtures.EncryptedPdf));

        failure.Failure.ShouldBe(DocumentExtractionFailure.Encrypted);
    }

    [Fact]
    public void A_file_that_is_not_really_a_pdf_is_damaged()
    {
        var notPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% nothing else\n%%EOF\n");

        Should.Throw<DocumentExtractionException>(() => Extractor.Extract(KnowledgeFileFormat.Pdf, notPdf, ExtractionLimits.Default, CancellationToken))
            .Failure.ShouldBe(DocumentExtractionFailure.Damaged);
    }

    [Fact]
    public void Only_the_first_pages_up_to_the_unit_limit_are_read()
    {
        var document = Extractor.Extract(
            KnowledgeFileFormat.Pdf, KnowledgeFixtures.Read(KnowledgeFixtures.ReturnPolicyPdf), new ExtractionLimits(2, 10), CancellationToken);

        document.Units.Select(unit => unit.LocationLabel).ShouldBe(["第 1 頁", "第 2 頁"]);
        document.UnitsTruncated.ShouldBeTrue();
    }

    private static ExtractedDocument Extract(string fixture) =>
        Extractor.Extract(KnowledgeFileFormat.Pdf, KnowledgeFixtures.Read(fixture), ExtractionLimits.Default, CancellationToken);
}
