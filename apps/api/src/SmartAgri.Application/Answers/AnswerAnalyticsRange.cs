using SmartAgri.Application.Databases;

namespace SmartAgri.Application.Answers;

/// <summary>
/// A validated <c>from</c>/<c>to</c> date range for the answer-outcome analytics endpoints
/// (M3.5 plan §5, Slice 6): <c>GET .../assistants/{id}/analytics</c>,
/// <c>GET .../operations/summary</c>, and the case statistics with the case list's
/// <c>closedFrom</c>/<c>closedTo</c> (M7 decision K). Defaults to the newest <see cref="DefaultDays"/>
/// days (inclusive of today); the whole range may span at most <see cref="MaxDays"/> days.
/// </summary>
/// <remarks>
/// <b>Dates are calendar days of the statistics time zone</b> (<c>Statistics:TimeZone</c>, #345), the
/// days the database fixed queries count in (<see cref="DatabaseFixedQueries.StartOfDay"/>,
/// <see cref="DatabaseFixedQueries.DayOf"/>): in Taipei the day changes at 00:00 local time, not at
/// 08:00 (UTC midnight). The SQL filter is the UTC half-open range
/// <c>[<see cref="FromUtc"/>, <see cref="ToExclusiveUtc"/>)</c> of those days, converted with the zone's
/// rules (DST included), so a range of N days is N local days even when one of them is 23 or 25 hours long.
/// </remarks>
/// <param name="From">The first local day, inclusive.</param>
/// <param name="To">The last local day, inclusive.</param>
/// <param name="TimeZone">The statistics time zone the days are calendar days of.</param>
public sealed record AnswerAnalyticsRange(DateOnly From, DateOnly To, TimeZoneInfo TimeZone)
{
    public const int DefaultDays = 30;

    public const int MaxDays = 180;

    /// <summary>The range's first instant, inclusive: 00:00 of <see cref="From"/> in
    /// <see cref="TimeZone"/>, as UTC.</summary>
    public DateTimeOffset FromUtc => DatabaseFixedQueries.StartOfDay(From, TimeZone);

    /// <summary>The instant just after the range's last day (00:00 of the day after
    /// <see cref="To"/> in <see cref="TimeZone"/>, as UTC): filter with <c>At &lt;</c> this,
    /// never <c>&lt;=</c>, so the whole of <see cref="To"/> is included regardless of time of
    /// day.</summary>
    public DateTimeOffset ToExclusiveUtc => DatabaseFixedQueries.StartOfDay(To.AddDays(1), TimeZone);

    /// <summary>
    /// Resolves <paramref name="from"/>/<paramref name="to"/> (both optional, local days of
    /// <paramref name="timeZone"/>) against <paramref name="now"/>: missing <paramref name="to"/> is
    /// today in <paramref name="timeZone"/>; missing <paramref name="from"/> is
    /// <see cref="DefaultDays"/> days before the resolved <paramref name="to"/>. Fails when
    /// <paramref name="from"/> is after <paramref name="to"/>, or the range spans more than
    /// <see cref="MaxDays"/> days.
    /// </summary>
    public static (AnswerAnalyticsRange? Range, string? Error) Resolve(
        DateOnly? from, DateOnly? to, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var resolvedTo = to ?? DatabaseFixedQueries.DayOf(now, timeZone);
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

        return (new AnswerAnalyticsRange(resolvedFrom, resolvedTo, timeZone), null);
    }
}
