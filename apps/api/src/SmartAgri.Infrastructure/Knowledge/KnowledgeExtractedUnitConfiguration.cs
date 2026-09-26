using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeExtractedUnits</c>: keyed by version and ordinal, and deleted with the
/// version through a foreign key that includes <c>OrganizationId</c>.
/// </summary>
internal sealed class KnowledgeExtractedUnitConfiguration : IEntityTypeConfiguration<KnowledgeExtractedUnit>
{
    public void Configure(EntityTypeBuilder<KnowledgeExtractedUnit> builder)
    {
        builder.ToTable("KnowledgeExtractedUnits");
        builder.HasKey(unit => new { unit.VersionId, unit.Ordinal });
        builder.Property(unit => unit.LocationKind)
            .HasConversion<WireNameConverter<KnowledgeUnitLocationKind>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(unit => unit.LocationLabel).HasMaxLength(KnowledgeExtractedUnit.LocationLabelMaxLength).IsRequired();
        builder.Property(unit => unit.Text).IsRequired();
        builder.Property(unit => unit.IssueCode)
            .HasConversion<WireNameConverter<KnowledgeUnitIssue>>()
            .HasMaxLength(32);

        // Target of the chunks' composite foreign key.
        builder.HasAlternateKey(unit => new { unit.VersionId, unit.Ordinal, unit.OrganizationId });

        builder.HasOne<KnowledgeDocumentVersion>()
            .WithMany()
            .HasForeignKey(unit => new { unit.VersionId, unit.OrganizationId })
            .HasPrincipalKey(version => new { version.Id, version.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
