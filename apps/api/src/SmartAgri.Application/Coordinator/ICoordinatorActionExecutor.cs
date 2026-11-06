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

    /// <summary>Not implemented, not authorized, token revoked: it becomes a reminder (Q18).</summary>
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

    /// <summary>One of <see cref="CoordinatorFailureCodes"/> (or another code of ours).</summary>
    public string? FailureCode { get; }

    public static CoordinatorActionResult Unavailable { get; } = new(CoordinatorActionOutcome.Unavailable, null, null);

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

    /// <summary>LINE's monthly message quota is used up.</summary>
    public const string QuotaExceeded = "quota-exceeded";

    public const string RateLimited = "rate-limited";

    public const string TimedOut = "timed-out";

    public const string NetworkError = "network-error";

    /// <summary>The service answered with an error.</summary>
    public const string Rejected = "rejected";

    /// <summary>「{原因}」 of <paramref name="code"/>, for the report.</summary>
    public static string Reason(string? code) => code switch
    {
        Unauthorized => "授權失效，請重新授權",
        QuotaExceeded => "LINE 本月訊息額度不足",
        RateLimited => "服務忙碌中",
        TimedOut => "服務沒有回應",
        NetworkError => "連不上服務",
        Rejected => "服務拒絕了這次操作",
        Error => "系統錯誤",
        _ => "執行失敗",
    };
}
