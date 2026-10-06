using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using SmartAgri.Api.PublicChannels;

namespace SmartAgri.Api.Line;

/// <summary>The partition of <see cref="LineQuestionRateLimiter"/> that refused a question.</summary>
public enum LineQuestionRateLimit
{
    /// <summary><see cref="PublicRateLimitOptions.LineQuestionsPerUserPerMinute"/>.</summary>
    UserPerMinute,

    /// <summary><see cref="PublicRateLimitOptions.LineQuestionsPerUserPerHour"/>.</summary>
    UserPerHour,

    /// <summary><see cref="PublicRateLimitOptions.LineQuestionsPerGroupPerMinute"/>.</summary>
    GroupPerMinute,

    /// <summary><see cref="PublicRateLimitOptions.LineQuestionsPerAssistantPerMinute"/>.</summary>
    AssistantPerMinute,

    /// <summary><see cref="PublicRateLimitOptions.LineMaxConcurrentQuestionsPerAssistant"/>.</summary>
    AssistantConcurrency,
}

/// <summary>
/// What <see cref="LineQuestionRateLimiter.TryAcquire"/> decided. When <see cref="Acquired"/>, dispose it
/// once the answer has been sent (it holds the assistant's concurrency permit); otherwise
/// <see cref="RefusedBy"/> says which limit refused and <see cref="Notify"/> whether this is the first
/// refusal of that partition in its window — the only one that gets 「問題太頻繁了，請稍後再試」.
/// </summary>
public sealed class LineQuestionPermit : IDisposable
{
    private readonly IReadOnlyList<RateLimitLease> _leases;

    internal LineQuestionPermit(IReadOnlyList<RateLimitLease> leases, LineQuestionRateLimit? refusedBy, bool notify)
    {
        _leases = leases;
        RefusedBy = refusedBy;
        Notify = notify;
    }

    public bool Acquired => RefusedBy is null;

    public LineQuestionRateLimit? RefusedBy { get; }

    public bool Notify { get; }

    public void Dispose()
    {
        foreach (var lease in _leases)
        {
            lease.Dispose();
        }
    }
}

/// <summary>
/// The rate limits of LINE questions (M5b plan §3 F, decision E), applied by the background processor
/// rather than at the webhook endpoint: every request comes from LINE's servers, so there is no client
/// address to partition by. Programmatic <see cref="PartitionedRateLimiter"/>s, values in
/// <c>PublicChannels:RateLimits:Line*</c>:
/// <list type="bullet">
/// <item>one-to-one: per LINE user (and assistant), a one-minute and a one-hour sliding window;</item>
/// <item>group or room: per chat (and assistant), a one-minute sliding window, all members together;</item>
/// <item>then per assistant: a one-minute sliding window and the answers being generated at once.</item>
/// </list>
/// Narrowest first; permits taken before a later limit refuses are not given back (as for the visitor
/// API). A refused partition is told so once per window (the concurrency limit: once a minute) — for an
/// assistant-wide limit, that is the first chat refused — so a flood is not answered with a flood of
/// notices.
/// </summary>
/// <remarks>
/// In this process's memory, like the visitor limits and the conversation history: one API instance per
/// deployment. The windows count real time; the once-per-window notice uses the host's
/// <see cref="TimeProvider"/>. Partition keys hold LINE ids and are never logged.
/// </remarks>
public sealed class LineQuestionRateLimiter : IDisposable
{
    /// <summary>Remembered notices beyond which expired ones are pruned.</summary>
    private const int NoticePruneThreshold = 10_000;

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly TimeProvider _clock;
    private readonly PartitionedRateLimiter<string> _userPerMinute;
    private readonly PartitionedRateLimiter<string> _userPerHour;
    private readonly PartitionedRateLimiter<string> _groupPerMinute;
    private readonly PartitionedRateLimiter<string> _assistantPerMinute;
    private readonly PartitionedRateLimiter<string> _assistantConcurrency;
    private readonly Lock _noticesLock = new();
    private readonly Dictionary<(LineQuestionRateLimit, string), DateTimeOffset> _notices = [];

    public LineQuestionRateLimiter(IOptions<PublicChannelsOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        var limits = options.Value.RateLimits;
        _clock = clock;
        _userPerMinute = Sliding(limits.LineQuestionsPerUserPerMinute, Minute, segments: 6);
        _userPerHour = Sliding(limits.LineQuestionsPerUserPerHour, Hour, segments: 12);
        _groupPerMinute = Sliding(limits.LineQuestionsPerGroupPerMinute, Minute, segments: 6);
        _assistantPerMinute = Sliding(limits.LineQuestionsPerAssistantPerMinute, Minute, segments: 6);
        _assistantConcurrency = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = limits.LineMaxConcurrentQuestionsPerAssistant,
                QueueLimit = 0,
            }));
    }

    /// <summary>Takes a permit for a question of <paramref name="chatId"/> (the user in a one-to-one
    /// chat, else the group or room) to <paramref name="assistantId"/>, or says which limit refused.</summary>
    public LineQuestionPermit TryAcquire(Guid assistantId, string chatId, bool oneToOne)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatId);
        var chatKey = assistantId.ToString("N") + ":" + chatId;
        var assistantKey = assistantId.ToString("N");
        IEnumerable<(LineQuestionRateLimit Limit, PartitionedRateLimiter<string> Limiter, string Key, TimeSpan Window)> layers = oneToOne
            ?
            [
                (LineQuestionRateLimit.UserPerMinute, _userPerMinute, chatKey, Minute),
                (LineQuestionRateLimit.UserPerHour, _userPerHour, chatKey, Hour),
                (LineQuestionRateLimit.AssistantPerMinute, _assistantPerMinute, assistantKey, Minute),
                (LineQuestionRateLimit.AssistantConcurrency, _assistantConcurrency, assistantKey, Minute),
            ]
            :
            [
                (LineQuestionRateLimit.GroupPerMinute, _groupPerMinute, chatKey, Minute),
                (LineQuestionRateLimit.AssistantPerMinute, _assistantPerMinute, assistantKey, Minute),
                (LineQuestionRateLimit.AssistantConcurrency, _assistantConcurrency, assistantKey, Minute),
            ];

        var leases = new List<RateLimitLease>();
        foreach (var (limit, limiter, key, window) in layers)
        {
            var lease = limiter.AttemptAcquire(key);
            if (lease.IsAcquired)
            {
                leases.Add(lease);
                continue;
            }

            lease.Dispose();
            foreach (var taken in leases)
            {
                taken.Dispose();
            }

            // Once per partition and window (plan §3 F): an assistant-wide refusal is told to one chat.
            return new LineQuestionPermit([], limit, FirstNoticeInWindow(limit, key, window));
        }

        return new LineQuestionPermit(leases, refusedBy: null, notify: false);
    }

    public void Dispose()
    {
        _userPerMinute.Dispose();
        _userPerHour.Dispose();
        _groupPerMinute.Dispose();
        _assistantPerMinute.Dispose();
        _assistantConcurrency.Dispose();
    }

    private bool FirstNoticeInWindow(LineQuestionRateLimit limit, string partitionKey, TimeSpan window)
    {
        var now = _clock.GetUtcNow();
        lock (_noticesLock)
        {
            if (_notices.TryGetValue((limit, partitionKey), out var noticedAt) && now - noticedAt < window)
            {
                return false;
            }

            if (_notices.Count >= NoticePruneThreshold)
            {
                foreach (var expired in _notices.Where(entry => now - entry.Value >= Hour).Select(entry => entry.Key).ToList())
                {
                    _notices.Remove(expired);
                }
            }

            _notices[(limit, partitionKey)] = now;
            return true;
        }
    }

    private static PartitionedRateLimiter<string> Sliding(int permits, TimeSpan window, int segments) =>
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                SegmentsPerWindow = segments,
                QueueLimit = 0,
            }));
}
