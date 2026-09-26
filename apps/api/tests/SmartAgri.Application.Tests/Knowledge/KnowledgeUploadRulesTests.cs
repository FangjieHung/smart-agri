using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge;

public class KnowledgeUploadRulesTests
{
    private const long Limit = 1024;

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF\n");

    // --- Names -----------------------------------------------------------------------

    [Theory]
    [InlineData("退貨政策.pdf", "退貨政策.pdf")]
    [InlineData("  退貨政策.pdf ", "退貨政策.pdf")]
    [InlineData(@"C:\Users\王小明\Desktop\退貨政策.pdf", "退貨政策.pdf")]
    [InlineData("/tmp/../退貨政策.pdf", "退貨政策.pdf")]
    public void A_name_loses_any_path_and_surrounding_space(string raw, string expected)
    {
        KnowledgeUploadRules.NormalizeFileName(raw).Value.ShouldBe(expected);
    }

    [Fact]
    public void A_decomposed_name_is_stored_composed_so_it_matches_the_same_name_typed_elsewhere()
    {
        var decomposed = "Cafe\u0301.md";

        KnowledgeUploadRules.NormalizeFileName(decomposed).Value.ShouldBe("Café.md");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\folder\\")]
    [InlineData("退貨\n政策.pdf")]
    [InlineData("退貨\u0000.pdf")]
    public void Empty_names_and_names_with_control_characters_are_refused(string? raw)
    {
        var rejection = KnowledgeUploadRules.NormalizeFileName(raw).Rejection;

        rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.InvalidFileName);
        rejection.Field.ShouldBe("file");
        rejection.Message.ShouldBe("檔名不正確，請重新命名後再上傳。");
    }

    [Fact]
    public void A_name_that_is_not_well_formed_unicode_is_refused()
    {
        // A lone surrogate; not an [InlineData] case, which would serialize it away.
        var raw = new string([(char)0xD800, '.', 'p', 'd', 'f']);

        KnowledgeUploadRules.NormalizeFileName(raw).Rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.InvalidFileName);
    }

    [Fact]
    public void Names_are_limited_to_the_document_name_length()
    {
        var longest = new string('名', KnowledgeDocument.NameMaxLength - 4) + ".pdf";
        KnowledgeUploadRules.NormalizeFileName(longest).Value.ShouldBe(longest);

        KnowledgeUploadRules.NormalizeFileName("名" + longest).Rejection.Message.ShouldBe("檔名最多 255 個字，請縮短後再上傳。");
    }

    // --- Batch id ----------------------------------------------------------------------

    [Fact]
    public void A_batch_id_is_optional_but_must_be_a_guid_when_sent()
    {
        var batch = Guid.NewGuid();

        KnowledgeUploadRules.ParseBatchId(null).Value.ShouldBeNull();
        KnowledgeUploadRules.ParseBatchId(" ").Value.ShouldBeNull();
        KnowledgeUploadRules.ParseBatchId($" {batch} ").Value.ShouldBe(batch);

        foreach (var bad in new[] { "batch-1", Guid.Empty.ToString() })
        {
            var rejection = KnowledgeUploadRules.ParseBatchId(bad).Rejection;
            rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.InvalidBatchId);
            rejection.Field.ShouldBe("batchId");
        }
    }

    // --- Size --------------------------------------------------------------------------

    [Fact]
    public void Size_up_to_the_limit_is_fine_and_one_byte_more_is_413_stated_in_megabytes()
    {
        KnowledgeUploadRules.CheckSize(Limit, Limit).ShouldBeNull();

        var rejection = KnowledgeUploadRules.CheckSize(Limit + 1, Limit).ShouldNotBeNull();
        rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.FileTooLarge);

        KnowledgeUploadRules.FileTooLarge(20L * 1024 * 1024).Message.ShouldBe("檔案超過 20 MB 的上限，請分割或壓縮後再上傳。");
        KnowledgeUploadRules.FileTooLarge(1536L * 1024).Message.ShouldContain("1.5 MB");
    }

    // --- Inspect: extension and content ------------------------------------------------

    [Fact]
    public void An_accepted_file_has_its_normalized_name_canonical_type_size_and_sha256()
    {
        var file = KnowledgeUploadRules.Inspect(" 報價.PDF", Pdf, Limit).Value;

        file.ShouldBe(new InspectedKnowledgeFile(
            "報價.PDF",
            KnowledgeFileFormat.Pdf,
            "application/pdf",
            Pdf.Length,
            Convert.ToHexStringLower(SHA256.HashData(Pdf))));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("舊版.doc")]
    [InlineData("圖片.png")]
    [InlineData("沒有副檔名")]
    public void Extensions_outside_the_five_formats_are_415_unsupported(string fileName)
    {
        var rejection = KnowledgeUploadRules.Inspect(fileName, Pdf, Limit).Rejection;

        rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.UnsupportedFileType);
        rejection.Message.ShouldBe("只支援 PDF、Word（.docx）、Excel（.xlsx）、純文字（.txt）與 Markdown（.md）檔案。");
    }

    [Fact]
    public void Office_files_must_be_zip_packages_with_their_main_part()
    {
        KnowledgeUploadRules.Inspect("說明.docx", Zip("word/document.xml"), Limit).IsAccepted.ShouldBeTrue();
        KnowledgeUploadRules.Inspect("配送.xlsx", Zip("xl/workbook.xml"), Limit).IsAccepted.ShouldBeTrue();

        // Part names are case-insensitive in Open Packaging Conventions.
        KnowledgeUploadRules.Inspect("說明.docx", Zip("Word/Document.xml"), Limit).IsAccepted.ShouldBeTrue();

        Mismatch("說明.docx", Zip("xl/workbook.xml")).ShouldBeTrue();
        Mismatch("配送.xlsx", Zip("word/document.xml")).ShouldBeTrue();
        Mismatch("說明.docx", Pdf).ShouldBeTrue();
        Mismatch("說明.docx", [0x50, 0x4B, 0x03, 0x04, 0xFF, 0xFF]).ShouldBeTrue(); // a truncated zip
    }

    [Fact]
    public void A_zip_appended_to_something_else_is_not_an_office_file()
    {
        // e.g. a self-extracting .exe renamed to .docx: ZipArchive would find the archive
        // at the end, so the local file header must come first.
        var selfExtracting = Encoding.ASCII.GetBytes("MZ\u0090\0 stub ").Concat(Zip("word/document.xml")).ToArray();

        Mismatch("說明.docx", selfExtracting).ShouldBeTrue();
    }

    [Fact]
    public void A_pdf_must_start_with_the_pdf_header()
    {
        var exe = new byte[] { (byte)'M', (byte)'Z', 0x90, 0x00, 0x03 };

        var rejection = KnowledgeUploadRules.Inspect("退貨政策.pdf", exe, Limit).Rejection;

        rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.FileContentMismatch);
        rejection.Message.ShouldBe("檔案內容不是有效的 PDF，可能已損毀，或只是改了副檔名。");
        Mismatch("退貨政策.pdf", Encoding.ASCII.GetBytes(" %PDF-1.7")).ShouldBeTrue();
        Mismatch("退貨政策.pdf", []).ShouldBeTrue();
    }

    [Fact]
    public void Text_and_markdown_content_is_left_to_processing()
    {
        // Encoding (UTF-8 only) is checked by the Slice 6 processing, which can say why.
        KnowledgeUploadRules.Inspect("notes.txt", [0xB0, 0x68], Limit).IsAccepted.ShouldBeTrue();
        KnowledgeUploadRules.Inspect("README.md", Encoding.UTF8.GetBytes("# 退貨"), Limit).IsAccepted.ShouldBeTrue();
        KnowledgeUploadRules.Inspect("empty.md", [], Limit).IsAccepted.ShouldBeTrue();
    }

    [Fact]
    public void Inspect_checks_size_then_name_then_extension_then_content()
    {
        var tooLarge = new byte[Limit + 1];
        KnowledgeUploadRules.Inspect("", tooLarge, Limit).Rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.FileTooLarge);
        KnowledgeUploadRules.Inspect("", Pdf, Limit).Rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.InvalidFileName);
        KnowledgeUploadRules.Inspect("a.exe", [0x4D, 0x5A], Limit).Rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.UnsupportedFileType);
        KnowledgeUploadRules.Inspect("a.pdf", [0x4D, 0x5A], Limit).Rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.FileContentMismatch);
    }

    // --- Duplicates --------------------------------------------------------------------

    [Fact]
    public void Same_content_is_refused_first_and_names_the_existing_document()
    {
        var rejection = KnowledgeUploadRules.CheckDuplicates("新檔名.pdf", "退貨政策.pdf", nameTaken: true).ShouldNotBeNull();

        rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.DuplicateContent);
        rejection.ExistingDocumentName.ShouldBe("退貨政策.pdf");
        rejection.Message.ShouldBe("這份檔案的內容與「退貨政策.pdf」完全相同，不需要重複上傳。");
    }

    [Fact]
    public void A_taken_name_points_to_uploading_a_new_version()
    {
        var rejection = KnowledgeUploadRules.CheckDuplicates("退貨政策.pdf", null, nameTaken: true).ShouldNotBeNull();

        rejection.Reason.ShouldBe(KnowledgeUploadRejectionReason.DuplicateName);
        rejection.ExistingDocumentName.ShouldBeNull();
        rejection.Message.ShouldContain("上傳新版本");

        KnowledgeUploadRules.CheckDuplicates("退貨政策.pdf", null, nameTaken: false).ShouldBeNull();
    }

    [Fact]
    public void Reasons_have_kebab_case_wire_names()
    {
        Domain.WireNames<KnowledgeUploadRejectionReason>.All.ShouldBe(
        [
            "file-missing", "too-many-files", "file-unreadable", "invalid-file-name", "invalid-batch-id",
            "file-too-large", "unsupported-file-type", "file-content-mismatch", "duplicate-content", "duplicate-name",
        ]);
    }

    private static bool Mismatch(string fileName, byte[] content) =>
        KnowledgeUploadRules.Inspect(fileName, content, Limit) is { IsAccepted: false } check
        && check.Rejection.Reason == KnowledgeUploadRejectionReason.FileContentMismatch;

    private static byte[] Zip(string partName)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open());
            writer.Write("<Types/>");
            writer.Dispose();
            using var part = new StreamWriter(archive.CreateEntry(partName).Open());
            part.Write("<document/>");
        }

        return buffer.ToArray();
    }
}
