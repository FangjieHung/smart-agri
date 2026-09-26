using System.Linq.Expressions;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>
/// Who may see and manage a knowledge base. Organization isolation is not decided here: the
/// persistence layer's query filter already limits every query to the caller's
/// organization. These rules decide among the organization's own knowledge bases.
/// </summary>
/// <remarks>
/// Ported from the frontend mock (<c>apps/admin/src/app/core/repositories/mock-demo-repository.ts</c>):
/// <list type="bullet">
/// <item><see cref="ListedFor"/> — <c>listKnowledgeBaseSummaries</c>, which keeps only
/// knowledge bases whose <c>ownerAccountId</c> is the viewer. Sharing does <b>not</b> put a
/// knowledge base in anyone else's list: in M2 sharing only decides who may connect it to
/// an assistant (M3), not who may open it.</item>
/// <item><see cref="ManageableBy"/> — <c>ownedKnowledgeBase</c>, used by
/// <c>getKnowledgeBaseDetail</c>, <c>updateKnowledgeSharing</c> and every other method:
/// only the owner (mapping <c>S+OWN</c>). A caller who is not the owner gets the same
/// <c>403 knowledge-base</c> as for an id that does not exist.</item>
/// </list>
/// Both are expressions so EF Core translates them into the query (one rule, used in SQL
/// and in unit tests alike). They are the same today but answer different questions, and
/// are expected to diverge once shared knowledge bases can be listed.
/// </remarks>
public static class KnowledgeBaseAccess
{
    /// <summary>Knowledge bases that appear in <paramref name="viewerAccountId"/>'s list.</summary>
    public static Expression<Func<KnowledgeBase, bool>> ListedFor(Guid viewerAccountId) =>
        knowledgeBase => knowledgeBase.OwnerAccountId == viewerAccountId;

    /// <summary>Knowledge bases <paramref name="viewerAccountId"/> may open, change, share
    /// or delete.</summary>
    public static Expression<Func<KnowledgeBase, bool>> ManageableBy(Guid viewerAccountId) =>
        knowledgeBase => knowledgeBase.OwnerAccountId == viewerAccountId;

    /// <summary>
    /// <see cref="ManageableBy"/> for one loaded knowledge base: the <c>viewerCanManage</c>
    /// flag in API responses, so the frontend never compares owner ids itself (M2 plan §3).
    /// </summary>
    public static bool CanManage(KnowledgeBase knowledgeBase, Guid viewerAccountId)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        return knowledgeBase.OwnerAccountId == viewerAccountId;
    }
}
