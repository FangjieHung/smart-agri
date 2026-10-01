using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Table <c>AssistantTestRuns</c> (M3.5 plan §4, issue #124). Cascades with its assistant, like
/// <c>AssistantTestCaseConfiguration</c>. A partial unique index allows at most one active
/// (<c>queued</c>／<c>running</c>) run per assistant, so concurrent 「全部重跑」 requests cannot both
/// queue one. <see cref="AssistantTestRun.Status"/> and <see cref="AssistantTestRun.RerunRequested"/>
/// are concurrency tokens (see <see cref="AssistantTestRun"/>).
/// </summary>
internal sealed class AssistantTestRunConfiguration : IEntityTypeConfiguration<AssistantTestRun>
{
    public void Configure(EntityTypeBuilder<AssistantTestRun> builder)
    {
        builder.ToTable("AssistantTestRuns");
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedNever();
        builder.Property(run => run.Trigger)
            .HasConversion<WireNameConverter<AssistantTestRunTrigger>>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(run => run.Status)
            .HasConversion<WireNameConverter<AssistantTestRunStatus>>()
            .HasMaxLength(16)
            .IsRequired()
            .IsConcurrencyToken();
        builder.Property(run => run.RerunRequested).IsConcurrencyToken();
        builder.Property(run => run.RerunTrigger)
            .HasConversion(
                new ValueConverter<AssistantTestRunTrigger?, string?>(
                    trigger => trigger.HasValue ? WireNames<AssistantTestRunTrigger>.ToWire(trigger.Value) : null,
                    name => name == null ? (AssistantTestRunTrigger?)null : WireNames<AssistantTestRunTrigger>.Parse(name)))
            .HasMaxLength(32);
        builder.Property(run => run.PromptVersion).HasMaxLength(AssistantTestRun.PromptVersionMaxLength);
        builder.Property(run => run.Model).HasMaxLength(AssistantTestRun.ModelMaxLength);
        builder.Ignore(run => run.IsActive);

        // History, newest first.
        builder.HasIndex(run => new { run.AssistantId, run.QueuedAt });

        // At most one active run per assistant (M3.5 plan §3: 「同一個助理若已有排隊中或執行中的重跑，
        // 就不再排入新的」).
        builder.HasIndex(run => run.AssistantId)
            .IsUnique()
            .HasDatabaseName("IX_AssistantTestRuns_AssistantId_Active")
            .HasFilter(
                $"\"Status\" IN ('{WireNames<AssistantTestRunStatus>.ToWire(AssistantTestRunStatus.Queued)}', " +
                $"'{WireNames<AssistantTestRunStatus>.ToWire(AssistantTestRunStatus.Running)}')");

        builder.HasOne<Assistant>()
            .WithMany()
            .HasForeignKey(run => new { run.AssistantId, run.OrganizationId })
            .HasPrincipalKey(assistant => new { assistant.Id, assistant.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
