using System.Globalization;
using System.Text.Json.Nodes;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// The line under an action of the preview card (store-coordinator design §3 Q14, #376): for LINE 群發, how many
/// messages it will use and how many are left this month. Called for the actions the settings want automatic, before
/// their effective mode is decided (<see cref="ICoordinatorActionModes"/>): finding the integration unusable (LINE
/// refusing the token) makes it unavailable, so the card then shows the action as 提醒.
/// </summary>
public interface ICoordinatorActionNotes
{
    /// <summary>The note of <paramref name="kind"/> for <paramref name="organizationId"/>, or <see langword="null"/>.</summary>
    ValueTask<string?> NoteAsync(Guid organizationId, ShopActionKind kind, CancellationToken cancellationToken);
}

/// <summary>No notes (the default before an integration registers its own).</summary>
public sealed class NoCoordinatorActionNotes : ICoordinatorActionNotes
{
    public ValueTask<string?> NoteAsync(Guid organizationId, ShopActionKind kind, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>(null);
}

/// <summary>
/// What LINE says before a broadcast (#376): what is left of the month's quota and the friends who would receive it.
/// </summary>
/// <param name="Remaining">The month's limit minus what was sent (LINE's own approximate count, OA Manager sends
/// included), never below 0; <see langword="null"/> when no limit is set (quota <c>none</c>).</param>
/// <param name="Followers">Friends as LINE's insight counts them (every first add; never decreases), when it has the
/// numbers.</param>
/// <param name="Blocks">Friends who blocked the account, with <paramref name="Followers"/>.</param>
public sealed record LineBroadcastEstimate(long? Remaining, long? Followers, long? Blocks)
{
    /// <summary>About how many messages a broadcast uses: friends minus blocks (an upper bound — accounts deleted since
    /// are still counted); <see langword="null"/> when LINE has no numbers yet.</summary>
    public long? Recipients => Followers is { } followers && Blocks is { } blocks ? Math.Max(0, followers - blocks) : null;

    /// <summary>A broadcast would exceed the quota: nothing is left, or the estimate is more than what is left. An
    /// unknown estimate with something left is allowed.</summary>
    public bool Insufficient => Remaining is { } remaining && (remaining <= 0 || Recipients > remaining);

    /// <summary>The estimate from LINE's answers. <paramref name="consumption"/> is required when the quota is limited;
    /// <paramref name="followers"/> counts only when ready.</summary>
    public static LineBroadcastEstimate Of(LineMessageQuota quota, LineQuotaConsumption? consumption, LineFollowersInsight? followers)
    {
        ArgumentNullException.ThrowIfNull(quota);
        long? remaining = null;
        if (quota.Limited)
        {
            ArgumentNullException.ThrowIfNull(consumption);
            remaining = Math.Max(0, (quota.Value ?? 0) - consumption.TotalUsage);
        }

        return followers is { IsReady: true }
            ? new LineBroadcastEstimate(remaining, followers.Followers, followers.Blocks)
            : new LineBroadcastEstimate(remaining, null, null);
    }
}

/// <summary>
/// LINE 群發 (store-coordinator design §2.2, §3 Q10, Q14, Q21, §5 Slice 5, #376): the card's note, the messages sent,
/// the day whose friend count is asked for, and how LINE's failures are reported — pure.
/// </summary>
public static class CoordinatorLineBroadcast
{
    /// <summary>The start of the note when the quota is not enough.</summary>
    public const string InsufficientNote = "額度不足，無法群發";

    /// <summary>The note when the month's quota could not be read.</summary>
    public const string QuotaUnknownNote = "暫時查不到 LINE 本月額度";

    /// <summary>The zone of LINE's insight dates (UTC+9).</summary>
    public static readonly TimeSpan InsightOffset = TimeSpan.FromHours(9);

    /// <summary>
    /// The card's note (Q14): 「約 N 則（好友 F－封鎖 B），本月剩 M 則」, 「…本月不限則數」 when no limit is set,
    /// 「則數無法估計（LINE 尚未提供好友數），…」 when LINE has no friend count yet; 「額度不足，無法群發：…」 first when
    /// <see cref="LineBroadcastEstimate.Insufficient"/>.
    /// </summary>
    public static string Note(LineBroadcastEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var used = estimate.Recipients is { } recipients
            ? $"約 {Number(recipients)} 則（好友 {Number(estimate.Followers!.Value)}－封鎖 {Number(estimate.Blocks!.Value)}）"
            : "則數無法估計（LINE 尚未提供好友數）";
        var left = estimate.Remaining is { } remaining ? $"本月剩 {Number(remaining)} 則" : "本月不限則數";
        var note = $"{used}，{left}";
        return estimate.Insufficient ? $"{InsufficientNote}：{note}" : note;
    }

    /// <summary>The day whose friend count to ask for first: yesterday in UTC+9 (LINE needs about a day to count; the
    /// day before is asked when it is not ready).</summary>
    public static DateOnly FollowersDate(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(InsightOffset).DateTime).AddDays(-1);

    /// <summary>What a broadcast sends: the announcement and, when the settings have one, its image (decision B: an
    /// HTTPS JPEG or PNG of at most 1 MB, the same address as original and preview).</summary>
    public static IReadOnlyList<JsonObject> Messages(string announcement, string? imageUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(announcement);
        List<JsonObject> messages = [LineMessages.Text(announcement)];
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            messages.Add(LineMessages.Image(imageUrl));
        }

        return messages;
    }

    /// <summary>
    /// The failure code of a LINE call that did not succeed, other than LINE refusing the token (which makes the action
    /// a reminder): the monthly limit → <see cref="CoordinatorFailureCodes.QuotaExhausted"/>, another <c>429</c> →
    /// <see cref="CoordinatorFailureCodes.RateLimited"/>, no answer, a server error or an unreadable answer →
    /// <see cref="CoordinatorFailureCodes.LineUnavailable"/>, another refusal (e.g. an image LINE cannot use) →
    /// <see cref="CoordinatorFailureCodes.Rejected"/>.
    /// </summary>
    public static string FailureCodeOf(LineApiResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result switch
        {
            { Outcome: LineApiOutcome.Succeeded or LineApiOutcome.Unauthorized } =>
                throw new ArgumentException("Only a failure other than a refused token has a failure code.", nameof(result)),
            { IsMonthlyLimit: true } => CoordinatorFailureCodes.QuotaExhausted,
            { Outcome: LineApiOutcome.RateLimited } => CoordinatorFailureCodes.RateLimited,
            { Outcome: LineApiOutcome.HttpError, StatusCode: >= 400 and < 500 } => CoordinatorFailureCodes.Rejected,
            _ => CoordinatorFailureCodes.LineUnavailable,
        };
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
