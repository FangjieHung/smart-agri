namespace SmartAgri.Application.Assistants;

/// <summary>
/// The rules of <c>PUT /api/v1/assistants/{id}/publishing/platform</c>, ported from the M2
/// knowledge-base sharing pattern (<c>KnowledgeSharingPolicy</c>) for assistants' single,
/// scope-less "平台內分享" list (M3 plan §5 Slice 3).
/// </summary>
/// <remarks>
/// Unlike knowledge-base sharing, there is no <c>scope</c> here — the request is simply the set
/// of accounts to share with — so an empty result is a valid state ("shared with nobody yet",
/// not a validation failure); this is why, unlike <c>KnowledgeSharingPolicy.Normalize</c>, this
/// class never returns a <c>ValidationResult</c>.
/// </remarks>
public static class AssistantPublishingPolicy
{
    /// <summary>
    /// Who an assistant can be shared with: every account of the organization except its owner,
    /// in the order given (the caller passes the organization's accounts; the query filter has
    /// already excluded every other organization's). Mirrors
    /// <c>KnowledgeSharingPolicy.ShareTargetIds</c>.
    /// </summary>
    public static IReadOnlyList<Guid> ShareTargetIds(Guid ownerAccountId, IEnumerable<Guid> organizationAccountIds)
    {
        ArgumentNullException.ThrowIfNull(organizationAccountIds);
        return [.. organizationAccountIds.Where(accountId => accountId != ownerAccountId).Distinct()];
    }

    /// <summary>
    /// Filters a requested list of account ids down to <paramref name="shareTargetIds"/>:
    /// the owner, another organization's account, an id that does not exist or is not even a
    /// GUID, and duplicates are all silently dropped. The kept ids come back in
    /// <paramref name="shareTargetIds"/>' order, whatever order they were requested in, so a
    /// response and a later read list them identically.
    /// </summary>
    public static IReadOnlyList<Guid> Normalize(IEnumerable<string?>? accountIds, IReadOnlyList<Guid> shareTargetIds)
    {
        ArgumentNullException.ThrowIfNull(shareTargetIds);

        var requested = new HashSet<Guid>();
        foreach (var raw in accountIds ?? [])
        {
            if (Guid.TryParse(raw, out var accountId))
            {
                requested.Add(accountId);
            }
        }

        return [.. shareTargetIds.Where(requested.Contains).Distinct()];
    }
}
