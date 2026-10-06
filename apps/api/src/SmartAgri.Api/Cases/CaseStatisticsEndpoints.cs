using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Answers;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Cases;
using SmartAgri.Domain.Cases;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Cases;

/// <summary>One row of the bottleneck statistics: a case type × the cases' current case group. Names and
/// numbers only — never a case's title, description, resolution or any conversation text.</summary>
/// <param name="OpenCount">Open now (待受理、處理中、待補件); the list's <c>status=open&amp;typeId=&amp;groupId=</c>.</param>
/// <param name="OverdueCount">Overdue now (<see cref="CaseAttention.Overdue"/>, the M7-5 rule); the list's
/// <c>overdue=true</c> with the same type and group.</param>
/// <param name="CompletedCount">Completed within the range; the list's <c>status=completed&amp;closedFrom=&amp;closedTo=</c>.</param>
/// <param name="CancelledCount">Cancelled within the range; the list's <c>status=cancelled&amp;closedFrom=&amp;closedTo=</c>.</param>
/// <param name="AverageHandlingHours">Average hours from creation to completion of the
/// <paramref name="CompletedCount"/> cases; <see langword="null"/> when there are none.</param>
public sealed record CaseStatisticsRowView(
    CaseTypeRefView Type,
    CaseGroupRefView Group,
    int OpenCount,
    int OverdueCount,
    int CompletedCount,
    int CancelledCount,
    double? AverageHandlingHours);

/// <summary><c>GET /api/v1/cases/statistics</c>'s response: the resolved range (UTC days) and the rows,
/// by type name then group name.</summary>
public sealed record CaseStatisticsView(DateOnly From, DateOnly To, IReadOnlyList<CaseStatisticsRowView> Rows);

/// <summary>
/// 瓶頸統計 (M7 plan §3 F, §5 Slice M7-6, decision K; issue #251): <c>GET /api/v1/cases/statistics?from=&amp;to=</c>,
/// the manager only (<c>403 organization-settings</c> for anyone else). The range is an
/// <see cref="AnswerAnalyticsRange"/> — the newest 30 days by default, at most 180, UTC days — like the
/// operations summary; an invalid one is <c>422 invalid-date-range</c>. What each number means is
/// <see cref="CaseStatistics"/>; each row opens the case list filtered to it (the manager sees every case,
/// <see cref="CaseVisibility"/>).
/// </summary>
public static class CaseStatisticsEndpoints
{
    public const string Path = CaseEndpoints.Path + "/statistics";

    public static IEndpointRouteBuilder MapCaseStatisticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, GetAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<CaseStatisticsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    internal static async Task<IResult> GetAsync(
        DateOnly? from,
        DateOnly? to,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var (range, error) = AnswerAnalyticsRange.Resolve(from, to, now);
        if (range is null)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity, AssistantAnalyticsEndpoints.InvalidDateRangeReason, error!, field: "to");
        }

        var cases = dbContext.Cases.AsNoTracking();
        var openStatuses = CaseStatuses.Open.ToList();
        var open = await CountAsync(cases.Where(item => openStatuses.Contains(item.Status)), cancellationToken);
        var overdue = await CountAsync(cases.Where(CaseAttention.Overdue(now)), cancellationToken);
        var cancelled = await CountAsync(cases.Where(CaseStatistics.CancelledWithin(range.FromUtc, range.ToExclusiveUtc)), cancellationToken);
        var completed = await cases
            .Where(CaseStatistics.CompletedWithin(range.FromUtc, range.ToExclusiveUtc))
            .Select(item => new CaseStatisticsCompletion(item.TypeId, item.GroupId, item.CreatedAt, item.CompletedAt!.Value))
            .ToListAsync(cancellationToken);

        var rows = CaseStatistics.Rows(open, overdue, completed, cancelled);
        var typeIds = rows.Select(row => row.TypeId).Distinct().ToList();
        var groupIds = rows.Select(row => row.GroupId).Distinct().ToList();
        var types = await dbContext.CaseTypes.AsNoTracking()
            .Where(type => typeIds.Contains(type.Id))
            .ToDictionaryAsync(type => type.Id, type => type.Name, cancellationToken);
        var groups = await dbContext.CaseGroups.AsNoTracking()
            .Where(group => groupIds.Contains(group.Id))
            .ToDictionaryAsync(group => group.Id, group => new CaseGroupRefView(group.Id, group.Name, group.IsArchived), cancellationToken);

        var views = rows
            .Select(row => new CaseStatisticsRowView(
                new CaseTypeRefView(row.TypeId, types[row.TypeId]),
                groups[row.GroupId],
                row.OpenCount,
                row.OverdueCount,
                row.CompletedCount,
                row.CancelledCount,
                row.AverageHandlingHours))
            .OrderBy(view => view.Type.Name, StringComparer.Ordinal)
            .ThenBy(view => view.Group.Name, StringComparer.Ordinal)
            .ThenBy(view => view.Type.Id)
            .ThenBy(view => view.Group.Id)
            .ToList();
        return Results.Ok(new CaseStatisticsView(range.From, range.To, views));
    }

    private static Task<List<CaseStatisticsCount>> CountAsync(IQueryable<Case> cases, CancellationToken cancellationToken) =>
        cases
            .GroupBy(item => new { item.TypeId, item.GroupId })
            .Select(group => new CaseStatisticsCount(group.Key.TypeId, group.Key.GroupId, group.Count()))
            .ToListAsync(cancellationToken);
}
