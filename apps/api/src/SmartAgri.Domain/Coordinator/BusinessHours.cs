namespace SmartAgri.Domain.Coordinator;

/// <summary>A business period on a particular date, as instants: opens at <see cref="Opens"/>,
/// closes at <see cref="Closes"/> (later; the next day's instant for an overnight period). Both carry
/// the shop's UTC offset at that moment.</summary>
public sealed record BusinessInterval(DateTimeOffset Opens, DateTimeOffset Closes);

/// <summary>
/// When a shop is open (store-coordinator design §3 Q7/Q20, §5 Slice 2, #373): pure calculations over
/// its <see cref="WeeklyHours"/> in its time zone, for any date — today or weeks ahead — so Slice 4 can
/// schedule its reminders (<c>RunAfter</c>) and Slice 3 can fill <c>{恢復營業}</c>.
/// </summary>
/// <remarks>
/// <para>
/// A period belongs to the date it <em>opens</em> on: Saturday's 18:00–02:00 is one of Saturday's
/// periods and closes on Sunday at 02:00. A date is the shop's local date.
/// </para>
/// <para>
/// Daylight saving time: a local time that does not exist (skipped when clocks go forward) is moved
/// forward by the gap — 02:30 on the day New York skips 02:00–03:00 is 03:30 — and a local time that
/// happens twice (clocks go back) is the first one, so a shop opens as early as its sign says.
/// </para>
/// </remarks>
public sealed class BusinessHours
{
    /// <summary>A week and a day: every weekday is looked at once, plus the starting one again.</summary>
    private const int SearchDays = 8;

    private readonly WeeklyHours _weekly;

    public BusinessHours(WeeklyHours weekly, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(weekly);
        ArgumentNullException.ThrowIfNull(zone);
        _weekly = weekly;
        Zone = zone;
    }

    public TimeZoneInfo Zone { get; }

    /// <summary>The shop's local date at <paramref name="instant"/>.</summary>
    public DateOnly DateAt(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    /// <summary>The instant the shop's clock shows <paramref name="time"/> on <paramref name="date"/>
    /// (with the daylight-saving rules above), at the shop's offset.</summary>
    public DateTimeOffset At(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local))
        {
            // Skipped: read the wall-clock time with the offset in force before the jump, which lands
            // as far past the jump as the time was past its start.
            var before = Zone.GetUtcOffset(local.AddDays(-1));
            return TimeZoneInfo.ConvertTime(new DateTimeOffset(local, before), Zone);
        }

        if (Zone.IsAmbiguousTime(local))
        {
            // Twice: the larger offset is the earlier instant.
            return new DateTimeOffset(local, Zone.GetAmbiguousTimeOffsets(local).Max());
        }

        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    /// <summary>The periods that open on <paramref name="date"/>, earliest first; empty on a regular
    /// day off.</summary>
    public IReadOnlyList<BusinessInterval> PeriodsOn(DateOnly date) =>
    [
        .. _weekly[date.DayOfWeek]
            .Select(period => new BusinessInterval(
                At(date, period.Open),
                At(period.IsOvernight ? date.AddDays(1) : date, period.Close)))
            .OrderBy(interval => interval.Opens),
    ];

    /// <summary>
    /// The instant of <paramref name="time"/> on the business day <paramref name="date"/> — a 店休 command's 提早打烊
    /// closing or 延後開店 opening: on <paramref name="date"/> itself, or on the next day when that is the small hours of
    /// the date's overnight period (Saturday's 18:00–02:00 closing early at 01:00 is Sunday 01:00).
    /// </summary>
    public DateTimeOffset OnBusinessDay(DateOnly date, TimeOnly time)
    {
        var instant = At(date, time);
        var periods = PeriodsOn(date);
        if (periods.Count > 0
            && instant <= periods[0].Opens
            && At(date.AddDays(1), time) is var nextDay
            && nextDay < periods.Max(period => period.Closes))
        {
            return nextDay;
        }

        return instant;
    }

    /// <summary>When the shop first opens on <paramref name="date"/>; <see langword="null"/> on a regular
    /// day off. Slice 4 reminds the owner to pause ordering before this on a closed day.</summary>
    public DateTimeOffset? FirstOpeningOn(DateOnly date) => PeriodsOn(date) is [var first, ..] ? first.Opens : null;

    /// <summary>
    /// The first opening of a date after <paramref name="date"/> — when the shop opens again after
    /// being closed all of <paramref name="date"/> (「下一個營業日開店前」, Q7, and <c>{恢復營業}</c>). An
    /// overnight period opening on <paramref name="date"/> is part of the closed day, not a reopening.
    /// <see langword="null"/> only when the week has no business period at all.
    /// </summary>
    public DateTimeOffset? FirstOpeningAfter(DateOnly date)
    {
        for (var offset = 1; offset <= SearchDays; offset++)
        {
            if (FirstOpeningOn(date.AddDays(offset)) is { } opens)
            {
                return opens;
            }
        }

        return null;
    }

    /// <summary>The first opening strictly after <paramref name="instant"/>; <see langword="null"/>
    /// only when the week has no business period at all.</summary>
    public DateTimeOffset? NextOpeningAfter(DateTimeOffset instant)
    {
        var start = DateAt(instant).AddDays(-1);
        for (var offset = 0; offset <= SearchDays; offset++)
        {
            foreach (var interval in PeriodsOn(start.AddDays(offset)))
            {
                if (interval.Opens > instant)
                {
                    return interval.Opens;
                }
            }
        }

        return null;
    }
}
