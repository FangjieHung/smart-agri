using System.Linq.Expressions;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Application.Cases;

/// <summary>
/// Who may see a <see cref="Case"/> (case ADR「誰看得到案件」; M7 plan §3 C, risk 1): the single rule
/// behind the case list, the detail, M7-4's actions and M7-6's drill-down. Deliberately its own rule —
/// not the knowledge base's, the database's or the assistant's. A caller sees a case when they are
/// <list type="bullet">
/// <item>its creator;</item>
/// <item>a member of its <b>current</b> case group (after a transfer the old group's members no longer
/// see it);</item>
/// <item>someone who has ever been its case owner — derived from the <see cref="CaseEventAction.Accepted"/>
/// events, so 「阿明」 keeps seeing the case after transferring it (M7-4);</item>
/// <item>the organization's manager (<paramref name="isManager"/>, the role <c>smb-admin</c>, decision A).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The caller must already be an internal account: external customers never see a case (checked
/// before this, <c>CaseGroupRules.IsEligibleMember</c>). Organization isolation is the persistence
/// layer's query filter, as elsewhere; <paramref name="members"/> and <paramref name="events"/> come
/// from the same (filtered) context.
/// </para>
/// <para>
/// Written as an <see cref="Expression{TDelegate}"/> so EF Core translates it into one query and unit
/// tests run it in memory, like <c>DatabaseRecordAccess</c>. A case the caller cannot see answers
/// exactly like one that does not exist (<c>403 case</c>).
/// </para>
/// </remarks>
public static class CaseVisibility
{
    public static Expression<Func<Case, bool>> VisibleTo(
        Guid callerId, bool isManager, IQueryable<CaseGroupMember> members, IQueryable<CaseEvent> events)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(events);
        return item =>
            isManager
            || item.CreatedByAccountId == callerId
            || members.Any(member => member.GroupId == item.GroupId && member.AccountId == callerId)
            || events.Any(caseEvent => caseEvent.CaseId == item.Id
                && caseEvent.Action == CaseEventAction.Accepted
                && caseEvent.OwnerAccountId == callerId);
    }
}
