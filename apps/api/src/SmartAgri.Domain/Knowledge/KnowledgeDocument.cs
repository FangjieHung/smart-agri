using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// One document in a knowledge base (table <c>KnowledgeDocuments</c>, M2 plan §4): the
/// logical document, whose content lives in its <see cref="KnowledgeDocumentVersion"/>s.
/// Uploading a file creates the document together with its version 1; later slices add
/// further versions (Slice 8) and FAQ entries (<see cref="KnowledgeItemKind.Faq"/>, Slice 10).
/// </summary>
/// <remarks>
/// The database enforces that the knowledge base belongs to <see cref="OrganizationId"/>
/// (composite foreign key), deletes the document with its knowledge base, and keeps
/// <see cref="Name"/> unique within the knowledge base. Emergency disabling
/// (<c>DisabledAt</c> and friends, plan §4) is added by Slice 8 (#42) together with the
/// endpoints that set it.
/// </remarks>
public sealed class KnowledgeDocument : IOrganizationScoped
{
    /// <summary>Also the longest accepted upload file name.</summary>
    public const int NameMaxLength = 255;

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
}
