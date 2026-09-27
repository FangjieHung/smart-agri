namespace SmartAgri.Application.Knowledge.Processing;

/// <summary>
/// What the owner is told when a version is <c>failed</c> or <c>partially-readable</c>
/// (<see cref="Domain.Knowledge.KnowledgeDocumentVersion.Issue"/>, M2 plan §4), in one place so
/// the processing job, its final-failure callback and the tests agree.
/// </summary>
public static class KnowledgeProcessingIssues
{
    /// <summary>No unit had readable text (M2 plan §4, verbatim).</summary>
    public const string NoReadableText = "找不到可讀文字，可能是掃描檔；目前不支援 OCR";

    public const string Encrypted = "這份 PDF 設有密碼，請解除密碼後重新上傳";

    public const string NotUtf8 = "文字檔不是 UTF-8 編碼，請另存為 UTF-8（目前不支援 Big5）";

    public const string Damaged = "無法讀取檔案內容，檔案可能已損毀；請確認檔案能正常開啟後重新上傳";

    /// <summary>Anything else that kept failing until the job ran out of attempts (the
    /// database, a bug): worth a retry, and never an internal error message.</summary>
    public const string Unexpected = "處理時發生錯誤，請重試。";

    public static string For(DocumentExtractionFailure failure) => failure switch
    {
        DocumentExtractionFailure.Encrypted => Encrypted,
        DocumentExtractionFailure.NotUtf8 => NotUtf8,
        DocumentExtractionFailure.Damaged => Damaged,
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };

    /// <summary>
    /// The issue for a version whose job failed for good with <paramref name="jobError"/> (the
    /// job's last error). The processing job fails permanently with one of the messages above
    /// as its error when the file itself cannot be read, and that message is shown as is;
    /// any other error — an exception's own message — becomes <see cref="Unexpected"/>.
    /// </summary>
    public static string ForFinalFailure(string jobError) =>
        jobError is Encrypted or NotUtf8 or Damaged ? jobError : Unexpected;
}
