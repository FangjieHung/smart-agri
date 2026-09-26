using Microsoft.Extensions.Logging;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace SmartAgri.Infrastructure.Knowledge.Extraction;

/// <summary>
/// PDF text with PdfPig (Apache-2.0; assistant-access ADR): one unit per page, in the order
/// the page draws its text (<see cref="ContentOrderTextExtractor"/>, which starts a new line
/// where the text moves to a new line). No OCR: a scanned page has no text to read, and the
/// readability rules mark it.
/// </summary>
/// <remarks>
/// A PDF that needs a password to open fails as <see cref="DocumentExtractionFailure.Encrypted"/>
/// (one that only restricts printing or copying opens without one and is read). A file PdfPig
/// cannot open, or one without pages, is <see cref="DocumentExtractionFailure.Damaged"/>. A
/// single page that fails to parse is read as empty — so it is reported as unreadable with its
/// page number — rather than failing the whole file.
/// </remarks>
public sealed class PdfTextExtractor : IDocumentTextExtractor
{
    private readonly ILogger<PdfTextExtractor> _logger;

    public PdfTextExtractor(ILogger<PdfTextExtractor> logger)
    {
        _logger = logger;
    }

    public bool CanExtract(KnowledgeFileFormat format) => format == KnowledgeFileFormat.Pdf;

    public ExtractedDocument Extract(
        KnowledgeFileFormat format,
        byte[] content,
        ExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);
        if (!CanExtract(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "Not a PDF.");
        }

        PdfDocument document;
        int pageCount;
        try
        {
            // Lenient: real-world PDFs often bend the specification; a font that cannot be
            // loaded skips its letters instead of failing the page.
            document = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            pageCount = document.NumberOfPages;
        }
        catch (PdfDocumentEncryptedException exception)
        {
            throw new DocumentExtractionException(DocumentExtractionFailure.Encrypted, "The PDF needs a password to open.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new DocumentExtractionException(DocumentExtractionFailure.Damaged, "PdfPig could not open the file.", exception);
        }

        using (document)
        {
            if (pageCount < 1)
            {
                throw new DocumentExtractionException(DocumentExtractionFailure.Damaged, "The PDF has no pages.");
            }

            var units = new List<ExtractedUnit>(Math.Min(pageCount, limits.MaxUnits));
            for (var number = 1; number <= pageCount && number <= limits.MaxUnits; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                units.Add(ExtractedUnit.Page(number, ReadPage(document, number)));
            }

            return new ExtractedDocument(units, UnitsTruncated: pageCount > limits.MaxUnits);
        }
    }

    private string ReadPage(PdfDocument document, int number)
    {
        try
        {
            return ContentOrderTextExtractor.GetText(document.GetPage(number));
        }
        catch (PdfDocumentEncryptedException exception)
        {
            throw new DocumentExtractionException(DocumentExtractionFailure.Encrypted, "The PDF needs a password to open.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never the page's content: only which page and what went wrong.
            _logger.LogWarning(exception, "PDF page {PageNumber} could not be read; it counts as a page without text.", number);
            return string.Empty;
        }
    }
}
