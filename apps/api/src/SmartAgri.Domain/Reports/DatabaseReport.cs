using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Reports;

/// <summary>
/// One period's report of one assistant on one database (table <c>DatabaseReports</c>, M4 #150): a
/// <b>snapshot</b>. Once saved, its statistics are never recomputed or edited — a record withdrawn
/// later changes the next period's report (and its "previous period" figures) but not this one
/// (withdrawal-and-retention ADR: 已產生的定期報表不追溯修改).
/// </summary>
/// <remarks>
/// <para>
/// <b>Statistics and AI summary are stored apart.</b> <see cref="StatisticsJson"/> is the fixed query's
/// result (<c>period-summary</c>) exactly as it was serialized; the summary columns
/// (<see cref="SummaryStatus"/>, <see cref="SummaryText"/>, …) are the model's text about those numbers and
/// can be replaced or discarded without touching them.
/// </para>
/// <para>
/// A report outlives its schedule and assistant (<see cref="AssistantId"/> has no foreign key;
/// <see cref="AssistantName"/> is a copy): it is a statement about the database's records for a period, and
/// only the database's deletion removes it. One row per (assistant, database, frequency, period) — the
/// unique index that makes a retried job produce one report.
/// </para>
/// </remarks>
public sealed class DatabaseReport : IOrganizationScoped
{
    public const int AssistantNameMaxLength = 100;

    public const int SummaryTextMaxLength = 2000;

    public const int SummaryNoteMaxLength = 200;

    public const int SummaryModelMaxLength = 100;

    /// <summary>For EF Core materialization.</summary>
    private DatabaseReport()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid DatabaseId { get; private set; }

    /// <summary>The assistant the report was produced for; not a foreign key (the report outlives it).</summary>
    public Guid AssistantId { get; private set; }

    public string AssistantName { get; private set; } = string.Empty;

    public ReportFrequency Frequency { get; private set; }

    /// <summary>First and last day of the reported period, calendar days of the statistics time zone.</summary>
    public DateOnly PeriodFrom { get; private set; }

    public DateOnly PeriodTo { get; private set; }

    public ReportStatus Status { get; private set; }

    /// <summary>Set for a <see cref="ReportStatus.Skipped"/> period.</summary>
    public ReportSkipReason? SkipReason { get; private set; }

    /// <summary>Set for a <see cref="ReportStatus.Generated"/> report.</summary>
    public ReportDataState? DataState { get; private set; }

    /// <summary>The <c>period-summary</c> result as JSON (<c>jsonb</c>); <see langword="null"/> when skipped.</summary>
    public string? StatisticsJson { get; private set; }

    /// <summary>When the report was produced (or the period was skipped).</summary>
    public DateTimeOffset GeneratedAt { get; private set; }

    public ReportSummaryStatus SummaryStatus { get; private set; }

    /// <summary>The summary text; only while <see cref="SummaryStatus"/> is <see cref="ReportSummaryStatus.Ready"/>.</summary>
    public string? SummaryText { get; private set; }

    /// <summary>Why a summary is failed or discarded, in words for the viewer.</summary>
    public string? SummaryNote { get; private set; }

    /// <summary>The model that wrote the ready summary.</summary>
    public string? SummaryModel { get; private set; }

    /// <summary>When the summary last changed state.</summary>
    public DateTimeOffset? SummaryUpdatedAt { get; private set; }

    /// <summary>A report whose statistics were computed. A <see cref="ReportDataState.Sufficient"/> one waits
    /// for its AI summary (<see cref="ReportSummaryStatus.Pending"/>); one with too few records never gets one.</summary>
    public static DatabaseReport Generated(
        Guid organizationId,
        Guid databaseId,
        Guid assistantId,
        string assistantName,
        ReportFrequency frequency,
        DateOnly periodFrom,
        DateOnly periodTo,
        ReportDataState dataState,
        string statisticsJson,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statisticsJson);
        var report = New(organizationId, databaseId, assistantId, assistantName, frequency, periodFrom, periodTo, now);
        report.Status = ReportStatus.Generated;
        report.DataState = dataState;
        report.StatisticsJson = statisticsJson;
        report.SummaryStatus = dataState == ReportDataState.Sufficient ? ReportSummaryStatus.Pending : ReportSummaryStatus.NotRequested;
        report.SummaryUpdatedAt = now;
        return report;
    }

    /// <summary>A period that came due but produced no report.</summary>
    public static DatabaseReport Skipped(
        Guid organizationId,
        Guid databaseId,
        Guid assistantId,
        string assistantName,
        ReportFrequency frequency,
        DateOnly periodFrom,
        DateOnly periodTo,
        ReportSkipReason reason,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a declared reason.");
        }

        var report = New(organizationId, databaseId, assistantId, assistantName, frequency, periodFrom, periodTo, now);
        report.Status = ReportStatus.Skipped;
        report.SkipReason = reason;
        report.SummaryStatus = ReportSummaryStatus.NotRequested;
        return report;
    }

    /// <summary>The model's text passed the number check. Replaces whatever a failed or discarded try left.</summary>
    public void SummaryReady(string text, string model, DateTimeOffset now)
    {
        RequireSummaryState(ReportSummaryStatus.Pending);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        SummaryStatus = ReportSummaryStatus.Ready;
        SummaryText = text.Length <= SummaryTextMaxLength ? text : text[..SummaryTextMaxLength];
        SummaryNote = null;
        SummaryModel = model.Length <= SummaryModelMaxLength ? model : model[..SummaryModelMaxLength];
        SummaryUpdatedAt = now;
    }

    /// <summary>The model call failed.</summary>
    public void SummaryFailed(string note, DateTimeOffset now)
    {
        RequireSummaryState(ReportSummaryStatus.Pending);
        SummaryStatus = ReportSummaryStatus.Failed;
        SummaryText = null;
        SummaryModel = null;
        SummaryNote = Note(note);
        SummaryUpdatedAt = now;
    }

    /// <summary>The model's text had a number the statistics do not contain; it is thrown away.</summary>
    public void SummaryDiscarded(string note, DateTimeOffset now)
    {
        RequireSummaryState(ReportSummaryStatus.Pending);
        SummaryStatus = ReportSummaryStatus.Discarded;
        SummaryText = null;
        SummaryModel = null;
        SummaryNote = Note(note);
        SummaryUpdatedAt = now;
    }

    /// <summary>Back to <see cref="ReportSummaryStatus.Pending"/> for another try; only a failed or
    /// discarded summary can be retried. Returns whether it changed.</summary>
    public bool RetrySummary(DateTimeOffset now)
    {
        if (SummaryStatus is not (ReportSummaryStatus.Failed or ReportSummaryStatus.Discarded))
        {
            return false;
        }

        SummaryStatus = ReportSummaryStatus.Pending;
        SummaryNote = null;
        SummaryUpdatedAt = now;
        return true;
    }

    private static DatabaseReport New(
        Guid organizationId,
        Guid databaseId,
        Guid assistantId,
        string assistantName,
        ReportFrequency frequency,
        DateOnly periodFrom,
        DateOnly periodTo,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || databaseId == Guid.Empty || assistantId == Guid.Empty)
        {
            throw new ArgumentException("A report needs an organization, a database and an assistant.");
        }

        ArgumentNullException.ThrowIfNull(assistantName);
        var name = assistantName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A report needs the assistant's name.", nameof(assistantName));
        }

        if (!Enum.IsDefined(frequency))
        {
            throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Not a declared frequency.");
        }

        if (periodTo < periodFrom)
        {
            throw new ArgumentException("A period cannot end before it starts.", nameof(periodTo));
        }

        return new DatabaseReport
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            DatabaseId = databaseId,
            AssistantId = assistantId,
            AssistantName = name.Length <= AssistantNameMaxLength ? name : name[..AssistantNameMaxLength],
            Frequency = frequency,
            PeriodFrom = periodFrom,
            PeriodTo = periodTo,
            GeneratedAt = now,
        };
    }

    private void RequireSummaryState(ReportSummaryStatus expected)
    {
        if (Status != ReportStatus.Generated || SummaryStatus != expected)
        {
            throw new InvalidOperationException($"The summary is {SummaryStatus} (report {Status}); expected {expected}.");
        }
    }

    private static string Note(string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        return note.Length <= SummaryNoteMaxLength ? note : note[..SummaryNoteMaxLength];
    }
}
