namespace SmartAgri.Api.Errors;

/// <summary>
/// A <c>403</c> reason and its fixed message — the frontend's
/// <c>RepositoryPermissionDeniedReason</c> union (<c>docs/handoff/tasks-6-10-backend-handoff.md</c>
/// §1.6). The message is the same whether the resource is missing or the caller lacks
/// permission, and never names the resource, its owner or anything else that would reveal
/// whether it exists.
/// </summary>
/// <remarks>
/// Add reasons here as their endpoints are built (M1 only needs <see cref="Team"/>); keep
/// the wire name and message identical to the frontend's mock
/// (<c>mock-demo-repository.ts</c>).
/// </remarks>
public sealed class ForbiddenReason
{
    /// <summary>Reading or changing team permissions without <c>manage-assistants</c>, or naming a
    /// member that does not exist (in the caller's organization).</summary>
    public static readonly ForbiddenReason Team = new(
        "team",
        "只有可管理助理與團隊的帳號可以查看或變更團隊成員權限。");

    /// <summary>
    /// Fallback for a permission-protected endpoint that forgot to declare its reason
    /// (see <c>PermissionPolicies.RequirePermission</c>, which always declares one). Not
    /// part of the frontend union on purpose, so it shows up in review and in the UI as a
    /// generic denial instead of silently borrowing another area's message.
    /// </summary>
    public static readonly ForbiddenReason Unspecified = new(
        "forbidden",
        "你沒有執行這項操作的權限。");

    private ForbiddenReason(string wireName, string message)
    {
        WireName = wireName;
        Message = message;
    }

    /// <summary>The <c>reason</c> field on the wire.</summary>
    public string WireName { get; }

    /// <summary>The <c>message</c> field on the wire.</summary>
    public string Message { get; }

    public override string ToString() => WireName;
}
