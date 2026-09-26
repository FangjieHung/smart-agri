using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// The original bytes of one <see cref="KnowledgeDocumentVersion"/> (table
/// <c>KnowledgeFileContents</c>, <c>bytea</c>; see
/// <c>docs/adr/2026-09-26-original-files-in-postgresql.md</c>). A table of its own, with no
/// navigation from versions or documents, so list and detail queries never load file
/// contents: only downloading and background processing read it, by version id.
/// </summary>
/// <remarks>
/// Written in the same save as its version, and deleted with it (database cascade), so a
/// file can never outlive its version or exist without one.
/// </remarks>
public sealed class KnowledgeFileContent : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private KnowledgeFileContent()
    {
    }

    public KnowledgeFileContent(KnowledgeDocumentVersion version, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.LongLength != version.SizeBytes)
        {
            throw new ArgumentException("The bytes must be exactly the version's size.", nameof(bytes));
        }

        VersionId = version.Id;
        OrganizationId = version.OrganizationId;
        Bytes = bytes;
    }

    public Guid VersionId { get; private set; }

    /// <summary>Always the version's organization.</summary>
    public Guid OrganizationId { get; private set; }

    public byte[] Bytes { get; private set; } = [];
}
