using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain;
using SmartAgri.Domain.Jobs;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Jobs;

/// <summary>
/// Table <c>BackgroundJobs</c>, the job queue (background-jobs-on-postgresql ADR).
/// Organization filter, <c>Organizations</c> foreign key and the <c>OrganizationId</c>
/// concurrency token come from <see cref="AppDbContext"/>'s organization scope; only
/// <see cref="JobClaimer"/> reads the table across organizations.
/// </summary>
internal sealed class BackgroundJobConfiguration : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> builder)
    {
        builder.ToTable("BackgroundJobs");
        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).ValueGeneratedNever();
        builder.Property(job => job.Kind).HasMaxLength(BackgroundJob.KindMaxLength).IsRequired();
        builder.Property(job => job.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(job => job.Status)
            .HasConversion<WireNameConverter<BackgroundJobStatus>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(job => job.LastError).HasMaxLength(BackgroundJob.LastErrorMaxLength);

        // Every claim increments Attempts, so matching on it when recording an outcome
        // makes the save fail (DbUpdateConcurrencyException) for a runner whose lease
        // expired and whose job another runner has claimed since.
        builder.Property(job => job.Attempts).IsConcurrencyToken();

        // What JobClaimer scans, in its ORDER BY; only jobs that can still be claimed.
        builder.HasIndex(job => job.RunAfter)
            .HasDatabaseName("IX_BackgroundJobs_Claimable")
            .HasFilter(
                $"\"Status\" IN ('{WireNames<BackgroundJobStatus>.ToWire(BackgroundJobStatus.Queued)}', " +
                $"'{WireNames<BackgroundJobStatus>.ToWire(BackgroundJobStatus.Running)}')");
    }
}
