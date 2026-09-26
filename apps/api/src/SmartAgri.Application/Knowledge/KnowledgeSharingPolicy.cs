using SmartAgri.Application.Validation;
using SmartAgri.Domain;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>
/// A knowledge base's sharing, normalized: <see cref="SharedWithAccountIds"/> is empty
/// unless the scope is <see cref="KnowledgeSharingScope.SpecificAccounts"/>, and
/// <see cref="AllowOriginalDownload"/> is false unless it is
/// <see cref="KnowledgeSharingScope.Public"/>.
/// </summary>
public sealed record KnowledgeSharingSettings(
    KnowledgeSharingScope Scope,
    IReadOnlyList<Guid> SharedWithAccountIds,
    bool AllowOriginalDownload)
{
    /// <summary>Same scope, same download flag and the same set of accounts (in any order).</summary>
    public bool IsEquivalentTo(KnowledgeSharingSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Scope == other.Scope
            && AllowOriginalDownload == other.AllowOriginalDownload
            && SharedWithAccountIds.ToHashSet().SetEquals(other.SharedWithAccountIds);
    }
}

/// <summary>
/// The rules of <c>PUT /api/v1/knowledge-bases/{id}/sharing</c> and of the detail's
/// <c>shareTargets</c>, ported from the frontend mock's <c>updateKnowledgeSharing</c> and
/// <c>getKnowledgeBaseDetail</c> (<c>mock-demo-repository.ts</c>). The server must apply
/// them itself even though the sharing panel pre-normalizes what it sends
/// (<c>tasks-6-10-backend-handoff.md</c> §3.5).
/// </summary>
public static class KnowledgeSharingPolicy
{
    public const string ScopeField = "scope";

    public const string SharedWithAccountIdsField = "sharedWithAccountIds";

    public const string UnknownScopeMessage = "分享範圍不正確，這次變更沒有儲存。";

    /// <summary>The mock's exact message for "specific accounts, but nobody (valid) chosen".</summary>
    public const string NoShareTargetMessage = "請至少選擇一個帳號或團隊。";

    /// <summary>
    /// Who a knowledge base can be shared with: every account of the organization except
    /// its owner, in the order given (the caller passes the organization's accounts; the
    /// query filter has already excluded every other organization's). The detail's
    /// <c>shareTargets</c> lists exactly these, and <see cref="Normalize"/> keeps only these.
    /// </summary>
    public static IReadOnlyList<Guid> ShareTargetIds(Guid ownerAccountId, IEnumerable<Guid> organizationAccountIds)
    {
        ArgumentNullException.ThrowIfNull(organizationAccountIds);
        return [.. organizationAccountIds.Where(accountId => accountId != ownerAccountId).Distinct()];
    }

    /// <summary>
    /// Validates and normalizes a requested sharing change, in the mock's order:
    /// <list type="number">
    /// <item>an unknown <paramref name="scope"/> is a failure on <c>scope</c>;</item>
    /// <item>requested account ids that are not in <paramref name="shareTargetIds"/> — the
    /// owner, another organization's account, an id that does not exist or is not even a
    /// GUID — are silently dropped, as are duplicates;</item>
    /// <item>for <see cref="KnowledgeSharingScope.SpecificAccounts"/>, nothing left is a
    /// failure on <c>sharedWithAccountIds</c> (<see cref="NoShareTargetMessage"/>); any other
    /// scope ignores the list;</item>
    /// <item><c>allowOriginalDownload</c> is kept only for
    /// <see cref="KnowledgeSharingScope.Public"/>, and forced to false otherwise.</item>
    /// </list>
    /// The kept ids come back in <paramref name="shareTargetIds"/>' order, whatever order they
    /// were requested in, so a response and a later read list them identically.
    /// </summary>
    public static ValidationResult<KnowledgeSharingSettings> Normalize(
        string? scope,
        IEnumerable<string?>? sharedWithAccountIds,
        bool allowOriginalDownload,
        IReadOnlyList<Guid> shareTargetIds)
    {
        ArgumentNullException.ThrowIfNull(shareTargetIds);

        if (scope is null || !WireNames<KnowledgeSharingScope>.All.Contains(scope))
        {
            return ValidationResult<KnowledgeSharingSettings>.Invalid(ScopeField, UnknownScopeMessage);
        }

        var parsedScope = WireNames<KnowledgeSharingScope>.Parse(scope);

        IReadOnlyList<Guid> kept = [];
        if (parsedScope == KnowledgeSharingScope.SpecificAccounts)
        {
            var requested = new HashSet<Guid>();
            foreach (var raw in sharedWithAccountIds ?? [])
            {
                if (Guid.TryParse(raw, out var accountId))
                {
                    requested.Add(accountId);
                }
            }

            kept = [.. shareTargetIds.Where(requested.Contains).Distinct()];
            if (kept.Count == 0)
            {
                return ValidationResult<KnowledgeSharingSettings>.Invalid(SharedWithAccountIdsField, NoShareTargetMessage);
            }
        }

        return ValidationResult<KnowledgeSharingSettings>.Valid(new KnowledgeSharingSettings(
            parsedScope,
            kept,
            parsedScope == KnowledgeSharingScope.Public && allowOriginalDownload));
    }
}
