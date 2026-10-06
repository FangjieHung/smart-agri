using System.Diagnostics.Metrics;
using SmartAgri.Domain.Observability;

namespace SmartAgri.Api.Line;

/// <summary>
/// The LINE answers' metrics (M5b plan §3 D and §7 risk 1), on the application's meter
/// (<see cref="SmartAgriMeter"/>). Never content and never a LINE id: only how long an answer took,
/// how it was delivered and the kind of chat.
/// </summary>
public sealed class LineAnswerMetrics
{
    /// <summary>Histogram, seconds: from receiving the event to the answer being sent (or dropped),
    /// tagged <see cref="DeliveryTag"/> and <see cref="ChatTag"/>.</summary>
    public const string AnswerDurationInstrument = "smartagri.line.answer.duration";

    /// <summary>Counter: answers pushed because the reply token could no longer be used (「補送」).</summary>
    public const string PushFallbacksInstrument = "smartagri.line.push_fallbacks";

    /// <summary>Counter: questions a rate limit refused, tagged <see cref="LimitTag"/>.</summary>
    public const string RateLimitedInstrument = "smartagri.line.rate_limited";

    /// <summary><c>reply</c>, <c>push</c> or <c>dropped</c>.</summary>
    public const string DeliveryTag = "delivery";

    /// <summary><c>one-to-one</c> or <c>group</c> (a group or room).</summary>
    public const string ChatTag = "chat";

    public const string LimitTag = "limit";

    private readonly Histogram<double> _duration;
    private readonly Counter<long> _pushFallbacks;
    private readonly Counter<long> _rateLimited;

    public LineAnswerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(SmartAgriMeter.Name);
        _duration = meter.CreateHistogram<double>(
            AnswerDurationInstrument, unit: "s", description: "LINE answers: time from the webhook event to delivery, by delivery and chat kind.");
        _pushFallbacks = meter.CreateCounter<long>(
            PushFallbacksInstrument, unit: "{message}", description: "LINE answers sent by push because the reply deadline had passed or the reply token was refused.");
        _rateLimited = meter.CreateCounter<long>(
            RateLimitedInstrument, unit: "{question}", description: "LINE questions refused by a rate limit, by limit.");
    }

    internal void RecordAnswer(TimeSpan elapsed, string delivery, bool oneToOne) =>
        _duration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>(DeliveryTag, delivery),
            new KeyValuePair<string, object?>(ChatTag, oneToOne ? "one-to-one" : "group"));

    internal void RecordPushFallback() => _pushFallbacks.Add(1);

    internal void RecordRateLimited(LineQuestionRateLimit limit) =>
        _rateLimited.Add(1, new KeyValuePair<string, object?>(LimitTag, limit.ToString()));
}
