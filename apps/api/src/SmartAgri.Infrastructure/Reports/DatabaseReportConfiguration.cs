using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Reports;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Infrastructure.Reports;

/// <summary>Names of <c>DatabaseReports</c> indexes that callers recognize.</summary>
public static class DatabaseReportIndexes
{
    /// <summary>One report per assistant, database, frequency and period: a retried or doubly
    /// delivered job saves one.</summary>
    public const string OnePerPeriod = "IX_DatabaseReports_OnePerPeriod";
}

/// <summary>
/// Table <c>DatabaseReports</c> (M4 #150): the snapshot of one period's report. The foreign key to the
/// database includes <c>OrganizationId</c> and cascades (a deleted database takes its reports with it);
/// the assistant has no key at all — the report outlives it, with the name copied. Check constraints keep
/// the states honest: a generated report has statistics and a data state and no skip reason, a skipped one
/// the reverse; only a ready summary has text and a model.
/// </summary>
internal sealed class DatabaseReportConfiguration : IEntityTypeConfiguration<DatabaseReport>
{
    public void Configure(EntityTypeBuilder<DatabaseReport> builder)
    {
        builder.ToTable("DatabaseReports", table =>
        {
            table.HasCheckConstraint("CK_DatabaseReports_Period", "\"PeriodTo\" >= \"PeriodFrom\"");
            table.HasCheckConstraint(
                "CK_DatabaseReports_Status",
                "(\"Status\" = 'generated' AND \"StatisticsJson\" IS NOT NULL AND \"DataState\" IS NOT NULL AND \"SkipReason\" IS NULL)" +
                " OR (\"Status\" = 'skipped' AND \"StatisticsJson\" IS NULL AND \"DataState\" IS NULL AND \"SkipReason\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_DatabaseReports_Summary",
                "(\"SummaryStatus\" = 'ready' AND \"SummaryText\" IS NOT NULL AND \"SummaryModel\" IS NOT NULL)" +
                " OR (\"SummaryStatus\" <> 'ready' AND \"SummaryText\" IS NULL AND \"SummaryModel\" IS NULL)");
        });
        builder.HasKey(report => report.Id);
        builder.Property(report => report.Id).ValueGeneratedNever();
        builder.Property(report => report.AssistantName).HasMaxLength(DatabaseReport.AssistantNameMaxLength).IsRequired();
        builder.Property(report => report.Frequency)
            .HasConversion<WireNameConverter<ReportFrequency>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(report => report.PeriodFrom).HasColumnType("date");
        builder.Property(report => report.PeriodTo).HasColumnType("date");
        builder.Property(report => report.Status)
            .HasConversion<WireNameConverter<ReportStatus>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(report => report.SkipReason)
            .HasConversion(
                skip => skip == null ? null : SmartAgri.Domain.WireNames<ReportSkipReason>.ToWire(skip.Value),
                name => name == null ? (ReportSkipReason?)null : SmartAgri.Domain.WireNames<ReportSkipReason>.Parse(name))
            .HasMaxLength(32);
        builder.Property(report => report.DataState)
            .HasConversion(
                state => state == null ? null : SmartAgri.Domain.WireNames<ReportDataState>.ToWire(state.Value),
                name => name == null ? (ReportDataState?)null : SmartAgri.Domain.WireNames<ReportDataState>.Parse(name))
            .HasMaxLength(32);
        builder.Property(report => report.StatisticsJson).HasColumnType("jsonb");
        builder.Property(report => report.SummaryStatus)
            .HasConversion<WireNameConverter<ReportSummaryStatus>>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(report => report.SummaryText).HasMaxLength(DatabaseReport.SummaryTextMaxLength);
        builder.Property(report => report.SummaryNote).HasMaxLength(DatabaseReport.SummaryNoteMaxLength);
        builder.Property(report => report.SummaryModel).HasMaxLength(DatabaseReport.SummaryModelMaxLength);

        builder.HasOne<Database>()
            .WithMany()
            .HasForeignKey(report => new { report.DatabaseId, report.OrganizationId })
            .HasPrincipalKey(database => new { database.Id, database.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(report => new { report.AssistantId, report.DatabaseId, report.Frequency, report.PeriodFrom })
            .IsUnique()
            .HasDatabaseName(DatabaseReportIndexes.OnePerPeriod);

        // A database's reports, newest period first.
        builder.HasIndex(report => new { report.DatabaseId, report.PeriodFrom });
    }
}
