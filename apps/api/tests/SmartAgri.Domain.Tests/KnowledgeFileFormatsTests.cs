using Shouldly;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Domain.Tests;

public class KnowledgeFileFormatsTests
{
    [Fact]
    public void The_five_accepted_extensions_in_plan_order()
    {
        KnowledgeFileFormats.Extensions.ShouldBe([".pdf", ".docx", ".xlsx", ".txt", ".md"]);
    }

    [Theory]
    [InlineData("退貨政策.pdf", KnowledgeFileFormat.Pdf)]
    [InlineData("報價.PDF", KnowledgeFileFormat.Pdf)]
    [InlineData("a.b.Docx", KnowledgeFileFormat.Docx)]
    [InlineData("配送時間.xlsx", KnowledgeFileFormat.Xlsx)]
    [InlineData("notes.txt", KnowledgeFileFormat.Text)]
    [InlineData("README.md", KnowledgeFileFormat.Markdown)]
    public void The_extension_names_the_format_in_any_case(string fileName, KnowledgeFileFormat expected)
    {
        KnowledgeFileFormats.TryFromFileName(fileName, out var format).ShouldBeTrue();
        format.ShouldBe(expected);
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("舊版.doc")]
    [InlineData("舊版.xls")]
    [InlineData("簡報.pptx")]
    [InlineData("pdf")]
    [InlineData("檔案.pdf.exe")]
    [InlineData("")]
    public void Anything_else_is_not_a_knowledge_file(string fileName)
    {
        KnowledgeFileFormats.TryFromFileName(fileName, out _).ShouldBeFalse();
    }

    [Fact]
    public void Each_format_has_one_canonical_content_type_that_maps_back()
    {
        foreach (var format in Enum.GetValues<KnowledgeFileFormat>())
        {
            KnowledgeFileFormats.TryFromContentType(KnowledgeFileFormats.ContentType(format), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(format);
        }

        KnowledgeFileFormats.ContentType(KnowledgeFileFormat.Markdown).ShouldBe("text/markdown");
        KnowledgeFileFormats.TryFromContentType("application/octet-stream", out _).ShouldBeFalse();
    }
}
