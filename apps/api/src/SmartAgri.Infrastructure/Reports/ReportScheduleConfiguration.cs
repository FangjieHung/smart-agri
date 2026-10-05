using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Reports;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Reports;

/// <summary>
/// Table <c>AssistantReportSchedules</c> (M4 #150): at most one per assistant (unique index). Both foreign
/// keys include <c>OrganizationId</c>, so a schedule can only name an assistant and a database of its own
/// organization, and both cascade: deleting the assistant or the database ends its schedule (a queued job
/// then finds nothing). It deliberately has <b>no</b> key to <c>AssistantDatabases</c>: disconnecting the
/// database leaves the schedule, and the next due period is recorded as skipped, with the reason.
/// </summary>
internal sealed class ReportScheduleConfiguration : IEntityTypeConfiguration<ReportSchedule>
{
    public void Configure(EntityTypeBuilder<ReportSchedule> builder)
    {
        builder.ToTable("AssistantReportSchedules");
        builder.HasKey(schedule => schedule.Id);
        builder.Property(schedule => schedule.Id).ValueGeneratedNever();
        builder.Property(schedule => schedule.Frequency)
            .HasConversion<WireNameConverter<ReportFrequency>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(schedule => schedule.NextPeriodFrom).HasColumnType("date");

        builder.HasOne<Assistant>()
            .WithMany()
            .HasForeignKey(schedule => new { schedule.AssistantId, schedule.OrganizationId })
            .HasPrincipalKey(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Database>()
            .WithMany()
            .HasForeignKey(schedule => new { schedule.DatabaseId, schedule.OrganizationId })
            .HasPrincipalKey(database => new { database.Id, database.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        // One schedule per assistant.
        builder.HasIndex(schedule => schedule.AssistantId).IsUnique();
    }
}
