using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Chat;

/// <summary>
/// One passage a <see cref="ChatReplyKind.CompanyData"/> <see cref="ChatMessage"/> cites (table
/// <c>ChatMessageCitations</c>, M3 plan §4): a <b>snapshot</b> taken when the reply was saved, so
/// the conversation keeps showing what it cited even after the source document is edited or
/// deleted (M3 plan §7 decision F). <see cref="ChunkId"/>/<see cref="KnowledgeBaseId"/>/
/// <see cref="DocumentId"/>/<see cref="VersionId"/> are best-effort back-references only (their
/// foreign keys <c>SET NULL</c> when the source is deleted, see
/// <c>SmartAgri.Infrastructure.Chat.ChatMessageCitationConfiguration</c>); every field a citation
/// display needs is copied onto this row and never re-read from the source.
/// </summary>
public sealed class ChatMessageCitation : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private ChatMessageCitation()
    {
    }

    public ChatMessageCitation(
        ChatMessage message,
        int ordinal,
        Guid? chunkId,
        Guid? knowledgeBaseId,
        string knowledgeBaseName,
        Guid? documentId,
        string documentName,
        Guid? versionId,
        int versionNumber,
        DateTimeOffset? versionEffectiveFrom,
        string locationLabel,
        string excerpt,
        string text)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1, nameof(ordinal));
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        ArgumentNullException.ThrowIfNull(locationLabel);
        ArgumentNullException.ThrowIfNull(excerpt);
        ArgumentNullException.ThrowIfNull(text);

        MessageId = message.Id;
        OrganizationId = message.OrganizationId;
        Ordinal = ordinal;
        ChunkId = chunkId;
        KnowledgeBaseId = knowledgeBaseId;
        KnowledgeBaseName = knowledgeBaseName;
        DocumentId = documentId;
        DocumentName = documentName;
        VersionId = versionId;
        VersionNumber = versionNumber;
        VersionEffectiveFrom = versionEffectiveFrom;
        LocationLabel = locationLabel;
        Excerpt = excerpt;
        Text = text;
    }

    public Guid MessageId { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>1-based, in the order the reply first cites it (matches the reply text's
    /// <c>[n]</c> markers).</summary>
    public int Ordinal { get; private set; }

    /// <summary><see langword="null"/> once the source chunk is deleted (e.g. the document was
    /// reprocessed); the snapshot fields below are unaffected.</summary>
    public Guid? ChunkId { get; private set; }

    public Guid? KnowledgeBaseId { get; private set; }

    public string KnowledgeBaseName { get; private set; } = string.Empty;

    public Guid? DocumentId { get; private set; }

    public string DocumentName { get; private set; } = string.Empty;

    public Guid? VersionId { get; private set; }

    public int VersionNumber { get; private set; }

    /// <summary>When the cited version took effect, for <c>ChatCitationView.updatedLabel</c>
    /// (<c>YYYY-MM-DD</c>); <see langword="null"/> only if retrieval somehow cited a version with
    /// no effective date (should not happen — retrieval only returns effective/pending chunks).</summary>
    public DateTimeOffset? VersionEffectiveFrom { get; private set; }

    public string LocationLabel { get; private set; } = string.Empty;

    /// <summary>The first 200 characters of <see cref="Text"/> (<c>ChatCitationView.excerpt</c>).</summary>
    public string Excerpt { get; private set; } = string.Empty;

    /// <summary>The passage's whole text, for the citation drawer
    /// (<c>GET .../chat/citations/{messageId}/{ordinal}</c>).</summary>
    public string Text { get; private set; } = string.Empty;
}
