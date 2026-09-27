using System.Linq.Expressions;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>
/// Who may see and manage an assistant's configuration. Organization isolation is not decided
/// here: the persistence layer's query filter already limits every query to the caller's
/// organization. These rules decide among the organization's own assistants.
/// </summary>
/// <remarks>
/// Ported from the frontend mock's <c>listAssistantConfigurations</c> /
/// <c>getAssistantSettings</c> (<c>docs/handoff/mock-to-api-mapping.md</c> §2.6): only the
/// owner may list, read or change an assistant's configuration (mapping <c>S+MA+OWN</c>). A
/// caller who is not the owner gets the same <c>403 assistant-configuration</c> as for an id
/// that does not exist. This is deliberately a different question from
/// <see cref="AssistantUseAccess.UsableBy"/> — who may <b>chat</b> with it.
/// </remarks>
public static class AssistantAccess
{
    /// <summary>Assistants that appear in <paramref name="viewerAccountId"/>'s configuration
    /// list.</summary>
    public static Expression<Func<Assistant, bool>> ListedFor(Guid viewerAccountId) =>
        assistant => assistant.OwnerAccountId == viewerAccountId;

    /// <summary>Assistants <paramref name="viewerAccountId"/> may open, change or delete.</summary>
    public static Expression<Func<Assistant, bool>> ManageableBy(Guid viewerAccountId) =>
        assistant => assistant.OwnerAccountId == viewerAccountId;

    /// <summary><see cref="ManageableBy"/> for one loaded assistant: the
    /// <c>viewerCanManage</c> flag in API responses, so the frontend never compares owner ids
    /// itself (M2 plan §3, carried over to assistants).</summary>
    public static bool CanManage(Assistant assistant, Guid viewerAccountId)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        return assistant.OwnerAccountId == viewerAccountId;
    }
}
