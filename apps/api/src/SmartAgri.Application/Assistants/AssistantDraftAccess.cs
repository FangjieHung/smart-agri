using System.Linq.Expressions;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>
/// Who may see, save or delete an assistant wizard draft. Organization isolation is not
/// decided here: the persistence layer's query filter already limits every query to the
/// caller's organization. Unlike an <see cref="Assistant"/>, a draft is never shared or
/// listed for anyone but its own owner (M3 plan §3, §4) — there is only one rule, used for
/// both listing and single-draft access.
/// </summary>
public static class AssistantDraftAccess
{
    /// <summary>Drafts that appear in <paramref name="viewerAccountId"/>'s draft list, and
    /// the only ones they may read, save or delete.</summary>
    public static Expression<Func<AssistantDraft, bool>> OwnedBy(Guid viewerAccountId) =>
        draft => draft.OwnerAccountId == viewerAccountId;
}
