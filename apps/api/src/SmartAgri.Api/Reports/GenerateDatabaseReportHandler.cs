using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Databases;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Reports;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Reports;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Reports;

/// <summary>
/// Handles <see cref="GenerateDatabaseReportJob.Kind"/> (M4 #150): saves one period's report snapshot for
/// one schedule, and queues the next period's job. Everything below happens in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Statistics</b> come only from <see cref="DatabaseFixedQueryService.PeriodSummaryForAsync"/> — the same
/// fixed <c>period-summary</c> query, run <b>as the assistant's owner</b>, so it applies the same rule as a
/// request would: the owner must still be a designated data manager holding
/// <c>read-consented-submissions</c> (then <c>Readable = false</c> and the period is skipped), only active
/// records count (a withdrawn submission is not in a new report), and the period and the one before it are
/// calendar days of <c>Statistics:TimeZone</c>. The result is saved as the query returned it.
/// </para>
/// <para>
/// <b>Idempotent.</b> A schedule that is gone, or whose <c>NextPeriodFrom</c> is no longer this job's
/// period, is stale — nothing to do. Otherwise a report that already exists for the period (unique index) is
/// kept as it is, and the chain moves on with a compare-and-set on <c>NextPeriodFrom</c>, so the next job
/// is queued exactly once however often this one is delivered. A job that runs late (the worker was down)
/// reports the period it names and queues the next, already due: periods are caught up in order.
/// </para>
/// <para>
/// <b>Auto-disable (#179).</b> The schedule counts skipped periods in a row (a generated one resets the
/// count); the third one in a row disables it in the compare-and-set that advances it, and no next job is
/// queued. A job for a disabled schedule does nothing.
/// </para>
/// <para>
/// A report with enough records for a comparison (<see cref="ReportDataRules"/>) gets a
/// <see cref="SummarizeDatabaseReportJob"/> in the same save; one without never does.
/// </para>
/// </remarks>
internal sealed class GenerateDatabaseReportHandler : IJobHandler
{
    private readonly AppDbContext _dbContext;
    private readonly DatabaseFixedQueryService _queries;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _timeZone;

    public GenerateDatabaseReportHandler(
        AppDbContext dbContext, DatabaseFixedQueryService queries, TimeProvider clock, IOptions<StatisticsOptions> options)
    {
        _dbContext = dbContext;
        _queries = queries;
        _clock = clock;
        _timeZone = options.Value.TryResolve()
            ?? throw new InvalidOperationException("Statistics:TimeZone was validated at startup.");
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<GenerateDatabaseReportJob>();
        var schedule = await _dbContext.ReportSchedules.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == payload.ScheduleId, cancellationToken);
        if (schedule is null || schedule.NextPeriodFrom != payload.PeriodFrom || schedule.IsAutoDisabled)
        {
            return;
        }

        var assistant = await _dbContext.Assistants.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == schedule.AssistantId, cancellationToken);
        if (assistant is null)
        {
            return;
        }

        Application.Databases.DatabaseQueryPeriod period;
        try
        {
            period = ReportPeriods.Starting(schedule.Frequency, payload.PeriodFrom);
        }
        catch (ArgumentException exception)
        {
            throw new PermanentJobFailure($"Report schedule {schedule.Id}: {exception.Message}", exception);
        }

        var now = _clock.GetUtcNow();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var existing = await _dbContext.DatabaseReports.AsNoTracking()
            .Where(report => report.AssistantId == assistant.Id
                && report.DatabaseId == schedule.DatabaseId
                && report.Frequency == schedule.Frequency
                && report.PeriodFrom == period.From)
            .Select(report => new { report.SkipReason })
            .SingleOrDefaultAsync(cancellationToken);
        ReportSkipReason? skipReason;
        if (existing is not null)
        {
            skipReason = existing.SkipReason;
        }
        else
        {
            var report = await BuildAsync(assistant, schedule, period, now, cancellationToken);
            skipReason = report.SkipReason;
            _dbContext.DatabaseReports.Add(report);
            if (report.SummaryStatus == ReportSummaryStatus.Pending)
            {
                _dbContext.BackgroundJobs.Add(BackgroundJob.Create(
                    job.OrganizationId,
                    SummarizeDatabaseReportJob.Kind,
                    new SummarizeDatabaseReportJob(report.Id),
                    now,
                    maxAttempts: SummarizeDatabaseReportJob.MaxAttempts));
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // #179: the skip counter moves with NextPeriodFrom in the same compare-and-set (and the same
        // transaction as the report row), so a duplicate delivery can neither count a period twice nor
        // queue a second next job. The period that disables the schedule queues none.
        var progress = ReportScheduleRules.AfterPeriod(schedule.ConsecutiveSkips, skipReason);
        DateTimeOffset? disabledAt = progress.Disables ? now : null;
        var next = ReportPeriods.After(schedule.Frequency, period);
        var advanced = await _dbContext.ReportSchedules
            .Where(candidate => candidate.Id == schedule.Id
                && candidate.NextPeriodFrom == period.From
                && candidate.ConsecutiveSkips == schedule.ConsecutiveSkips
                && candidate.AutoDisabledAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.NextPeriodFrom, next.From)
                    .SetProperty(candidate => candidate.ConsecutiveSkips, progress.ConsecutiveSkips)
                    .SetProperty(candidate => candidate.AutoDisabledAt, disabledAt)
                    .SetProperty(candidate => candidate.AutoDisabledReason, progress.DisabledReason),
                cancellationToken);
        if (advanced == 1 && !progress.Disables)
        {
            _dbContext.BackgroundJobs.Add(BackgroundJob.Create(
                job.OrganizationId,
                GenerateDatabaseReportJob.Kind,
                new GenerateDatabaseReportJob(schedule.Id, next.From),
                now,
                runAfter: ReportPeriods.DueAt(next, _timeZone)));
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<DatabaseReport> BuildAsync(
        Domain.Assistants.Assistant assistant,
        ReportSchedule schedule,
        Application.Databases.DatabaseQueryPeriod period,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connected = await _dbContext.AssistantDatabases.AnyAsync(
            link => link.AssistantId == assistant.Id && link.DatabaseId == schedule.DatabaseId, cancellationToken);
        if (!connected)
        {
            return Skipped(ReportSkipReason.NotConnected);
        }

        var previous = ReportPeriods.Before(schedule.Frequency, period);
        var outcome = await _queries.PeriodSummaryForAsync(
            assistant.OwnerAccountId, schedule.DatabaseId, period, previous, cancellationToken);
        if (!outcome.IsOk)
        {
            return Skipped(ReportSkipReason.OwnerCannotRead);
        }

        var statistics = outcome.Value;
        return DatabaseReport.Generated(
            assistant.OrganizationId,
            schedule.DatabaseId,
            assistant.Id,
            assistant.Name,
            schedule.Frequency,
            period.From,
            period.To,
            ReportDataRules.StateOf(statistics),
            JsonSerializer.Serialize(statistics, JsonSerializerOptions.Web),
            now);

        DatabaseReport Skipped(ReportSkipReason reason) => DatabaseReport.Skipped(
            assistant.OrganizationId, schedule.DatabaseId, assistant.Id, assistant.Name, schedule.Frequency, period.From, period.To, reason, now);
    }
}
