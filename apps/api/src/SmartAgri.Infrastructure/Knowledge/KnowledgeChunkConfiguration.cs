using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeChunks</c>. Two composite foreign keys, both cascading: to its version
/// (version, document, knowledge base, organization — so the repeated ids always match the
/// version's) and to the unit it was cut from (version, unit ordinal, organization).
/// </summary>
internal sealed class KnowledgeChunkConfiguration : IEntityTypeConfiguration<KnowledgeChunk>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunk> builder)
    {
        builder.ToTable("KnowledgeChunks");
        builder.HasKey(chunk => chunk.Id);
        builder.Property(chunk => chunk.Id).ValueGeneratedNever();
        builder.Property(chunk => chunk.LocationLabel).HasMaxLength(KnowledgeExtractedUnit.LocationLabelMaxLength).IsRequired();
        builder.Property(chunk => chunk.Text).IsRequired();

        builder.HasOne<KnowledgeDocumentVersion>()
            .WithMany()
            .HasForeignKey(chunk => new { chunk.VersionId, chunk.DocumentId, chunk.KnowledgeBaseId, chunk.OrganizationId })
            .HasPrincipalKey(version => new { version.Id, version.DocumentId, version.KnowledgeBaseId, version.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<KnowledgeExtractedUnit>()
            .WithMany()
            .HasForeignKey(chunk => new { chunk.VersionId, chunk.UnitOrdinal, chunk.OrganizationId })
            .HasPrincipalKey(unit => new { unit.VersionId, unit.Ordinal, unit.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // A version's chunks in preview order; also the lookup that replaces them.
        builder.HasIndex(chunk => new { chunk.VersionId, chunk.UnitOrdinal, chunk.Ordinal }).IsUnique();
    }
}
