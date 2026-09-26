using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>
/// Why an upload was refused. The wire name is the error response's <c>reason</c>, so the
/// frontend's per-file results (Slice 12) can tell them apart without reading messages.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeUploadRejectionReason>))]
public enum KnowledgeUploadRejectionReason
{
    /// <summary>No file in the request (<c>422</c>).</summary>
    [JsonStringEnumMemberName("file-missing")]
    FileMissing,

    /// <summary>More than one file: uploads are one file per request (<c>422</c>).</summary>
    [JsonStringEnumMemberName("too-many-files")]
    TooManyFiles,

    /// <summary>The request body could not be read as a form (<c>422</c>).</summary>
    [JsonStringEnumMemberName("file-unreadable")]
    FileUnreadable,

    [JsonStringEnumMemberName("invalid-file-name")]
    InvalidFileName,

    /// <summary>A <c>batchId</c> was sent but is not a GUID (<c>422</c>).</summary>
    [JsonStringEnumMemberName("invalid-batch-id")]
    InvalidBatchId,

    /// <summary>Larger than <c>Knowledge:MaxFileBytes</c> (<c>413</c>).</summary>
    [JsonStringEnumMemberName("file-too-large")]
    FileTooLarge,

    /// <summary>The extension is not an accepted format (<c>415</c>).</summary>
    [JsonStringEnumMemberName("unsupported-file-type")]
    UnsupportedFileType,

    /// <summary>The bytes are not what the extension says, e.g. a renamed <c>.exe</c> (<c>415</c>).</summary>
    [JsonStringEnumMemberName("file-content-mismatch")]
    FileContentMismatch,

    /// <summary>The knowledge base already has a version with the same SHA-256 (<c>422</c>).</summary>
    [JsonStringEnumMemberName("duplicate-content")]
    DuplicateContent,

    /// <summary>The knowledge base already has a document with this name (<c>422</c>).</summary>
    [JsonStringEnumMemberName("duplicate-name")]
    DuplicateName,
}

/// <summary>An upload refusal: shown to the owner as <see cref="Message"/>.</summary>
/// <param name="ExistingDocumentName">For <see cref="KnowledgeUploadRejectionReason.DuplicateContent"/>:
/// the document that already has this content.</param>
public sealed record KnowledgeUploadRejection(
    KnowledgeUploadRejectionReason Reason,
    string Message,
    string? ExistingDocumentName = null)
{
    /// <summary>The request field a <c>422</c> names in <c>errors</c>.</summary>
    public string Field => Reason == KnowledgeUploadRejectionReason.InvalidBatchId
        ? KnowledgeUploadRules.BatchIdField
        : KnowledgeUploadRules.FileField;
}

/// <summary>The version that already has an uploaded file's content, for the duplicate rules.</summary>
public sealed record KnowledgeExistingContent(Guid DocumentId, string DocumentName, int VersionNumber);

/// <summary>An uploaded file that passed every check that does not need the database.</summary>
/// <param name="FileName">Normalized (<see cref="KnowledgeUploadRules.NormalizeFileName"/>).</param>
/// <param name="Sha256">Lower-case hex, as stored and compared for duplicates.</param>
public sealed record InspectedKnowledgeFile(
    string FileName,
    KnowledgeFileFormat Format,
    string ContentType,
    long SizeBytes,
    string Sha256);

/// <summary>
/// The upload rules of <c>POST /api/v1/knowledge-bases/{id}/documents</c> (M2 plan, Slice 5),
/// in the order the endpoint applies them:
/// <list type="number">
/// <item>the request carries exactly one file and, optionally, a GUID <c>batchId</c>;</item>
/// <item>size: at most <c>Knowledge:MaxFileBytes</c> (<see cref="CheckSize"/>, applied
/// before the file is even read);</item>
/// <item>name, extension and content (<see cref="Inspect"/>): the extension must be one of
/// <see cref="KnowledgeFileFormats.Extensions"/>, and PDF, DOCX and XLSX files must really
/// be such files (their leading bytes, or the ZIP part that makes them one). TXT and
/// Markdown encoding is checked when the file is processed (Slice 6), not here;</item>
/// <item>duplicates within the knowledge base (<see cref="CheckDuplicates"/>): the same
/// content under any name first, then the same name.</item>
/// </list>
/// Every refusal is a <see cref="KnowledgeUploadRejection"/>; the endpoint maps its reason
/// to the HTTP status and writes nothing.
/// </summary>
public static class KnowledgeUploadRules
{
    public const string FileField = "file";

    public const string BatchIdField = "batchId";

    public const string FileMissingMessage = "請選擇要上傳的檔案。";

    public const string TooManyFilesMessage = "一次只能上傳一個檔案。";

    public const string FileUnreadableMessage = "無法讀取上傳的檔案，請重新上傳。";

    public const string InvalidFileNameMessage = "檔名不正確，請重新命名後再上傳。";

    public const string InvalidBatchIdMessage = "批次代碼格式不正確。";

    public const string UnsupportedFileTypeMessage =
        "只支援 PDF、Word（.docx）、Excel（.xlsx）、純文字（.txt）與 Markdown（.md）檔案。";

    public static readonly string FileNameTooLongMessage =
        $"檔名最多 {KnowledgeDocument.NameMaxLength} 個字，請縮短後再上傳。";

    public static KnowledgeUploadRejection FileMissing { get; } =
        new(KnowledgeUploadRejectionReason.FileMissing, FileMissingMessage);

    public static KnowledgeUploadRejection TooManyFiles { get; } =
        new(KnowledgeUploadRejectionReason.TooManyFiles, TooManyFilesMessage);

    public static KnowledgeUploadRejection FileUnreadable { get; } =
        new(KnowledgeUploadRejectionReason.FileUnreadable, FileUnreadableMessage);

    /// <summary>What a too large upload is told; <paramref name="maxFileBytes"/> is shown in MB.</summary>
    public static KnowledgeUploadRejection FileTooLarge(long maxFileBytes) =>
        new(
            KnowledgeUploadRejectionReason.FileTooLarge,
            $"檔案超過 {FormatMegabytes(maxFileBytes)} MB 的上限，請分割或壓縮後再上傳。");

    /// <summary>The optional <c>batchId</c> form field: absent or blank is no batch.</summary>
    public static KnowledgeUploadCheck<Guid?> ParseBatchId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return KnowledgeUploadCheck<Guid?>.Accept(null);
        }

        return Guid.TryParse(raw.Trim(), out var batchId) && batchId != Guid.Empty
            ? KnowledgeUploadCheck<Guid?>.Accept(batchId)
            : KnowledgeUploadCheck<Guid?>.Reject(new(KnowledgeUploadRejectionReason.InvalidBatchId, InvalidBatchIdMessage));
    }

    /// <summary><see langword="null"/> when <paramref name="sizeBytes"/> is within the limit.</summary>
    public static KnowledgeUploadRejection? CheckSize(long sizeBytes, long maxFileBytes) =>
        sizeBytes > maxFileBytes ? FileTooLarge(maxFileBytes) : null;

    /// <summary>
    /// The name a file is stored and listed under: whatever follows the last <c>/</c> or
    /// <c>\</c> (some clients send a path), in Unicode normalization form C (macOS file
    /// pickers may send decomposed characters, which would otherwise not match the same
    /// name typed elsewhere), trimmed. Must then be 1–<see cref="KnowledgeDocument.NameMaxLength"/>
    /// characters without control characters.
    /// </summary>
    public static KnowledgeUploadCheck<string> NormalizeFileName(string? raw)
    {
        var name = raw ?? string.Empty;
        var separator = name.LastIndexOfAny(['/', '\\']);
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        try
        {
            name = name.Normalize(NormalizationForm.FormC).Trim();
        }
        catch (ArgumentException)
        {
            // Not well-formed UTF-16 (a lone surrogate).
            return KnowledgeUploadCheck<string>.Reject(InvalidFileName(InvalidFileNameMessage));
        }

        if (name.Length == 0 || name.Any(char.IsControl))
        {
            return KnowledgeUploadCheck<string>.Reject(InvalidFileName(InvalidFileNameMessage));
        }

        return name.Length > KnowledgeDocument.NameMaxLength
            ? KnowledgeUploadCheck<string>.Reject(InvalidFileName(FileNameTooLongMessage))
            : KnowledgeUploadCheck<string>.Accept(name);
    }

    /// <summary>
    /// Size, name, extension and content of an uploaded file, in that order, and its
    /// SHA-256. Needs the whole file: the ZIP check reads its central directory, which is
    /// at the end.
    /// </summary>
    public static KnowledgeUploadCheck<InspectedKnowledgeFile> Inspect(string? rawFileName, byte[] content, long maxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (CheckSize(content.LongLength, maxFileBytes) is { } tooLarge)
        {
            return KnowledgeUploadCheck<InspectedKnowledgeFile>.Reject(tooLarge);
        }

        var name = NormalizeFileName(rawFileName);
        if (!name.IsAccepted)
        {
            return KnowledgeUploadCheck<InspectedKnowledgeFile>.Reject(name.Rejection);
        }

        if (!KnowledgeFileFormats.TryFromFileName(name.Value, out var format))
        {
            return KnowledgeUploadCheck<InspectedKnowledgeFile>.Reject(
                new(KnowledgeUploadRejectionReason.UnsupportedFileType, UnsupportedFileTypeMessage));
        }

        if (!ContentMatches(format, content))
        {
            return KnowledgeUploadCheck<InspectedKnowledgeFile>.Reject(
                new(KnowledgeUploadRejectionReason.FileContentMismatch, ContentMismatchMessage(format)));
        }

        return KnowledgeUploadCheck<InspectedKnowledgeFile>.Accept(new InspectedKnowledgeFile(
            name.Value,
            format,
            KnowledgeFileFormats.ContentType(format),
            content.LongLength,
            Sha256(content)));
    }

    /// <summary>
    /// The duplicate rules, given what the knowledge base already holds: a version with the
    /// same SHA-256 (<paramref name="documentWithSameContent"/> is its document's name) is
    /// refused whatever its name; otherwise a document named <paramref name="fileName"/> is
    /// refused with a pointer to uploading a new version instead. <see langword="null"/>
    /// when neither applies.
    /// </summary>
    public static KnowledgeUploadRejection? CheckDuplicates(string fileName, string? documentWithSameContent, bool nameTaken)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        if (documentWithSameContent is not null)
        {
            return new KnowledgeUploadRejection(
                KnowledgeUploadRejectionReason.DuplicateContent,
                $"這份檔案的內容與「{documentWithSameContent}」完全相同，不需要重複上傳。",
                documentWithSameContent);
        }

        return nameTaken
            ? new KnowledgeUploadRejection(
                KnowledgeUploadRejectionReason.DuplicateName,
                $"這個知識庫已經有名為「{fileName}」的文件。要更新它的內容，請改用「上傳新版本」。")
            : null;
    }

    /// <summary>
    /// The duplicate rule for a new version (<c>POST .../documents/{docId}/versions</c>): the
    /// SHA-256 is unique per knowledge base, so content that any version already has is
    /// refused — also an earlier version of the same document (to go back to it, that version
    /// is still there). There is no name rule: the document keeps its name, and the version
    /// keeps its own file name. <see langword="null"/> when <paramref name="existing"/> is.
    /// </summary>
    public static KnowledgeUploadRejection? CheckNewVersionDuplicate(Guid documentId, KnowledgeExistingContent? existing)
    {
        if (existing is null)
        {
            return null;
        }

        var message = existing.DocumentId == documentId
            ? $"這份檔案的內容與這份文件的第 {existing.VersionNumber} 版完全相同，不需要再上傳一次。"
            : $"這份檔案的內容與「{existing.DocumentName}」的第 {existing.VersionNumber} 版完全相同，不能當作這份文件的新版本上傳。";
        return new KnowledgeUploadRejection(KnowledgeUploadRejectionReason.DuplicateContent, message, existing.DocumentName);
    }

    /// <summary>Lower-case hexadecimal SHA-256 of <paramref name="content"/>.</summary>
    public static string Sha256(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>Whether the bytes are really a file of <paramref name="format"/>, as far as
    /// accepting the upload needs to know (processing validates the rest).</summary>
    public static bool ContentMatches(KnowledgeFileFormat format, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return format switch
        {
            KnowledgeFileFormat.Pdf => content.AsSpan().StartsWith("%PDF-"u8),
            KnowledgeFileFormat.Docx => IsZipWithPart(content, "word/document.xml"),
            KnowledgeFileFormat.Xlsx => IsZipWithPart(content, "xl/workbook.xml"),
            KnowledgeFileFormat.Text or KnowledgeFileFormat.Markdown => true,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a declared knowledge file format."),
        };
    }

    private static string ContentMismatchMessage(KnowledgeFileFormat format) => format switch
    {
        KnowledgeFileFormat.Pdf => "檔案內容不是有效的 PDF，可能已損毀，或只是改了副檔名。",
        KnowledgeFileFormat.Docx => "檔案內容不是有效的 Word（.docx）文件，可能已損毀、設有密碼，或只是改了副檔名。",
        KnowledgeFileFormat.Xlsx => "檔案內容不是有效的 Excel（.xlsx）活頁簿，可能已損毀、設有密碼，或只是改了副檔名。",
        _ => UnsupportedFileTypeMessage,
    };

    /// <summary>
    /// A ZIP archive (an Office Open XML package) containing <paramref name="partName"/>.
    /// It must start with a ZIP local file header: an archive appended to something else (a
    /// self-extracting <c>.exe</c>) is not accepted. Only the central directory is read;
    /// nothing is decompressed. A password-protected Office file is not a ZIP at all.
    /// </summary>
    private static bool IsZipWithPart(byte[] content, string partName)
    {
        if (!content.AsSpan().StartsWith("PK\u0003\u0004"u8))
        {
            return false;
        }

        try
        {
            using var archive = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);

            // Part names are case-insensitive in Open Packaging Conventions.
            return archive.Entries.Any(entry => string.Equals(entry.FullName, partName, StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static KnowledgeUploadRejection InvalidFileName(string message) =>
        new(KnowledgeUploadRejectionReason.InvalidFileName, message);

    private static string FormatMegabytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>Either an accepted <typeparamref name="T"/> or why the upload was refused.</summary>
public sealed class KnowledgeUploadCheck<T>
{
    private readonly T? _value;
    private readonly KnowledgeUploadRejection? _rejection;

    private KnowledgeUploadCheck(T? value, KnowledgeUploadRejection? rejection)
    {
        _value = value;
        _rejection = rejection;
    }

    public bool IsAccepted => _rejection is null;

    /// <summary>Throws unless <see cref="IsAccepted"/>.</summary>
    public T Value => IsAccepted
        ? _value!
        : throw new InvalidOperationException("A refused upload has no value; check IsAccepted first.");

    /// <summary>Throws when <see cref="IsAccepted"/>.</summary>
    public KnowledgeUploadRejection Rejection => _rejection
        ?? throw new InvalidOperationException("An accepted upload has no rejection; check IsAccepted first.");

    public static KnowledgeUploadCheck<T> Accept(T value) => new(value, null);

    public static KnowledgeUploadCheck<T> Reject(KnowledgeUploadRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return new(default, rejection);
    }
}
