using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge.Processing;

/// <summary>
/// Reads the text of an uploaded file, unit by unit (M2 plan, Slice 6): a PDF's pages, a
/// DOCX's or Markdown file's sections, a plain-text file whole, an XLSX's worksheets.
/// Implemented in Infrastructure, where the parsing libraries live (PdfPig, Open XML SDK;
/// M2 plan §3); the rules applied to what they read — readability and chunking — are
/// Application's (<see cref="KnowledgeVersionProcessing"/>).
/// </summary>
/// <remarks>
/// Pure CPU work on bytes already in memory: the same bytes always give the same result, so
/// a failure is never worth retrying. A file that cannot be read at all throws
/// <see cref="DocumentExtractionException"/>; a page or section that yields little or garbled
/// text is not an error here but a unit the readability rules judge.
/// </remarks>
public interface IDocumentTextExtractor
{
    /// <summary>Whether this extractor reads <paramref name="format"/>; exactly one registered
    /// extractor reads each <see cref="KnowledgeFileFormat"/>.</summary>
    bool CanExtract(KnowledgeFileFormat format);

    /// <summary>The units of <paramref name="content"/>, in reading order, at most
    /// <see cref="ExtractionLimits.MaxUnits"/> of them.</summary>
    /// <exception cref="DocumentExtractionException">The file is encrypted, not valid UTF-8
    /// (plain text) or cannot be parsed.</exception>
    ExtractedDocument Extract(
        KnowledgeFileFormat format,
        byte[] content,
        ExtractionLimits limits,
        CancellationToken cancellationToken);
}

/// <summary>How much of one file is read (M2 plan §7, risk 3: processing runs in the Api
/// process, so a huge workbook must not be read whole).</summary>
public sealed record ExtractionLimits
{
    /// <param name="maxUnits">Pages, sections or worksheets; later ones are not read.</param>
    /// <param name="maxSheetRows">Rows per worksheet after its header row; later ones are not read.</param>
    public ExtractionLimits(int maxUnits, int maxSheetRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxUnits, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSheetRows, 1);
        MaxUnits = maxUnits;
        MaxSheetRows = maxSheetRows;
    }

    public const int DefaultMaxUnits = 2000;

    public const int DefaultMaxSheetRows = 5000;

    public static ExtractionLimits Default { get; } = new(DefaultMaxUnits, DefaultMaxSheetRows);

    public int MaxUnits { get; }

    public int MaxSheetRows { get; }
}

/// <summary>Why a whole file could not be read; see <see cref="KnowledgeProcessingIssues.For"/>.</summary>
public enum DocumentExtractionFailure
{
    /// <summary>A PDF that needs a password to open.</summary>
    Encrypted,

    /// <summary>A TXT or MD file that is not valid UTF-8 (e.g. Big5).</summary>
    NotUtf8,

    /// <summary>The file is damaged or not really of its format.</summary>
    Damaged,
}

/// <summary>Thrown by an <see cref="IDocumentTextExtractor"/> for a file that cannot be read
/// at all. Retrying cannot help: the bytes do not change.</summary>
public sealed class DocumentExtractionException : Exception
{
    public DocumentExtractionException(DocumentExtractionFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public DocumentExtractionFailure Failure { get; }
}
