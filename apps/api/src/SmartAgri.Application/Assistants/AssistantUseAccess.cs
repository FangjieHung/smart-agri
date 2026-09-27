using System.Linq.Expressions;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>
/// Who may <b>use</b> (chat with) an assistant — a different question from
/// <see cref="AssistantAccess"/>, which decides who may open or change its configuration.
/// Backs <c>GET /api/v1/assistants?usable=true</c> (mapping <c>S</c>) and, from #76, every
/// conversation endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The owner may always use their own assistant, paused or not (M3 plan §5 Slice 3: "助理暫停後，
/// 非擁有者無法使用，擁有者可以"). Anyone else needs all three: the account holds
/// <c>use-shared-assistants</c>, an <see cref="AssistantShare"/> row names it, and the assistant
/// is <see cref="AssistantStatus.Ready"/> (not paused) — this is the single place that decision
/// is made, so <c>listUsableAssistants</c> and every chat endpoint stay in lockstep.
/// </para>
/// <para>Written as an <see cref="Expression{TDelegate}"/> so EF Core translates it into the
/// query and unit tests can run it in memory, like <see cref="AssistantAccess"/> and
/// <see cref="AssistantKnowledgeAccess"/>.</para>
/// </remarks>
public static class AssistantUseAccess
{
    public static Expression<Func<Assistant, bool>> UsableBy(
        Guid accountId, bool hasUseSharedAssistants, IQueryable<AssistantShare> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        return assistant =>
            assistant.OwnerAccountId == accountId
            || (hasUseSharedAssistants
                && assistant.Status == AssistantStatus.Ready
                && shares.Any(share => share.AssistantId == assistant.Id && share.AccountId == accountId));
    }
}
