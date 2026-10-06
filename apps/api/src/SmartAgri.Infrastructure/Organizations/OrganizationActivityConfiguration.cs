using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Organizations;

/// <summary>
/// Table <c>OrganizationActivities</c> (M6 plan §4). No foreign key from <c>ActorAccountId</c> to
/// the accounts on purpose (see <see cref="OrganizationActivity"/>); the organization's own foreign
/// key, filter and concurrency token come from <c>AppDbContext.ApplyOrganizationScope</c>.
/// </summary>
internal sealed class OrganizationActivityConfiguration : IEntityTypeConfiguration<OrganizationActivity>
{
    public void Configure(EntityTypeBuilder<OrganizationActivity> builder)
    {
        builder.ToTable("OrganizationActivities");
        builder.HasKey(activity => activity.Id);
        builder.Property(activity => activity.Id).ValueGeneratedNever();
        builder.Property(activity => activity.Action)
            .HasConversion<WireNameConverter<OrganizationActivityAction>>()
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(activity => activity.Detail).HasColumnType("jsonb");

        // "The last change of this kind" (each settings section's 上次變更), newest last.
        builder.HasIndex(activity => new { activity.OrganizationId, activity.Action, activity.At });
    }
}
