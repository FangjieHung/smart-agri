using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeDocumentVersions</c>. Belongs to its document through a composite
/// foreign key (document, knowledge base, organization) that cascades; its file is a
/// separate table (<see cref="KnowledgeFileContentConfiguration"/>).
/// </summary>
internal sealed class KnowledgeDocumentVersionConfiguration : IEntityTypeConfiguration<KnowledgeDocumentVersion>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocumentVersion> builder)
    {
        builder.ToTable("KnowledgeDocumentVersions");
        builder.HasKey(version => version.Id);
        builder.Property(version => version.Id).ValueGeneratedNever();
        builder.Property(version => version.FileName).HasMaxLength(KnowledgeDocumentVersion.FileNameMaxLength).IsRequired();
        builder.Property(version => version.ContentType).HasMaxLength(KnowledgeDocumentVersion.ContentTypeMaxLength).IsRequired();
        builder.Property(version => version.Sha256).HasMaxLength(KnowledgeDocumentVersion.Sha256Length).IsFixedLength().IsRequired();
        builder.Property(version => version.Issue).HasMaxLength(KnowledgeDocumentVersion.IssueMaxLength);

        // Status changes are compare-and-set: saved only if the row still has the status it
        // was read with (see KnowledgeDocumentVersion's remarks).
        builder.Property(version => version.ProcessingStatus)
            .HasConversion<WireNameConverter<KnowledgeDocumentStatus>>()
            .HasMaxLength(32)
            .IsRequired()
            .IsConcurrencyToken();

        // Target of the file contents' and extracted units' composite foreign keys.
        builder.HasAlternateKey(version => new { version.Id, version.OrganizationId });

        // Target of the chunks' composite foreign key: a chunk's DocumentId and
        // KnowledgeBaseId (kept for filtering retrieval) must be its version's.
        builder.HasAlternateKey(version => new { version.Id, version.DocumentId, version.KnowledgeBaseId, version.OrganizationId });

        builder.HasOne<KnowledgeDocument>()
            .WithMany()
            .HasForeignKey(version => new { version.DocumentId, version.KnowledgeBaseId, version.OrganizationId })
            .HasPrincipalKey(document => new { document.Id, document.KnowledgeBaseId, document.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // The uploader is an account of the same organization. Restrict, like a knowledge
        // base's owner: accounts are not deleted in M2.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(version => new { version.UploadedByAccountId, version.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(version => new { version.DocumentId, version.VersionNumber }).IsUnique();

        // The duplicate-content rule: one SHA-256 per knowledge base, whichever document or
        // version has it. Enforced here so concurrent uploads cannot both succeed.
        builder.HasIndex(version => new { version.KnowledgeBaseId, version.Sha256 }).IsUnique();
    }
}
