using System.Linq.Expressions;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Application.Cases;

/// <summary>
/// 逾期提示 (M7 plan §3 E, decision D; issue #250): which cases need the caller's attention, decided when
/// read — no notification table, no daily job. The single definition behind <c>GET /api/v1/cases/attention</c>
/// (the side navigation's number and the home page's card) and the list's <c>overdue=true</c>.
/// </summary>
/// <remarks>
/// <para>
/// Overdue (<see cref="Overdue"/>): the due time has passed (<c>DueAt &lt; now</c>; at the very moment it
/// is due it is not yet overdue) and the case is still open. 待補件 keeps counting (case ADR): waiting for
/// the creator does not stop the clock. 已完成 and 已取消 never count.
/// </para>
/// <para>
/// Whose attention: the case owner's for a case someone accepted (<see cref="OwnedBy"/>); every member of
/// the case's <b>current</b> group for a case still 待受理 (<see cref="PendingInGroupsOf"/>) — after a
/// transfer, which clears the owner, it counts for the new group. Being the manager adds nothing: the
/// manager sees overdue cases in the bottleneck statistics (M7-6). The two never overlap (a 待受理 case
/// has no owner), so the side navigation's number is their sum, and it equals the list's
/// <c>scope=owned&amp;overdue=true</c> plus <c>scope=my-groups&amp;status=pending&amp;overdue=true</c>.
/// </para>
/// <para>
/// Written as <see cref="Expression{TDelegate}"/>s over the visible cases (<see cref="CaseVisibility"/>),
/// so EF Core translates each count into one query and the unit tests run the same rule in memory.
/// </para>
/// </remarks>
public static class CaseAttention
{
    /// <summary>Overdue at <paramref name="now"/>: past its due time and still open.</summary>
    public static Expression<Func<Case, bool>> Overdue(DateTimeOffset now) =>
        item => item.DueAt < now
            && (item.Status == CaseStatus.Pending || item.Status == CaseStatus.InProgress || item.Status == CaseStatus.AwaitingInfo);

    /// <summary>The same rule for one case in memory.</summary>
    public static bool IsOverdue(CaseStatus status, DateTimeOffset dueAt, DateTimeOffset now) => dueAt < now && status.IsOpen();

    /// <summary>The cases <paramref name="callerId"/> owns (案件負責人) now: the list's <c>scope=owned</c>.</summary>
    public static Expression<Func<Case, bool>> OwnedBy(Guid callerId) => item => item.OwnerAccountId == callerId;

    /// <summary>The cases whose current group has <paramref name="callerId"/> as a member: the list's
    /// <c>scope=my-groups</c>.</summary>
    public static Expression<Func<Case, bool>> InGroupsOf(Guid callerId, IQueryable<CaseGroupMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return item => members.Any(member => member.GroupId == item.GroupId && member.AccountId == callerId);
    }

    /// <summary>待受理 in one of <paramref name="callerId"/>'s groups: 「待我受理」, the cases they may accept.</summary>
    public static Expression<Func<Case, bool>> PendingInGroupsOf(Guid callerId, IQueryable<CaseGroupMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return item => item.Status == CaseStatus.Pending
            && members.Any(member => member.GroupId == item.GroupId && member.AccountId == callerId);
    }

    /// <summary>
    /// The three sets the attention counts are taken from, narrowed from <paramref name="visible"/> (the
    /// cases the caller may see): their own overdue cases, their groups' overdue 待受理 cases, and every
    /// 待受理 case in their groups.
    /// </summary>
    public static CaseAttentionSets For(
        IQueryable<Case> visible, IQueryable<CaseGroupMember> members, Guid callerId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(members);
        var pending = visible.Where(PendingInGroupsOf(callerId, members));
        return new CaseAttentionSets(
            OwnedOverdue: visible.Where(OwnedBy(callerId)).Where(Overdue(now)),
            GroupPendingOverdue: pending.Where(Overdue(now)),
            PendingForMe: pending);
    }
}

/// <summary>What <see cref="CaseAttention.For"/> narrows the visible cases to; each is counted on its own.</summary>
/// <param name="OwnedOverdue">The caller's own (案件負責人) overdue cases.</param>
/// <param name="GroupPendingOverdue">Overdue 待受理 cases in the caller's groups.</param>
/// <param name="PendingForMe">Every 待受理 case in the caller's groups, overdue or not (「待我受理」).</param>
public sealed record CaseAttentionSets(
    IQueryable<Case> OwnedOverdue,
    IQueryable<Case> GroupPendingOverdue,
    IQueryable<Case> PendingForMe);
