using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// Carries out one kind of action automatically for a shop (store-coordinator design §3 Q8, Q18, Q21, §5 Slices 4,
/// 5, 7, 8, #375): LINE 群發 (Slice 5), Google 特殊營業時間 (Slice 7), FB 貼文 (Slice 8). Ordering systems have none
/// (Q15). Slice 4 registers none, so every action is a reminder; each later slice registers its executor
/// (<c>services.AddScoped&lt;ICoordinatorActionExecutor, …&gt;()</c>; the last one registered for a kind wins).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsAvailableAsync"/> decides the effective mode the preview card shows and a confirmed command records
/// (<see cref="ExecutorActionModes"/>): an action the settings want automatic runs automatically only while its
/// executor is available for the shop (approved API, a token, an authorization). When it runs, the executor may
/// still answer <see cref="CoordinatorActionOutcome.Unavailable"/> (the token was revoked meanwhile): the action then
/// becomes a reminder with its manual steps.
/// </para>
/// <para>
/// Called from a background job, at least once: <see cref="CoordinatorActionRequest.ActionId"/> is the key to make a
/// repeated call harmless (e.g. LINE's <c>X-Line-Retry-Key</c>). An exception counts as
/// <see cref="CoordinatorFailureCodes.Error"/>; nothing is retried automatically — the member presses 〔重試〕.
/// </para>
/// </remarks>
public interface ICoordinatorActionExecutor
{
    /// <summary>The kind it carries out; never <see cref="ShopActionKind.Ordering"/>.</summary>
    ShopActionKind Kind { get; }

    /// <summary>Whether it can run for <paramref name="organizationId"/> now (cheap: settings and stored tokens,
    /// no external call).</summary>
    ValueTask<bool> IsAvailableAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Applies the action (<see cref="CoordinatorActionStep.Apply"/>).</summary>
    Task<CoordinatorActionResult> ExecuteAsync(CoordinatorActionRequest request, CancellationToken cancellationToken);

    /// <summary>Takes an applied action back (<see cref="CoordinatorActionStep.Undo"/>, Q21), with
    /// <see cref="CoordinatorActionRequest.UndoneExternalRef"/>.</summary>
    Task<CoordinatorActionResult> UndoAsync(CoordinatorActionRequest request, CancellationToken cancellationToken);
}

/// <summary>One automatic action to carry out.</summary>
/// <param name="ActionId">The <see cref="CoordinatorCommandAction"/>; the idempotency key of the call.</param>
/// <param name="Scenario">The scenario applied, or for an undo the scenario of the command undone.</param>
/// <param name="Date">The shop's local date of the command.</param>
/// <param name="Time">The new closing (提早打烊) or opening (延後開店) time.</param>
/// <param name="Announcement">The announcement to post (LINE 群發 and FB 貼文; a reopen's correction).</param>
/// <param name="ImageUrl">The announcement's image, when the settings have one.</param>
/// <param name="UndoneExternalRef">For an undo: what the applied action returned (e.g. the FB post id).</param>
public sealed record CoordinatorActionRequest(
    Guid OrganizationId,
    Guid ActionId,
    ShopActionKind Kind,
    CoordinatorActionStep Step,
    ShopScenario Scenario,
    DateOnly Date,
    TimeOnly? Time,
    string? Announcement,
    string? ImageUrl,
    string? UndoneExternalRef,
    ShopSettingsDocument Settings)
{
    /// <summary>No announcement: it may hold a reason a person wrote.</summary>
    public override string ToString() =>
        $"{nameof(CoordinatorActionRequest)} {{ ActionId = {ActionId}, Kind = {Kind}, Step = {Step}, Scenario = {Scenario}, Date = {Date} }}";
}

/// <summary>How an automatic action went.</summary>
public enum CoordinatorActionOutcome
{
    Succeeded,

    /// <summary>Tried and failed (<see cref="CoordinatorActionResult.FailureCode"/>): 〔重試〕.</summary>
    Failed,

    /// <summary>Not implemented, not authorized, token revoked: it becomes a reminder (Q18), with
    /// <see cref="CoordinatorActionResult.FailureCode"/> saying why when the member should know (e.g.
    /// <see cref="CoordinatorFailureCodes.TokenRevoked"/>).</summary>
    Unavailable,
}

/// <summary>An executor's answer.</summary>
public sealed record CoordinatorActionResult
{
    private CoordinatorActionResult(CoordinatorActionOutcome outcome, string? externalRef, string? failureCode)
    {
        Outcome = outcome;
        ExternalRef = externalRef;
        FailureCode = failureCode;
    }

    public CoordinatorActionOutcome Outcome { get; }

    /// <summary>What to undo it by later (at most <see cref="CoordinatorCommandAction.ExternalRefMaxLength"/>).</summary>
    public string? ExternalRef { get; }

    /// <summary>One of <see cref="CoordinatorFailureCodes"/> (or another code of ours): why it failed, or why an
    /// unavailable action became a reminder.</summary>
    public string? FailureCode { get; }

    public static CoordinatorActionResult Unavailable { get; } = new(CoordinatorActionOutcome.Unavailable, null, null);

    /// <summary>Unavailable for a reason the report tells the member (the reminder line shows
    /// <see cref="CoordinatorFailureCodes.Reason"/>), e.g. <see cref="CoordinatorFailureCodes.TokenRevoked"/>.</summary>
    public static CoordinatorActionResult UnavailableBecause(string code) =>
        CoordinatorCommandAction.IsValidFailureCode(code)
            ? new(CoordinatorActionOutcome.Unavailable, null, code)
            : throw new ArgumentException("A reason code is 1-32 of a-z, 0-9 and '-': a code of ours, never text from outside.", nameof(code));

    public static CoordinatorActionResult Succeeded(string? externalRef = null)
    {
        if (externalRef is { Length: > CoordinatorCommandAction.ExternalRefMaxLength })
        {
            throw new ArgumentException($"At most {CoordinatorCommandAction.ExternalRefMaxLength} characters.", nameof(externalRef));
        }

        return new(CoordinatorActionOutcome.Succeeded, string.IsNullOrEmpty(externalRef) ? null : externalRef, null);
    }

    public static CoordinatorActionResult Failed(string code) =>
        CoordinatorCommandAction.IsValidFailureCode(code)
            ? new(CoordinatorActionOutcome.Failed, null, code)
            : throw new ArgumentException("A failure code is 1-32 of a-z, 0-9 and '-': a code of ours, never text from outside.", nameof(code));
}

/// <summary>
/// The failure codes the report knows (<see cref="Reason"/> says each in Chinese). Executors of later slices may add
/// codes here; an unknown code reads 「執行失敗」.
/// </summary>
public static class CoordinatorFailureCodes
{
    /// <summary>An unexpected error (an exception in the executor, the job giving up).</summary>
    public const string Error = "error";

    /// <summary>The service refused the token or the authorization was revoked.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>LINE refused a broadcast: the account's monthly message limit is reached (a <c>429</c> saying so, #376).</summary>
    public const string QuotaExhausted = "quota-exhausted";

    /// <summary>LINE 群發 was not sent: its estimated recipients exceed what is left of the month's quota (#376).</summary>
    public const string QuotaInsufficient = "quota-insufficient";

    /// <summary>LINE did not answer, answered with a server error, or could not be reached (#376).</summary>
    public const string LineUnavailable = "line-unavailable";

    /// <summary>With <see cref="CoordinatorActionOutcome.Unavailable"/>: the service refused the shop's credential (or it
    /// can no longer be decrypted), so the action became a reminder — for LINE until the operator sets the token again
    /// (#376), for FB and Google until the owner authorizes again through the link the 統籌助理 pushes them
    /// (<see cref="ICoordinatorReauthorization"/>, #377).</summary>
    public const string TokenRevoked = "token-revoked";

    /// <summary>With <see cref="CoordinatorActionOutcome.Unavailable"/>: the platform's Google Cloud project may not call
    /// the Business Profile APIs yet (Basic API Access not approved: a quota of 0, or the API not enabled), so Google
    /// 特殊營業時間 became a reminder (#378).</summary>
    public const string GoogleAccessPending = "google-access-pending";

    /// <summary>With <see cref="CoordinatorActionOutcome.Unavailable"/>: the Google 商家 location has no regular hours, and
    /// Google sets special hours only for one that has (#378), so Google 特殊營業時間 became a reminder.</summary>
    public const string GoogleNoRegularHours = "google-no-regular-hours";

    /// <summary>Google did not answer, answered with a server error, or could not be reached (#378).</summary>
    public const string GoogleUnavailable = "google-unavailable";

    /// <summary>With <see cref="CoordinatorActionOutcome.Unavailable"/>: FB refused for want of a permission (Graph error
    /// 10 or 200–299: the Meta app's App Review has not passed, or the owner did not grant publishing), so FB 貼文 became
    /// a reminder (#379).</summary>
    public const string FacebookPermission = "facebook-permission";

    /// <summary>FB is down, answered with a server error or could not be reached (#379).</summary>
    public const string FacebookUnavailable = "facebook-unavailable";

    /// <summary>FB could not use the announcement's image (Graph error 324, or 100 on a photo post) (#379).</summary>
    public const string FacebookImage = "facebook-image";

    /// <summary>FB did not answer a post in time: it may have been posted (Graph has no idempotency key), so the
    /// member looks at the page before pressing 〔重試〕 (#379).</summary>
    public const string FacebookTimedOut = "facebook-timed-out";

    public const string RateLimited = "rate-limited";

    public const string TimedOut = "timed-out";

    public const string NetworkError = "network-error";

    /// <summary>The service answered with an error.</summary>
    public const string Rejected = "rejected";

    /// <summary>「{原因}」 of <paramref name="code"/> for an action of <paramref name="kind"/>: as <see cref="Reason(string?)"/>,
    /// except that FB's and Google's <see cref="TokenRevoked"/> is the owner's authorization (#377).</summary>
    public static string Reason(string? code, ShopActionKind kind) =>
        code == TokenRevoked && CoordinatorAuthorizations.ServiceOf(kind) is { } service
            ? $"{CoordinatorAuthorizations.Label(service)}授權失效，已改為手動；請老闆用統籌助理傳的連結重新授權"
            : Reason(code);

    /// <summary>「{原因}」 of <paramref name="code"/>, for the report.</summary>
    public static string Reason(string? code) => code switch
    {
        Unauthorized => "授權失效，請重新授權",
        QuotaExhausted => "LINE 本月訊息額度已用完",
        QuotaInsufficient => "LINE 本月剩餘額度不足，沒有群發",
        LineUnavailable => "LINE 暫時無法使用",
        GoogleAccessPending => "Google 商家 API 尚未開通，先以提醒處理",
        GoogleNoRegularHours => "Google 商家沒有設定一般營業時間，無法自動設定特殊營業時間，先以提醒處理",
        GoogleUnavailable => "Google 暫時無法使用",
        TokenRevoked => "LINE 權杖失效，已改為手動；請平台營運者重新設定權杖",
        FacebookPermission => "FB 權限不足（應用程式審核未通過或未授權發文），先以提醒處理",
        FacebookUnavailable => "FB 暫時無法使用",
        FacebookImage => "FB 讀不到公告圖片（圖片網址要能公開下載，JPG 或 PNG、4MB 以內）",
        FacebookTimedOut => "FB 沒有及時回應，貼文可能已經發出；按〔重試〕前請先到粉專確認",
        RateLimited => "服務忙碌中",
        TimedOut => "服務沒有回應",
        NetworkError => "連不上服務",
        Rejected => "服務拒絕了這次操作",
        Error => "系統錯誤",
        _ => "執行失敗",
    };
}
