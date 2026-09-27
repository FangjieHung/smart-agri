using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeFileContents</c>: original files as <c>bytea</c>, one row per version
/// (original-files-in-postgresql ADR). No entity navigates here, so only a query on this
/// set reads file bytes. The foreign key includes <c>OrganizationId</c> and cascades from
/// the version.
/// </summary>
internal sealed class KnowledgeFileContentConfiguration : IEntityTypeConfiguration<KnowledgeFileContent>
{
    public void Configure(EntityTypeBuilder<KnowledgeFileContent> builder)
    {
        builder.ToTable("KnowledgeFileContents");
        builder.HasKey(content => content.VersionId);
        builder.Property(content => content.VersionId).ValueGeneratedNever();
        builder.Property(content => content.Bytes).HasColumnType("bytea").IsRequired();

        builder.HasOne<KnowledgeDocumentVersion>()
            .WithOne()
            .HasForeignKey<KnowledgeFileContent>(content => new { content.VersionId, content.OrganizationId })
            .HasPrincipalKey<KnowledgeDocumentVersion>(version => new { version.Id, version.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
