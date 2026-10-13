using System.Text.Json.Serialization;
using SmartAgri.Domain.Accounts;

namespace SmartAgri.Application.Accounts;

/// <summary>Why a member cannot be deactivated right now (issue #356).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemberDeactivationBlock>))]
public enum MemberDeactivationBlock
{
    /// <summary>The caller's own account: deactivating it would sign the caller out for good.</summary>
    [JsonStringEnumMemberName("self")]
    Self,

    /// <summary>The organization's last manager (<c>smb-admin</c>) still in use.</summary>
    [JsonStringEnumMemberName("last-admin")]
    LastAdmin,

    /// <summary>Already deactivated.</summary>
    [JsonStringEnumMemberName("already-deactivated")]
    AlreadyDeactivated,

    /// <summary>The member is a manager (<c>smb-admin</c>) and the caller is not: only a manager deactivates
    /// or reactivates another manager (issue #361; owner decision 2026-10-13).</summary>
    [JsonStringEnumMemberName("admin-required")]
    AdminRequired,
}

/// <summary>Why an account may not take over a deactivated member's work.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SuccessorIneligibleReason>))]
public enum SuccessorIneligibleReason
{
    /// <summary>External customers never take over a colleague's work (they cannot own cases either,
    /// <c>CaseGroupRules.IsEligibleMember</c>).</summary>
    [JsonStringEnumMemberName("external-customer")]
    ExternalCustomer,

    /// <summary>The account lacks a permission the transferred assets need
    /// (<see cref="MemberOffboardingRules.RequiredPermissions"/>).</summary>
    [JsonStringEnumMemberName("missing-permissions")]
    MissingPermissions,

    /// <summary>The account is itself deactivated (never offered, but checked on submit).</summary>
    [JsonStringEnumMemberName("deactivated")]
    Deactivated,
}

/// <summary>What a member holds that deactivation hands over, counted (issue #356).</summary>
/// <param name="Assistants">Assistants they own.</param>
/// <param name="Drafts">Unfinished assistant drafts they own.</param>
/// <param name="KnowledgeBases">Knowledge bases they own.</param>
/// <param name="Databases">Databases they own (archived ones included).</param>
/// <param name="OpenIssues">Unresolved 處理事項 assigned to them.</param>
/// <param name="OpenCases">Open, accepted cases they are the case owner of.</param>
/// <param name="BorrowedSources">Knowledge bases and databases <b>someone else owns</b> that their
/// assistants use because the owner shared them with this member (a knowledge base shared with specific
/// accounts, or a database designating them as a data manager). Not transferred: the successor's
/// assistants stop using them at answer time until the owner shares them with the successor too.</param>
public sealed record MemberHoldings(
    int Assistants,
    int Drafts,
    int KnowledgeBases,
    int Databases,
    int OpenIssues,
    int OpenCases,
    int BorrowedSources);

/// <summary>Whether one account may be chosen as the successor.</summary>
/// <param name="Reason"><see langword="null"/> when eligible.</param>
/// <param name="MissingPermissions">For <see cref="SuccessorIneligibleReason.MissingPermissions"/>: which
/// ones, in the frontend's <c>ACCOUNT_PERMISSIONS</c> order; otherwise empty.</param>
public sealed record SuccessorCheck(SuccessorIneligibleReason? Reason, IReadOnlyList<AccountPermission> MissingPermissions)
{
    public bool IsEligible => Reason is null;
}

/// <summary>
/// The rules of 停用成員並指定接手人 that need no database (issue #356; owner decision 2026-10-13: a
/// manager deactivates a member and designates a successor, and the member's assets go to the successor
/// in one go). The endpoint loads the facts; these decide.
/// </summary>
public static class MemberOffboardingRules
{
    public const string SuccessorField = "successorAccountId";

    public const string SelfMessage = "不能停用自己的帳號。";

    public const string LastAdminMessage = "這是組織裡最後一位使用中的管理者，停用後就沒有人能管理組織設定。請先新增另一位管理者再停用。";

    public const string AlreadyDeactivatedMessage = "這位成員已經停用。";

    public const string AdminRequiredMessage = "只有管理者能停用或重新啟用另一位管理者。";

    public const string NotDeactivatedMessage = "這位成員目前沒有停用。";

    public const string SuccessorRequiredMessage = "請選擇接手人。";

    public const string SuccessorNotEligibleMessage =
        "接手人必須是同一組織裡仍在使用中的內部成員，並具備管理這些資產需要的權限。";

    /// <summary>
    /// What the successor must hold to manage what they receive: <c>manage-assistants</c> when there is
    /// at least one assistant or draft (editing an assistant needs it), <c>manage-data-sources</c> when
    /// there is at least one knowledge base or database (the frontend's 「管理資料來源」). Nothing else:
    /// the other permission-dependent behaviours already degrade on their own when the owner lacks the
    /// permission (publishing settings need <c>manage-publishing</c>, periodic reports and borrowed
    /// databases need <c>read-consented-submissions</c>, and 處理事項 only go to a successor holding
    /// <c>handle-assistant-issues</c>), so they are not a reason to refuse.
    /// </summary>
    public static IReadOnlyList<AccountPermission> RequiredPermissions(MemberHoldings holdings)
    {
        ArgumentNullException.ThrowIfNull(holdings);
        var required = new List<AccountPermission>();
        if (holdings.Assistants + holdings.Drafts > 0)
        {
            required.Add(AccountPermission.ManageAssistants);
        }

        if (holdings.KnowledgeBases + holdings.Databases > 0)
        {
            required.Add(AccountPermission.ManageDataSources);
        }

        return AccountPermissionOrder.Sort(required);
    }

    /// <summary>
    /// Whether the member may be deactivated by the caller now, checked in this order: not the caller's
    /// own account, not already deactivated, a manager only by a manager (issue #361), and not the last
    /// manager in use — <paramref name="otherActiveAdmins"/> counts the <c>smb-admin</c> accounts other
    /// than the member that are not deactivated. <see langword="null"/> when nothing stands in the way.
    /// </summary>
    public static MemberDeactivationBlock? CheckDeactivation(
        Guid callerId, AccountRole callerRole, Guid memberId, AccountRole memberRole, bool memberDeactivated, int otherActiveAdmins)
    {
        if (callerId == memberId)
        {
            return MemberDeactivationBlock.Self;
        }

        if (memberDeactivated)
        {
            return MemberDeactivationBlock.AlreadyDeactivated;
        }

        if (!MayManageRole(callerRole, memberRole))
        {
            return MemberDeactivationBlock.AdminRequired;
        }

        return memberRole == AccountRole.SmbAdmin && otherActiveAdmins == 0 ? MemberDeactivationBlock.LastAdmin : null;
    }

    /// <summary>
    /// Whether a caller of <paramref name="callerRole"/> (who already holds <c>manage-assistants</c>) may
    /// deactivate or reactivate a member of <paramref name="memberRole"/>: a manager only by a manager,
    /// anyone else by anyone who may open the team settings (issue #361).
    /// </summary>
    public static bool MayManageRole(AccountRole callerRole, AccountRole memberRole) =>
        memberRole != AccountRole.SmbAdmin || callerRole == AccountRole.SmbAdmin;

    /// <summary>
    /// Whether a successor must be chosen: only when the member holds something to hand over — an
    /// assistant, draft, knowledge base, database, unresolved 處理事項 or open case (issue #361; owner
    /// decision 2026-10-13). <see cref="MemberHoldings.BorrowedSources"/> never moves, so it does not count.
    /// </summary>
    public static bool SuccessorRequired(MemberHoldings holdings)
    {
        ArgumentNullException.ThrowIfNull(holdings);
        return holdings.Assistants + holdings.Drafts + holdings.KnowledgeBases + holdings.Databases
            + holdings.OpenIssues + holdings.OpenCases > 0;
    }

    /// <summary>The message shown for <paramref name="block"/>.</summary>
    public static string MessageOf(MemberDeactivationBlock block) => block switch
    {
        MemberDeactivationBlock.Self => SelfMessage,
        MemberDeactivationBlock.LastAdmin => LastAdminMessage,
        MemberDeactivationBlock.AlreadyDeactivated => AlreadyDeactivatedMessage,
        MemberDeactivationBlock.AdminRequired => AdminRequiredMessage,
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "Not a declared block."),
    };

    /// <summary>
    /// Whether <paramref name="candidateId"/> may be the successor of <paramref name="memberId"/>: another
    /// account (the caller included) of the same organization — the endpoint only loads those — that is
    /// in use, internal (<c>smb-admin</c> or <c>internal-employee</c>) and holds every one of
    /// <paramref name="required"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The candidate is the member themselves.</exception>
    public static SuccessorCheck CheckSuccessor(
        Guid memberId,
        Guid candidateId,
        AccountRole candidateRole,
        bool candidateDeactivated,
        IReadOnlySet<AccountPermission> candidatePermissions,
        IReadOnlyList<AccountPermission> required)
    {
        ArgumentNullException.ThrowIfNull(candidatePermissions);
        ArgumentNullException.ThrowIfNull(required);
        if (candidateId == memberId)
        {
            throw new ArgumentException("A member is never their own successor.", nameof(candidateId));
        }

        if (candidateDeactivated)
        {
            return new SuccessorCheck(SuccessorIneligibleReason.Deactivated, []);
        }

        if (candidateRole is not (AccountRole.SmbAdmin or AccountRole.InternalEmployee))
        {
            return new SuccessorCheck(SuccessorIneligibleReason.ExternalCustomer, []);
        }

        var missing = AccountPermissionOrder.Sort([.. required.Where(permission => !candidatePermissions.Contains(permission))]);
        return missing.Count == 0
            ? new SuccessorCheck(null, [])
            : new SuccessorCheck(SuccessorIneligibleReason.MissingPermissions, missing);
    }

    /// <summary>The note on the <c>accepted</c> event of a handed-over case (<c>Case.HandOver</c>).</summary>
    public static string CaseHandOverNote(string memberDisplayName, string successorDisplayName) =>
        $"「{memberDisplayName}」的帳號已停用，案件移交給「{successorDisplayName}」。";
}
