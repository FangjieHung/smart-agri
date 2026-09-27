using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeChunks</c>. Two composite foreign keys, both cascading: to its version
/// (version, document, knowledge base, organization — so the repeated ids always match the
/// version's) and to the unit it was cut from (version, unit ordinal, organization).
/// </summary>
/// <remarks>
/// <see cref="KnowledgeChunk.Embedding"/> is a pgvector <c>vector</c> column without a fixed
/// dimension (M2 plan §3), mapped from the domain's <c>float[]</c> to <see cref="Vector"/> by
/// Pgvector.EntityFrameworkCore (<c>UseVector()</c>, see <see cref="AppDbContext"/>). No
/// approximate (HNSW) index: search is exact, filtered by organization and model first.
/// </remarks>
internal sealed class KnowledgeChunkConfiguration : IEntityTypeConfiguration<KnowledgeChunk>
{
    /// <summary>pgvector's type without a dimension: any model's vectors fit.</summary>
    public const string VectorColumnType = "vector";

    /// <summary>Compares vectors by value (and snapshots a copy), so replacing one is detected
    /// and an unchanged one is not written again.</summary>
    private static readonly ValueComparer<float[]?> EmbeddingComparer = new(
        (left, right) => left == right || (left != null && right != null && left.SequenceEqual(right)),
        vector => vector == null ? 0 : vector.Aggregate(vector.Length, (hash, value) => HashCode.Combine(hash, value)),
        vector => vector == null ? null : vector.ToArray());

    public void Configure(EntityTypeBuilder<KnowledgeChunk> builder)
    {
        builder.ToTable("KnowledgeChunks");
        builder.HasKey(chunk => chunk.Id);
        builder.Property(chunk => chunk.Id).ValueGeneratedNever();
        builder.Property(chunk => chunk.LocationLabel).HasMaxLength(KnowledgeExtractedUnit.LocationLabelMaxLength).IsRequired();
        builder.Property(chunk => chunk.Text).IsRequired();
        builder.Property(chunk => chunk.Embedding)
            .HasColumnType(VectorColumnType)
            .HasConversion(new ValueConverter<float[]?, Vector?>(values => values == null ? null : new Vector(values), vector => vector == null ? null : vector.ToArray()), EmbeddingComparer);
        builder.Property(chunk => chunk.EmbeddingModel).HasMaxLength(KnowledgeChunk.EmbeddingModelMaxLength);

        // Navigable for query expressions only (the retrieval eligibility filter follows it to
        // the version and its document); search results do not load it.
        builder.HasOne(chunk => chunk.Version)
            .WithMany()
            .HasForeignKey(chunk => new { chunk.VersionId, chunk.DocumentId, chunk.KnowledgeBaseId, chunk.OrganizationId })
            .HasPrincipalKey(version => new { version.Id, version.DocumentId, version.KnowledgeBaseId, version.OrganizationId })
            .IsRequired()
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
