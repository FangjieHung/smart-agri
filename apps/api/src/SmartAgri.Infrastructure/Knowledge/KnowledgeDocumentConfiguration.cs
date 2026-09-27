using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeDocuments</c>. The foreign key to <c>KnowledgeBases</c> includes
/// <c>OrganizationId</c> and cascades, so a document always belongs to a knowledge base of
/// its own organization and goes with it; its versions and their files follow by cascade.
/// </summary>
/// <remarks>
/// Emergency disabling (Slice 8): the three <c>Disabled*</c> columns are set and cleared
/// together (a check constraint says so), the disabler is an account of the same organization,
/// and <c>DisabledAt</c> is a concurrency token, so of two concurrent disables (or enables)
/// only one is saved.
/// </remarks>
internal sealed class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
    {
        builder.ToTable("KnowledgeDocuments");
        builder.HasKey(document => document.Id);
        builder.Property(document => document.Id).ValueGeneratedNever();
        builder.Property(document => document.Kind)
            .HasConversion<WireNameConverter<KnowledgeItemKind>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(document => document.Name).HasMaxLength(KnowledgeDocument.NameMaxLength).IsRequired();
        builder.Property(document => document.DisabledReason).HasMaxLength(KnowledgeDocument.DisabledReasonMaxLength);
        builder.Property(document => document.DisabledAt).IsConcurrencyToken();
        builder.ToTable(table => table.HasCheckConstraint(
            "CK_KnowledgeDocuments_Disabled",
            "(\"DisabledAt\" IS NULL AND \"DisabledByAccountId\" IS NULL AND \"DisabledReason\" IS NULL) OR " +
            "(\"DisabledAt\" IS NOT NULL AND \"DisabledByAccountId\" IS NOT NULL AND \"DisabledReason\" IS NOT NULL)"));

        // Target of the versions' composite foreign key: a version's KnowledgeBaseId (kept
        // for the per-knowledge-base duplicate-content index) must be its document's.
        builder.HasAlternateKey(document => new { document.Id, document.KnowledgeBaseId, document.OrganizationId });

        builder.HasOne<KnowledgeBase>()
            .WithMany()
            .HasForeignKey(document => new { document.KnowledgeBaseId, document.OrganizationId })
            .HasPrincipalKey(knowledgeBase => new { knowledgeBase.Id, knowledgeBase.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // Whoever disabled it is an account of the same organization (accounts are not
        // deleted in M2, hence Restrict, like a version's uploader).
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(document => new { document.DisabledByAccountId, document.OrganizationId })
            .HasPrincipalKey(account => new { account.Id, account.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        // The duplicate-name rule, enforced by the database too, so two concurrent uploads
        // of the same name cannot both succeed. Also serves listing a knowledge base's items.
        builder.HasIndex(document => new { document.KnowledgeBaseId, document.Name }).IsUnique();
    }
}
