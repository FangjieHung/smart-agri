using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Api.Knowledge;

/// <summary>
/// What a version's stored content becomes with the current rules: an FAQ entry's one unit and
/// chunk (<see cref="KnowledgeFaqProcessing"/>), or what the file's extractor read, judged and
/// chunked (<see cref="KnowledgeVersionProcessing"/>). Processing (<see cref="ProcessKnowledgeVersionHandler"/>)
/// and <c>rechunk</c> (<see cref="RechunkCommand"/>) both read through this, so a version cut
/// again is cut exactly as a new upload would be.
/// </summary>
internal static class KnowledgeVersionReader
{
    /// <exception cref="DocumentExtractionException">The content cannot be read at all (a
    /// password, not UTF-8, damaged — an FAQ entry's content that does not parse included); the
    /// same bytes will not read any better another time.</exception>
    public static ProcessedVersion Read(
        KnowledgeDocumentVersion version,
        byte[] content,
        IEnumerable<IDocumentTextExtractor> extractors,
        ExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(extractors);
        ArgumentNullException.ThrowIfNull(limits);

        if (version.IsFaq)
        {
            KnowledgeFaqEntry entry;
            try
            {
                entry = KnowledgeFaqEntry.FromContent(content);
            }
            catch (FormatException exception)
            {
                // Only the FAQ endpoints write this content, so this is damage, not the owner's input.
                throw new DocumentExtractionException(DocumentExtractionFailure.Damaged, "The FAQ entry's stored content does not parse.", exception);
            }

            return KnowledgeFaqProcessing.Process(entry);
        }

        if (!KnowledgeFileFormats.TryFromContentType(version.ContentType, out var format))
        {
            // Versions only ever store a canonical content type (KnowledgeDocumentVersion.Create).
            throw new InvalidOperationException($"Version {version.Id} has an unknown content type.");
        }

        var extractor = extractors.SingleOrDefault(candidate => candidate.CanExtract(format))
            ?? throw new InvalidOperationException($"No single text extractor is registered for {format}.");
        return KnowledgeVersionProcessing.Process(extractor.Extract(format, content, limits, cancellationToken), limits, ChunkingOptions.Default);
    }
}
