using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Databases;
using SmartAgri.Application.Reports;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Reports;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Reports;

/// <summary>The AI summary of a report, apart from its statistics (M4 #150). <see cref="Label"/> is always
/// <c>AI 摘要</c>; <see cref="Text"/> is only set while <see cref="Status"/> is <c>ready</c> — a failed or
/// discarded summary has a <see cref="Note"/> instead, and the report is complete without it.</summary>
public sealed record DatabaseReportAiSummaryView(
    string Label,
    ReportSummaryStatus Status,
    string? Text,
    string? Note,
    DateTimeOffset? UpdatedAt,
    string Disclaimer);

/// <summary>One line of the reports list (statistics not included).</summary>
/// <param name="DataMessage">For a report with too few records (<c>insufficient-records</c>): the
/// <c>紀錄不足</c> sentence; otherwise <see langword="null"/>.</param>
/// <param name="SkipMessage">For a skipped period: why.</param>
public sealed record DatabaseReportListItemView(
    Guid Id,
    Guid AssistantId,
    string AssistantName,
    ReportFrequency Frequency,
    string PeriodFrom,
    string PeriodTo,
    string PeriodLabel,
    ReportStatus Status,
    ReportSkipReason? SkipReason,
    string? SkipMessage,
    ReportDataState? DataState,
    string? DataMessage,
    DateTimeOffset GeneratedAt,
    ReportSummaryStatus SummaryStatus);

/// <summary>An assistant's active schedule on the database: how often and when the next report is made. A
/// schedule that disabled itself (#179) is not listed: no next report will be made.</summary>
/// <param name="NextReportDate">The day (statistics time zone) the next period is over and its report is made.</param>
public sealed record DatabaseReportScheduleView(
    Guid AssistantId,
    string AssistantName,
    ReportFrequency Frequency,
    string NextPeriodFrom,
    string NextReportDate);

/// <summary><c>GET /api/v1/databases/{id}/reports</c>: the schedules on the database and its reports,
/// newest period first.</summary>
public sealed record DatabaseReportListView(
    Guid DatabaseId,
    IReadOnlyList<DatabaseReportScheduleView> Schedules,
    IReadOnlyList<DatabaseReportListItemView> Reports);

/// <summary>One report: the saved statistics exactly as the fixed query returned them, and — separately —
/// the AI summary. <see cref="Statistics"/> is <see langword="null"/> for a skipped period.</summary>
public sealed record DatabaseReportView(
    DatabaseReportListItemView Report,
    DatabasePeriodSummaryResult? Statistics,
    DatabaseReportAiSummaryView AiSummary);

/// <summary>
/// Periodic reports as HTTP (M4 #150): <c>GET /api/v1/databases/{id}/reports</c>, <c>GET
/// .../reports/{reportId}</c> and <c>POST .../reports/{reportId}/summary</c> (retry a failed or discarded AI
/// summary). Reports are produced by the schedule's jobs; there is no endpoint that makes one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who sees a report</b>: exactly the accounts that may read the database's records right now
/// (<see cref="DatabaseRecordReaders.CanReadAsync"/>: a designated data manager holding
/// <c>read-consented-submissions</c>), checked on <b>every</b> request — a revoked designation or permission
/// hides the reports on the next one. The assistant's owner sees them only when they are such a reader too.
/// Anyone else gets what the timeline gives: <c>403 database</c> if the database is not visible to them (or
/// does not exist — same bytes), <c>403 database-records</c> if it is but its records are not theirs to read —
/// for every report id alike, so a report's existence is never revealed. A reader asking for an id that is
/// not a report of this database gets <c>403 database-report</c>.
/// </para>
/// <para>
/// A report's numbers never change after it is made (withdrawal does not rewrite it); the retry only
/// touches the AI summary.
/// </para>
/// </remarks>
public static class DatabaseReportEndpoints
{
    /// <summary>The most reports one list returns, newest period first.</summary>
    public const int ListLimit = 60;

    public static IEndpointRouteBuilder MapDatabaseReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var reports = endpoints.MapGroup(DatabaseEndpoints.DatabasesPath).RequireAuthorization();

        reports.MapGet("/{id:guid}/reports", ListAsync)
            .Produces<DatabaseReportListView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        reports.MapGet("/{id:guid}/reports/{reportId:guid}", GetAsync)
            .Produces<DatabaseReportView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        reports.MapPost("/{id:guid}/reports/{reportId:guid}/summary", RetrySummaryAsync)
            .Produces<DatabaseReportView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        return endpoints;
    }

    internal static async Task<IResult> ListAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        IOptions<StatisticsOptions> options,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (!await DatabaseRecordReaders.CanReadAsync(dbContext, permissions, viewerId, id, cancellationToken))
        {
            return await DatabaseSubmissionEndpoints.RecordsRefusedAsync(dbContext, permissions, viewerId, id, cancellationToken);
        }

        var timeZone = options.Value.TryResolve() ?? throw new InvalidOperationException("Statistics:TimeZone was validated at startup.");
        var rows = await dbContext.DatabaseReports.AsNoTracking()
            .Where(report => report.DatabaseId == id)
            .OrderByDescending(report => report.PeriodFrom).ThenByDescending(report => report.GeneratedAt).ThenBy(report => report.Id)
            .Take(ListLimit)
            .ToListAsync(cancellationToken);
        var schedules = await (
            from schedule in dbContext.ReportSchedules.AsNoTracking()
            where schedule.DatabaseId == id && schedule.AutoDisabledAt == null
            join assistant in dbContext.Assistants.AsNoTracking() on schedule.AssistantId equals assistant.Id
            orderby assistant.Name, schedule.Id
            select new { schedule.AssistantId, assistant.Name, schedule.Frequency, schedule.NextPeriodFrom })
            .ToListAsync(cancellationToken);

        return Results.Ok(new DatabaseReportListView(
            id,
            [.. schedules.Select(schedule =>
            {
                var period = ReportPeriods.Starting(schedule.Frequency, schedule.NextPeriodFrom);
                return new DatabaseReportScheduleView(
                    schedule.AssistantId,
                    schedule.Name,
                    schedule.Frequency,
                    Day(period.From),
                    Day(period.To.AddDays(1)));
            })],
            [.. rows.Select(ToListItem)]));
    }

    internal static async Task<IResult> GetAsync(
        Guid id,
        Guid reportId,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (!await DatabaseRecordReaders.CanReadAsync(dbContext, permissions, viewerId, id, cancellationToken))
        {
            return await DatabaseSubmissionEndpoints.RecordsRefusedAsync(dbContext, permissions, viewerId, id, cancellationToken);
        }

        var report = await dbContext.DatabaseReports.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == reportId && candidate.DatabaseId == id, cancellationToken);
        return report is null ? ApiErrors.NotFound(ForbiddenReason.DatabaseReport) : Results.Ok(ToView(report));
    }

    /// <summary>
    /// Asks for another AI summary of a report whose summary failed or was discarded: same access as
    /// reading it. Only those two states change (to <c>pending</c>, with one summary job queued in the same
    /// save); for any other state nothing is queued and the report is returned as it is, so pressing it twice
    /// costs one model call.
    /// </summary>
    internal static async Task<IResult> RetrySummaryAsync(
        Guid id,
        Guid reportId,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (!await DatabaseRecordReaders.CanReadAsync(dbContext, permissions, viewerId, id, cancellationToken))
        {
            return await DatabaseSubmissionEndpoints.RecordsRefusedAsync(dbContext, permissions, viewerId, id, cancellationToken);
        }

        var report = await dbContext.DatabaseReports
            .SingleOrDefaultAsync(candidate => candidate.Id == reportId && candidate.DatabaseId == id, cancellationToken);
        if (report is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.DatabaseReport);
        }

        var now = clock.GetUtcNow();
        if (report.RetrySummary(now))
        {
            dbContext.BackgroundJobs.Add(BackgroundJob.Create(
                report.OrganizationId,
                SummarizeDatabaseReportJob.Kind,
                new SummarizeDatabaseReportJob(report.Id),
                now,
                maxAttempts: SummarizeDatabaseReportJob.MaxAttempts));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(ToView(report));
    }

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static DatabaseReportView ToView(DatabaseReport report)
    {
        var statistics = report.StatisticsJson is null
            ? null
            : JsonSerializer.Deserialize<DatabasePeriodSummaryResult>(report.StatisticsJson, JsonSerializerOptions.Web);
        return new DatabaseReportView(
            ToListItem(report, statistics),
            statistics,
            new DatabaseReportAiSummaryView(
                ReportDataRules.SummaryLabel,
                report.SummaryStatus,
                report.SummaryText,
                report.SummaryNote,
                report.SummaryUpdatedAt,
                ReportDataRules.SummaryDisclaimer));
    }

    private static DatabaseReportListItemView ToListItem(DatabaseReport report) => ToListItem(
        report,
        report.DataState == ReportDataState.InsufficientRecords && report.StatisticsJson is not null
            ? JsonSerializer.Deserialize<DatabasePeriodSummaryResult>(report.StatisticsJson, JsonSerializerOptions.Web)
            : null);

    private static DatabaseReportListItemView ToListItem(DatabaseReport report, DatabasePeriodSummaryResult? statistics) => new(
        report.Id,
        report.AssistantId,
        report.AssistantName,
        report.Frequency,
        Day(report.PeriodFrom),
        Day(report.PeriodTo),
        new DatabaseQueryPeriod(null, report.PeriodFrom, report.PeriodTo).Label,
        report.Status,
        report.SkipReason,
        report.SkipReason is { } reason ? ReportDataRules.SkipMessage(reason) : null,
        report.DataState,
        report.DataState == ReportDataState.InsufficientRecords && statistics is not null
            ? ReportDataRules.InsufficientMessage(statistics)
            : null,
        report.GeneratedAt,
        report.SummaryStatus);
}
