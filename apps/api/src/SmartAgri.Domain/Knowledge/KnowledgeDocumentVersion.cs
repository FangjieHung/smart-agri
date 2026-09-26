using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// One uploaded file of a <see cref="KnowledgeDocument"/> (table
/// <c>KnowledgeDocumentVersions</c>, M2 plan §4) and where its processing stands. The file's
/// bytes are a separate <see cref="KnowledgeFileContent"/> row, so reading versions never
/// reads files.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="KnowledgeBaseId"/> repeats the document's so the database itself can keep
/// <see cref="Sha256"/> unique per knowledge base (the duplicate-content rule); the
/// composite foreign key to the document includes it, so it can never disagree with the
/// document's. Deleting the document deletes its versions and their files.
/// </para>
/// <para>
/// <see cref="ProcessingStatus"/> is a concurrency token: every status change is saved
/// only if the row still has the status it was read with, so a retry and a background
/// job (or two retries) cannot both act on the same state.
/// </para>
/// <para>
/// Review and approval (<c>ReviewState</c>, <c>EffectiveFrom</c>, <c>ApprovedByAccountId</c>,
/// <c>ApprovedAt</c>) are deliberately not here yet: Slice 8 (#42) adds them together with
/// the rules that set them, with <c>pending-review</c> as the value for every existing
/// version (every version, version 1 included, must be approved; plan §7 decision 4).
/// Processing status and review state stay separate columns.
/// </para>
/// </remarks>
public sealed class KnowledgeDocumentVersion : IOrganizationScoped
{
    public const int FileNameMaxLength = KnowledgeDocument.NameMaxLength;

    public const int ContentTypeMaxLength = 128;

    /// <summary>A SHA-256 digest as lower-case hexadecimal.</summary>
    public const int Sha256Length = 64;

    public const int IssueMaxLength = 1000;

    /// <summary>For EF Core materialization.</summary>
    private KnowledgeDocumentVersion()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public Guid DocumentId { get; private set; }

    /// <summary>1 for the upload that created the document, then 2, 3, … per document.</summary>
    public int VersionNumber { get; private set; }

    /// <summary>The uploaded file's name (normalized), also used when it is downloaded.</summary>
    public string FileName { get; private set; } = string.Empty;

    /// <summary>The canonical content type of the file's format
    /// (<see cref="KnowledgeFileFormats.ContentType"/>).</summary>
    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>The file's SHA-256, lower-case hex. Unique within the knowledge base.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    public KnowledgeDocumentStatus ProcessingStatus { get; private set; }

    /// <summary>Why the version is <see cref="KnowledgeDocumentStatus.Failed"/> or only
    /// partially readable, for the owner; otherwise <see langword="null"/>.</summary>
    public string? Issue { get; private set; }

    /// <summary>The batch the owner uploaded it in, if the upload named one (the
    /// frontend's multi-file upload, Slice 12).</summary>
    public Guid? UploadBatchId { get; private set; }

    public Guid UploadedByAccountId { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    /// <summary>The last change to this row: the upload, then every status change.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A new, <see cref="KnowledgeDocumentStatus.Queued"/> version of
    /// <paramref name="document"/>. The caller enqueues its processing in the same save.</summary>
    public static KnowledgeDocumentVersion Create(
        KnowledgeDocument document,
        int versionNumber,
        string fileName,
        string contentType,
        long sizeBytes,
        string sha256,
        Guid uploadedByAccountId,
        Guid? uploadBatchId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentOutOfRangeException.ThrowIfLessThan(versionNumber, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);

        if (fileName.Length is 0 or > FileNameMaxLength || fileName.Trim().Length != fileName.Length)
        {
            throw new ArgumentException(
                $"A file name must be 1-{FileNameMaxLength} characters without surrounding white space.",
                nameof(fileName));
        }

        if (!KnowledgeFileFormats.TryFromContentType(contentType, out _))
        {
            throw new ArgumentException("Only a knowledge file format's canonical content type can be stored.", nameof(contentType));
        }

        if (sha256.Length != Sha256Length || !sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("A SHA-256 must be 64 lower-case hexadecimal characters.", nameof(sha256));
        }

        if (uploadedByAccountId == Guid.Empty)
        {
            throw new ArgumentException("An uploader id must not be empty.", nameof(uploadedByAccountId));
        }

        if (uploadBatchId == Guid.Empty)
        {
            throw new ArgumentException("A batch id must not be empty; pass null for none.", nameof(uploadBatchId));
        }

        return new KnowledgeDocumentVersion
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = document.OrganizationId,
            KnowledgeBaseId = document.KnowledgeBaseId,
            DocumentId = document.Id,
            VersionNumber = versionNumber,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            ProcessingStatus = KnowledgeDocumentStatus.Queued,
            Issue = null,
            UploadBatchId = uploadBatchId,
            UploadedByAccountId = uploadedByAccountId,
            UploadedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary><see cref="KnowledgeDocumentStatus.Queued"/> → <see cref="KnowledgeDocumentStatus.Processing"/>.</summary>
    public void StartProcessing(DateTimeOffset now)
    {
        RequireStatus(KnowledgeDocumentStatus.Queued);
        ProcessingStatus = KnowledgeDocumentStatus.Processing;
        UpdatedAt = now;
    }

    /// <summary>A queued or processing version failed, for <paramref name="issue"/>
    /// (shown to the owner as is).</summary>
    public void MarkFailed(string issue, DateTimeOffset now)
    {
        RequireIssue(issue);
        if (ProcessingStatus is not (KnowledgeDocumentStatus.Queued or KnowledgeDocumentStatus.Processing))
        {
            throw new InvalidOperationException($"Only a queued or processing version can fail; this one is {ProcessingStatus}.");
        }

        ProcessingStatus = KnowledgeDocumentStatus.Failed;
        Issue = issue;
        UpdatedAt = now;
    }

    /// <summary>
    /// A <see cref="KnowledgeDocumentStatus.Processing"/> version's text was read: it is
    /// <see cref="KnowledgeDocumentStatus.Ready"/> (no issue),
    /// <see cref="KnowledgeDocumentStatus.PartiallyReadable"/> or
    /// <see cref="KnowledgeDocumentStatus.Failed"/> (both with an issue for the owner), as the
    /// readability rules decided (M2 plan §4).
    /// </summary>
    public void CompleteProcessing(KnowledgeDocumentStatus outcome, string? issue, DateTimeOffset now)
    {
        RequireStatus(KnowledgeDocumentStatus.Processing);
        switch (outcome)
        {
            case KnowledgeDocumentStatus.Ready when issue is null:
                break;
            case KnowledgeDocumentStatus.PartiallyReadable or KnowledgeDocumentStatus.Failed:
                RequireIssue(issue);
                break;
            default:
                throw new ArgumentException(
                    "Processing ends ready without an issue, or partially readable or failed with one.",
                    nameof(outcome));
        }

        ProcessingStatus = outcome;
        Issue = issue;
        UpdatedAt = now;
    }

    /// <summary>
    /// A <see cref="KnowledgeDocumentStatus.Failed"/> version goes back to the queue, its
    /// issue cleared; the caller enqueues its processing again in the same save. Nothing
    /// else can be retried.
    /// </summary>
    public void Requeue(DateTimeOffset now)
    {
        RequireStatus(KnowledgeDocumentStatus.Failed);
        ProcessingStatus = KnowledgeDocumentStatus.Queued;
        Issue = null;
        UpdatedAt = now;
    }

    private static void RequireIssue([System.Diagnostics.CodeAnalysis.NotNull] string? issue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issue);
        if (issue.Length > IssueMaxLength)
        {
            throw new ArgumentException($"An issue must be at most {IssueMaxLength} characters.", nameof(issue));
        }
    }

    private void RequireStatus(KnowledgeDocumentStatus expected)
    {
        if (ProcessingStatus != expected)
        {
            throw new InvalidOperationException($"This version is {ProcessingStatus}, not {expected}.");
        }
    }
}
