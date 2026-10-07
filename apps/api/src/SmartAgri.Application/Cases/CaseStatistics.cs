using System.Linq.Expressions;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Application.Cases;

/// <summary>
/// 瓶頸統計 (M7 plan §3 F, decision K; issue #251): per case type × current case group, the open and
/// overdue cases now, and the cases completed and cancelled within a date range with the average
/// handling time. The single definition behind <c>GET /api/v1/cases/statistics</c> and the list's
/// <c>closedFrom</c>/<c>closedTo</c>, so a row's number and the list it opens always agree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overdue</b> is <see cref="CaseAttention.Overdue"/>, the very rule of the side navigation and the
/// list's <c>overdue=true</c> (M7-5); nothing here restates it.
/// </para>
/// <para>
/// <b>Average handling time = creation → completion</b>, in hours, over the cases whose
/// <see cref="Case.CompletedAt"/> falls within the range (<see cref="CompletedWithin"/>). Only
/// <c>Complete</c> writes <see cref="Case.CompletedAt"/>, so a cancelled case never counts. A case is
/// counted under its type and group at completion: the type never changes, and a closed case cannot be
/// transferred, so the case's current <see cref="Case.GroupId"/> <i>is</i> the group it was completed in —
/// a case transferred before completion counts for the group that completed it, not the one it started in.
/// </para>
/// <para>
/// The range is <c>[fromUtc, toExclusiveUtc)</c>, the half-open instants of an
/// <c>AnswerAnalyticsRange</c> (UTC days, as the operations summary counts).
/// </para>
/// </remarks>
public static class CaseStatistics
{
    /// <summary>Completed within <c>[fromUtc, toExclusiveUtc)</c>.</summary>
    public static Expression<Func<Case, bool>> CompletedWithin(DateTimeOffset fromUtc, DateTimeOffset toExclusiveUtc) =>
        item => item.Status == CaseStatus.Completed
            && item.CompletedAt != null && item.CompletedAt >= fromUtc && item.CompletedAt < toExclusiveUtc;

    /// <summary>Cancelled within <c>[fromUtc, toExclusiveUtc)</c>.</summary>
    public static Expression<Func<Case, bool>> CancelledWithin(DateTimeOffset fromUtc, DateTimeOffset toExclusiveUtc) =>
        item => item.Status == CaseStatus.Cancelled
            && item.CancelledAt != null && item.CancelledAt >= fromUtc && item.CancelledAt < toExclusiveUtc;

    /// <summary>Closed (completed or cancelled) within the range: the list's <c>closedFrom</c>/<c>closedTo</c>;
    /// with <c>status=completed</c> or <c>status=cancelled</c> it is exactly one of the two counts.</summary>
    public static Expression<Func<Case, bool>> ClosedWithin(DateTimeOffset fromUtc, DateTimeOffset toExclusiveUtc) =>
        item => (item.Status == CaseStatus.Completed
                && item.CompletedAt != null && item.CompletedAt >= fromUtc && item.CompletedAt < toExclusiveUtc)
            || (item.Status == CaseStatus.Cancelled
                && item.CancelledAt != null && item.CancelledAt >= fromUtc && item.CancelledAt < toExclusiveUtc);

    /// <summary>Hours from <paramref name="createdAt"/> to <paramref name="completedAt"/>.</summary>
    public static double HandlingHours(DateTimeOffset createdAt, DateTimeOffset completedAt) => (completedAt - createdAt).TotalHours;

    /// <summary>
    /// Puts the four sets together, one row per (type, group) that appears in any of them; a pair with
    /// nothing open, overdue, completed or cancelled has no row. <see cref="CaseStatisticsRow.AverageHandlingHours"/>
    /// is <see langword="null"/> when the pair completed nothing within the range.
    /// </summary>
    public static IReadOnlyList<CaseStatisticsRow> Rows(
        IEnumerable<CaseStatisticsCount> open,
        IEnumerable<CaseStatisticsCount> overdue,
        IEnumerable<CaseStatisticsCompletion> completed,
        IEnumerable<CaseStatisticsCount> cancelled)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(overdue);
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(cancelled);
        var openBy = Index(open);
        var overdueBy = Index(overdue);
        var cancelledBy = Index(cancelled);
        var completedBy = completed
            .GroupBy(item => (item.TypeId, item.GroupId))
            .ToDictionary(group => group.Key, group => group.Select(item => HandlingHours(item.CreatedAt, item.CompletedAt)).ToList());

        return
        [
            .. openBy.Keys
                .Concat(overdueBy.Keys)
                .Concat(completedBy.Keys)
                .Concat(cancelledBy.Keys)
                .Distinct()
                .Select(key =>
                {
                    var hours = completedBy.GetValueOrDefault(key) ?? [];
                    return new CaseStatisticsRow(
                        key.TypeId,
                        key.GroupId,
                        openBy.GetValueOrDefault(key),
                        overdueBy.GetValueOrDefault(key),
                        hours.Count,
                        cancelledBy.GetValueOrDefault(key),
                        hours.Count == 0 ? null : hours.Average());
                }),
        ];
    }

    private static Dictionary<(Guid TypeId, Guid GroupId), int> Index(IEnumerable<CaseStatisticsCount> counts) =>
        counts.GroupBy(count => (count.TypeId, count.GroupId)).ToDictionary(group => group.Key, group => group.Sum(count => count.Count));
}

/// <summary>How many cases of one (type, group) are in a set.</summary>
public sealed record CaseStatisticsCount(Guid TypeId, Guid GroupId, int Count);

/// <summary>One case completed within the range: what its handling time is computed from.</summary>
public sealed record CaseStatisticsCompletion(Guid TypeId, Guid GroupId, DateTimeOffset CreatedAt, DateTimeOffset CompletedAt);

/// <summary>One row of the statistics, by ids (the endpoint adds the names).</summary>
/// <param name="AverageHandlingHours">Creation → completion, over <paramref name="CompletedCount"/>;
/// <see langword="null"/> when that is 0.</param>
public sealed record CaseStatisticsRow(
    Guid TypeId, Guid GroupId, int OpenCount, int OverdueCount, int CompletedCount, int CancelledCount, double? AverageHandlingHours);
