using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// Table <c>KnowledgeActivities</c>. No foreign key to <c>KnowledgeBases</c> on purpose (see
/// <see cref="KnowledgeActivity"/>): the <c>knowledge-base-deleted</c> row outlives its
/// knowledge base, so deletion removes the other rows explicitly instead of by cascade.
/// </summary>
internal sealed class KnowledgeActivityConfiguration : IEntityTypeConfiguration<KnowledgeActivity>
{
    public void Configure(EntityTypeBuilder<KnowledgeActivity> builder)
    {
        builder.ToTable("KnowledgeActivities");
        builder.HasKey(activity => activity.Id);
        builder.Property(activity => activity.Id).ValueGeneratedNever();
        builder.Property(activity => activity.Action)
            .HasConversion<WireNameConverter<KnowledgeActivityAction>>()
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(activity => activity.Detail).HasColumnType("jsonb");

        // A knowledge base's log, newest last; also what deleting a knowledge base filters on.
        builder.HasIndex(activity => new { activity.OrganizationId, activity.KnowledgeBaseId, activity.At });
    }
}
