using System.Linq.Expressions;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>
/// Who may <b>use</b> (chat with) an assistant — a different question from
/// <see cref="AssistantAccess"/>, which decides who may open or change its configuration.
/// Backs <c>GET /api/v1/assistants?usable=true</c> (mapping <c>S</c>) and, from #73, every
/// conversation endpoint.
/// </summary>
/// <remarks>
/// <para>
/// M3 Slice 1 (this ticket) only knows about ownership: the owner may always use their own
/// assistant, paused or not (M3 plan §5 Slice 3: "助理暫停後，非擁有者無法使用，擁有者可以"). Sharing
/// is #73's <c>AssistantShare</c> table; when it lands, <see cref="UsableBy"/> is expected to
/// grow an <c>OR</c> branch for "shared with this account AND the account holds
/// <c>use-shared-assistants</c>" — the single place that decision is made, so
/// <c>listUsableAssistants</c> and every chat endpoint stay in lockstep.
/// </para>
/// <para>Written as an <see cref="Expression{TDelegate}"/> so EF Core translates it into the
/// query and unit tests can run it in memory, like <see cref="AssistantAccess"/>.</para>
/// </remarks>
public static class AssistantUseAccess
{
    public static Expression<Func<Assistant, bool>> UsableBy(Guid accountId) =>
        assistant => assistant.OwnerAccountId == accountId;
}
