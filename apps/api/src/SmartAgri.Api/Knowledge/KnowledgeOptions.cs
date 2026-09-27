using SmartAgri.Application.Knowledge.Processing;

namespace SmartAgri.Api.Knowledge;

/// <summary>Configuration section <c>Knowledge</c> (see apps/api/README.md, "Knowledge documents").</summary>
public sealed class KnowledgeOptions
{
    public const string SectionName = "Knowledge";

    /// <summary>20 MB (MiB), the M2 plan's default single-file limit.</summary>
    public const long DefaultMaxFileBytes = 20L * 1024 * 1024;

    /// <summary>
    /// The highest <see cref="MaxFileBytes"/> accepted: an upload is held in memory while it
    /// is checked, and stored as one <c>bytea</c> value (original-files-in-postgresql ADR;
    /// larger files are a reason to revisit that decision, not to raise this).
    /// </summary>
    public const long MaxFileBytesLimit = 256L * 1024 * 1024;

    /// <summary>
    /// Room for the multipart envelope around the file: boundaries, the part headers (a
    /// 255-character non-ASCII file name is sent twice, once percent-encoded) and the
    /// <c>batchId</c> field.
    /// </summary>
    public const long MultipartOverheadBytes = 64 * 1024;

    /// <summary>The highest <see cref="MaxExtractedUnits"/> accepted.</summary>
    public const int ExtractedUnitsLimit = 100_000;

    /// <summary>The highest <see cref="MaxSheetRows"/> accepted: every row is read into memory
    /// before it is chunked.</summary>
    public const int SheetRowsLimit = 1_000_000;

    /// <summary>The largest file a knowledge base accepts, in bytes. Reverse proxies in
    /// front of the Api must allow request bodies of <see cref="MaxRequestBodyBytes"/>.</summary>
    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    /// <summary>The request body size limit of the upload endpoint (Kestrel, per endpoint)
    /// and its multipart section limit: <see cref="MaxFileBytes"/> plus
    /// <see cref="MultipartOverheadBytes"/>.</summary>
    public long MaxRequestBodyBytes => MaxFileBytes + MultipartOverheadBytes;

    /// <summary>The most units (pages, sections, worksheets) processing reads from one file;
    /// later ones are left out and the version is <c>partially-readable</c> (M2 plan §7, risk 3).</summary>
    public int MaxExtractedUnits { get; set; } = ExtractionLimits.DefaultMaxUnits;

    /// <summary>The most data rows processing reads from one worksheet, after its header row.</summary>
    public int MaxSheetRows { get; set; } = ExtractionLimits.DefaultMaxSheetRows;

    /// <summary><see cref="MaxExtractedUnits"/> and <see cref="MaxSheetRows"/> for the extractors.</summary>
    public ExtractionLimits ExtractionLimits => new(MaxExtractedUnits, MaxSheetRows);

    /// <summary>Why these options are unusable, or <see langword="null"/>.</summary>
    public string? Validate()
    {
        if (MaxFileBytes is < 1 or > MaxFileBytesLimit)
        {
            return $"{SectionName}:{nameof(MaxFileBytes)} must be 1-{MaxFileBytesLimit} bytes.";
        }

        if (MaxExtractedUnits is < 1 or > ExtractedUnitsLimit)
        {
            return $"{SectionName}:{nameof(MaxExtractedUnits)} must be 1-{ExtractedUnitsLimit}.";
        }

        return MaxSheetRows is < 1 or > SheetRowsLimit
            ? $"{SectionName}:{nameof(MaxSheetRows)} must be 1-{SheetRowsLimit}."
            : null;
    }
}
