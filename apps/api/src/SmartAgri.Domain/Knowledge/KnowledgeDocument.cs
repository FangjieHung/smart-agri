using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// One document in a knowledge base (table <c>KnowledgeDocuments</c>, M2 plan §4): the
/// logical document, whose content lives in its <see cref="KnowledgeDocumentVersion"/>s.
/// Uploading a file creates the document together with its version 1; later slices add
/// further versions (Slice 8) and FAQ entries (<see cref="KnowledgeItemKind.Faq"/>, Slice 10).
/// </summary>
/// <remarks>
/// <para>
/// The database enforces that the knowledge base belongs to <see cref="OrganizationId"/>
/// (composite foreign key), deletes the document with its knowledge base, and keeps
/// <see cref="Name"/> unique within the knowledge base.
/// </para>
/// <para>
/// Emergency disabling (Slice 8, #42): while <see cref="DisabledAt"/> is set, nothing of the
/// document is retrievable, whatever its versions' approval. Disabling touches nothing else,
/// so enabling it again returns the document to exactly where it was — including approvals
/// made meanwhile, which take effect as soon as it is enabled.
/// </para>
/// </remarks>
public sealed class KnowledgeDocument : IOrganizationScoped
{
    /// <summary>Also the longest accepted upload file name.</summary>
    public const int NameMaxLength = 255;

    /// <summary>The longest reason for an emergency disable.</summary>
    public const int DisabledReasonMaxLength = 500;

    private readonly List<KnowledgeDocumentVersion> _versions = [];

    /// <summary>For EF Core materialization.</summary>
    private KnowledgeDocument()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public KnowledgeItemKind Kind { get; private set; }

    /// <summary>
    /// What the document is listed as. For an uploaded document, the file name of its first
    /// version (e.g. <c>退貨政策.pdf</c>), kept when later versions have other file names.
    /// Unique within the knowledge base, compared exactly (after the upload rules'
    /// normalization).
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the owner disabled the document in an emergency; <see langword="null"/>
    /// while it is enabled. Set together with <see cref="DisabledByAccountId"/> and
    /// <see cref="DisabledReason"/>, and cleared together with them.</summary>
    public DateTimeOffset? DisabledAt { get; private set; }

    public Guid? DisabledByAccountId { get; private set; }

    /// <summary>Why it was disabled, as the owner wrote it (trimmed).</summary>
    public string? DisabledReason { get; private set; }

    /// <summary>
    /// The document's versions, for query expressions (<c>document.Versions.Any(...)</c>,
    /// which EF Core translates into a subquery). Not loaded from the database unless a query
    /// includes it; in memory it holds the versions created for this instance.
    /// </summary>
    public IReadOnlyCollection<KnowledgeDocumentVersion> Versions => _versions;

    /// <summary>A new document for an uploaded file; its version 1 is created with it.</summary>
    /// <param name="name">Already normalized by the upload rules; must not be blank or
    /// padded, nor longer than <see cref="NameMaxLength"/>.</param>
    public static KnowledgeDocument CreateUploaded(KnowledgeBase knowledgeBase, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > NameMaxLength || name.Trim().Length != name.Length)
        {
            throw new ArgumentException(
                $"A document name must be 1-{NameMaxLength} characters without surrounding white space.",
                nameof(name));
        }

        return new KnowledgeDocument
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = knowledgeBase.OrganizationId,
            KnowledgeBaseId = knowledgeBase.Id,
            Kind = KnowledgeItemKind.Document,
            Name = name,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Stops the document from being retrieved at once (the business review's "緊急停用"):
    /// <paramref name="reason"/> is required, trimmed, at most
    /// <see cref="DisabledReasonMaxLength"/> characters (the rules layer reports those to the
    /// owner first; here they are caller bugs). Throws when it is already disabled.
    /// </summary>
    public void Disable(Guid actorAccountId, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (actorAccountId == Guid.Empty)
        {
            throw new ArgumentException("An actor id must not be empty.", nameof(actorAccountId));
        }

        if (reason.Length is 0 or > DisabledReasonMaxLength || reason.Trim().Length != reason.Length)
        {
            throw new ArgumentException(
                $"A disable reason must be 1-{DisabledReasonMaxLength} characters without surrounding white space.",
                nameof(reason));
        }

        if (DisabledAt is not null)
        {
            throw new InvalidOperationException("The document is already disabled.");
        }

        DisabledAt = now;
        DisabledByAccountId = actorAccountId;
        DisabledReason = reason;
    }

    /// <summary>Lifts an emergency disable: the document is exactly as it was before (plus
    /// whatever was approved meanwhile). Throws when it is not disabled.</summary>
    public void Enable()
    {
        if (DisabledAt is null)
        {
            throw new InvalidOperationException("The document is not disabled.");
        }

        DisabledAt = null;
        DisabledByAccountId = null;
        DisabledReason = null;
    }

    /// <summary>Called by <see cref="KnowledgeDocumentVersion.Create"/> only.</summary>
    internal void AddVersion(KnowledgeDocumentVersion version) => _versions.Add(version);
}
