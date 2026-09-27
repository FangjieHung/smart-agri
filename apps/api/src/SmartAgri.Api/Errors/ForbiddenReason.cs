namespace SmartAgri.Api.Errors;

/// <summary>
/// A <c>403</c> reason and its fixed message — the frontend's
/// <c>RepositoryPermissionDeniedReason</c> union (<c>docs/handoff/tasks-6-10-backend-handoff.md</c>
/// §1.6). The message is the same whether the resource is missing or the caller lacks
/// permission, and never names the resource, its owner or anything else that would reveal
/// whether it exists.
/// </summary>
/// <remarks>
/// Add reasons here as their endpoints are built (M1 needs <see cref="Team"/> and
/// <see cref="PasswordChangeRequired"/>); keep
/// the wire name and message identical to the frontend's mock
/// (<c>mock-demo-repository.ts</c>). One wire name may have more than one message when the
/// mock has (<see cref="KnowledgeBase"/> and <see cref="KnowledgeBaseCreate"/>, like the
/// mock's <c>database</c>): the frontend switches on the reason, never on the message.
/// </remarks>
public sealed class ForbiddenReason
{
    /// <summary>Reading or changing team permissions without <c>manage-assistants</c>, or naming a
    /// member that does not exist (in the caller's organization).</summary>
    public static readonly ForbiddenReason Team = new(
        "team",
        "只有可管理助理與團隊的帳號可以查看或變更團隊成員權限。");

    /// <summary>
    /// A knowledge base that does not exist, belongs to another organization, or is not the
    /// caller's own (only the owner may open or change one). Same bytes in every case.
    /// </summary>
    public static readonly ForbiddenReason KnowledgeBase = new(
        "knowledge-base",
        "你沒有這個知識庫的存取權限，或它已不存在。");

    /// <summary>
    /// Creating a knowledge base without <c>manage-data-sources</c>. Same wire name as
    /// <see cref="KnowledgeBase"/>; the message differs because there is no existing
    /// resource whose existence it could reveal.
    /// </summary>
    public static readonly ForbiddenReason KnowledgeBaseCreate = new(
        "knowledge-base",
        "只有可管理資料來源的帳號可以建立知識庫。");

    /// <summary>
    /// An assistant's configuration (list, settings, source connections, deletion) that does
    /// not exist, belongs to another organization, or is not the caller's own (only the owner
    /// may open or change one; listing and settings also need <c>manage-assistants</c>). Same
    /// bytes in every case (M3 plan, Slice 1 acceptance).
    /// </summary>
    public static readonly ForbiddenReason AssistantConfiguration = new(
        "assistant-configuration",
        "你沒有這個助理的存取權限，或它已不存在。");

    /// <summary>
    /// Using (chatting with) an assistant the caller may not: it does not exist, belongs to
    /// another organization, is paused for a non-owner, or is not shared with the caller
    /// (<c>AssistantUseAccess.UsableBy</c>, extended by #73). Same bytes in every case.
    /// </summary>
    public static readonly ForbiddenReason AssistantUse = new(
        "assistant-use",
        "你沒有使用這個助理的權限，或它已不存在。");

    /// <summary>
    /// An assistant wizard draft that does not exist, belongs to another organization, or
    /// belongs to another account of the same organization (drafts are never shared: only
    /// their owner may read, save or delete one). Same bytes in every case (M3 plan, Slice 2
    /// acceptance; #72).
    /// </summary>
    public static readonly ForbiddenReason AssistantDraft = new(
        "assistant-draft",
        "你沒有這份精靈草稿的存取權限，或它已不存在。");

    /// <summary>
    /// An assistant's publishing settings (<c>GET/PUT .../publishing*</c>) that do not exist,
    /// belong to another organization, or are not the caller's own (only the owner may open or
    /// change them; also needs <c>manage-publishing</c>). Same bytes in every case, mirroring
    /// <see cref="AssistantConfiguration"/>. Named <c>publishing</c> (not
    /// <see cref="AssistantConfiguration"/>) because the mock's <c>getAssistantPublishing</c> /
    /// <c>updatePlatformSharing</c> use a distinct <c>403 publishing</c> reason
    /// (<c>docs/handoff/mock-to-api-mapping.md</c> §2.5), separate from the settings screen's.
    /// </summary>
    public static readonly ForbiddenReason Publishing = new(
        "publishing",
        "你沒有這個助理的發布設定存取權限，或它已不存在。");

    /// <summary>
    /// A conversation thread (<c>threadId</c>) that does not exist, belongs to another
    /// organization, or belongs to another account — including another account the caller's
    /// assistant is shared with, and the assistant's own owner when the thread is someone
    /// else's. Same bytes whether the id is missing or simply not the caller's own (M3 plan,
    /// Slice 6 acceptance; #76): a <c>threadId</c> is always treated as unauthenticated input.
    /// </summary>
    public static readonly ForbiddenReason ChatThread = new(
        "chat-thread",
        "你沒有這段對話的存取權限，或它已不存在。");

    /// <summary>
    /// The account still has the one-time password from <c>setup</c>: until it sets its own
    /// (<c>POST /api/v1/auth/change-password</c>), every protected endpoint except
    /// <c>GET /api/v1/me</c> answers with this (see <c>PasswordChangeGate</c>). New in
    /// ticket #8; the frontend adds it to its union with the change-password page (#12).
    /// </summary>
    public static readonly ForbiddenReason PasswordChangeRequired = new(
        "password-change-required",
        "請先設定新密碼，才能使用其他功能。");

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
