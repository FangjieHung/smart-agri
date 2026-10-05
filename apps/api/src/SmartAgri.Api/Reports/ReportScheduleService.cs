using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Databases;
using SmartAgri.Application.Databases;
using SmartAgri.Application.Reports;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Reports;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Reports;

/// <summary>
/// Puts an assistant's report setting (<see cref="ReportScheduleRules"/>) into effect (M4 #150): removes the
/// schedule, or replaces it with a new one — a new id, so any job still queued for the old setting finds
/// nothing — and queues the first period's job for the moment that period is over. The calendar is the
/// statistics time zone's (<c>Statistics:TimeZone</c>), the one the fixed queries use.
/// </summary>
/// <remarks>
/// Call inside the transaction that saves the settings. It saves in two steps (delete, then insert) so the
/// unique index on the assistant never sees two rows.
/// </remarks>
public sealed class ReportScheduleService
{
    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _timeZone;

    public ReportScheduleService(AppDbContext dbContext, TimeProvider clock, IOptions<StatisticsOptions> options)
    {
        _dbContext = dbContext;
        _clock = clock;
        _timeZone = options.Value.TryResolve()
            ?? throw new InvalidOperationException("Statistics:TimeZone was validated at startup.");
    }

    /// <summary>The assistant's schedule, tracked, or <see langword="null"/>.</summary>
    public Task<ReportSchedule?> FindAsync(Guid assistantId, CancellationToken cancellationToken) =>
        _dbContext.ReportSchedules.SingleOrDefaultAsync(schedule => schedule.AssistantId == assistantId, cancellationToken);

    /// <summary>Makes the assistant's schedule what <paramref name="choice"/> says. Does nothing when it
    /// already is, unless <paramref name="resume"/> (<see cref="ReportScheduleRules.Resumes"/>, #179): then an
    /// auto-disabled schedule is replaced by a fresh one — skip counter zero, first period the one containing
    /// today, so the periods missed while it was disabled are not back-filled.</summary>
    public async Task ApplyAsync(
        Assistant assistant, ReportSchedule? existing, ReportScheduleChoice choice, CancellationToken cancellationToken, bool resume = false)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(choice);
        if (!ReportScheduleRules.Differs(existing?.Frequency, existing?.DatabaseId, choice) && !resume)
        {
            return;
        }

        if (existing is not null)
        {
            _dbContext.ReportSchedules.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (choice.Frequency is not { } frequency || choice.DatabaseId is not { } databaseId)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var first = ReportPeriods.Containing(frequency, DatabaseFixedQueries.DayOf(now, _timeZone));
        var schedule = ReportSchedule.Create(assistant.OrganizationId, assistant.Id, databaseId, frequency, first.From, now);
        _dbContext.ReportSchedules.Add(schedule);
        _dbContext.BackgroundJobs.Add(BackgroundJob.Create(
            assistant.OrganizationId,
            GenerateDatabaseReportJob.Kind,
            new GenerateDatabaseReportJob(schedule.Id, first.From),
            now,
            runAfter: ReportPeriods.DueAt(first, _timeZone)));
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
