namespace SmartAgri.Application.Answers;

/// <summary>
/// A validated <c>from</c>/<c>to</c> date range for the answer-outcome analytics endpoints
/// (M3.5 plan §5, Slice 6): <c>GET .../assistants/{id}/analytics</c> and
/// <c>GET .../operations/summary</c>. Defaults to the newest <see cref="DefaultDays"/> days
/// (inclusive of today, in UTC); the whole range may span at most <see cref="MaxDays"/> days.
/// </summary>
public sealed record AnswerAnalyticsRange(DateOnly From, DateOnly To)
{
    public const int DefaultDays = 30;

    public const int MaxDays = 180;

    /// <summary>The range's first instant, inclusive.</summary>
    public DateTimeOffset FromUtc => new(From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>The instant just after the range's last day: filter with <c>At &lt;</c> this,
    /// never <c>&lt;=</c>, so the whole of <see cref="To"/> is included regardless of time of
    /// day.</summary>
    public DateTimeOffset ToExclusiveUtc => new(To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// Resolves <paramref name="from"/>/<paramref name="to"/> (both optional) against
    /// <paramref name="now"/>: missing <paramref name="to"/> is today (UTC); missing
    /// <paramref name="from"/> is <see cref="DefaultDays"/> days before the resolved
    /// <paramref name="to"/>. Fails when <paramref name="from"/> is after
    /// <paramref name="to"/>, or the range spans more than <see cref="MaxDays"/> days.
    /// </summary>
    public static (AnswerAnalyticsRange? Range, string? Error) Resolve(DateOnly? from, DateOnly? to, DateTimeOffset now)
    {
        var resolvedTo = to ?? DateOnly.FromDateTime(now.UtcDateTime);
        var resolvedFrom = from ?? resolvedTo.AddDays(-(DefaultDays - 1));

        if (resolvedFrom > resolvedTo)
        {
            return (null, "起始日期必須不晚於結束日期。");
        }

        var days = resolvedTo.DayNumber - resolvedFrom.DayNumber + 1;
        if (days > MaxDays)
        {
            return (null, $"日期範圍最長 {MaxDays} 天。");
        }

        return (new AnswerAnalyticsRange(resolvedFrom, resolvedTo), null);
    }
}
