using System.Linq.Expressions;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Assistants;

/// <summary>
/// Which knowledge bases an assistant's owner may connect to it (M3 plan §3, step 1): their
/// own, any <see cref="KnowledgeSharingScope.Public"/> one, or a
/// <see cref="KnowledgeSharingScope.SpecificAccounts"/> one that shares with them. This is
/// <see cref="SmartAgri.Application.Knowledge.KnowledgeBaseAccess"/>'s counterpart for
/// assistants — the owner does not have to be the caller (an assistant's settings can only be
/// changed by its own owner, but the rule itself is about the owner, not "whoever is asking").
/// </summary>
/// <remarks>
/// Shared by every place that needs the answer: connecting/disconnecting a source
/// (<c>AssistantEndpoints</c>), the draft's field validation (#72), and — re-checked at answer
/// time, per the grounded-answers ADR — <c>GroundedAnswerService</c> (a share the owner no
/// longer has access to is excluded even if the assistant's settings still name it).
/// Written as an <see cref="Expression{TDelegate}"/> over <see cref="KnowledgeBase"/> plus an
/// <see cref="IQueryable{T}"/> of shares (the same pattern as
/// <c>KnowledgeItemStates.Of</c>), so EF Core translates the share check into a correlated
/// subquery and unit tests can run it against in-memory collections.
/// </remarks>
public static class AssistantKnowledgeAccess
{
    public static Expression<Func<KnowledgeBase, bool>> ConnectableBy(
        Guid ownerAccountId, IQueryable<KnowledgeBaseShare> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        return knowledgeBase =>
            knowledgeBase.OwnerAccountId == ownerAccountId
            || knowledgeBase.SharingScope == KnowledgeSharingScope.Public
            || (knowledgeBase.SharingScope == KnowledgeSharingScope.SpecificAccounts
                && shares.Any(share => share.KnowledgeBaseId == knowledgeBase.Id && share.AccountId == ownerAccountId));
    }
}
