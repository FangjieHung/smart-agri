using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeDocuments</c>. The foreign key to <c>KnowledgeBases</c> includes
/// <c>OrganizationId</c> and cascades, so a document always belongs to a knowledge base of
/// its own organization and goes with it; its versions and their files follow by cascade.
/// </summary>
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

        // Target of the versions' composite foreign key: a version's KnowledgeBaseId (kept
        // for the per-knowledge-base duplicate-content index) must be its document's.
        builder.HasAlternateKey(document => new { document.Id, document.KnowledgeBaseId, document.OrganizationId });

        builder.HasOne<KnowledgeBase>()
            .WithMany()
            .HasForeignKey(document => new { document.KnowledgeBaseId, document.OrganizationId })
            .HasPrincipalKey(knowledgeBase => new { knowledgeBase.Id, knowledgeBase.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // The duplicate-name rule, enforced by the database too, so two concurrent uploads
        // of the same name cannot both succeed. Also serves listing a knowledge base's items.
        builder.HasIndex(document => new { document.KnowledgeBaseId, document.Name }).IsUnique();
    }
}
