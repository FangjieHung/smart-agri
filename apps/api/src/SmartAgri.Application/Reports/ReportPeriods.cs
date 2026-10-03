using SmartAgri.Application.Databases;
using SmartAgri.Domain.Reports;

namespace SmartAgri.Application.Reports;

/// <summary>
/// The periods of a periodic report (M4 #150): whole calendar weeks (Monday to Sunday) or months of
/// the statistics time zone (<c>Statistics:TimeZone</c>), the same days the fixed queries count in
/// (<see cref="DatabaseFixedQueries"/>). Pure: callers pass the day, already converted to the zone.
/// </summary>
/// <remarks>
/// A report is produced once its period is over, and compares with the <b>full calendar period before
/// it</b> (last week, last month — the same rule as <c>last-week</c>/<c>last-month</c>), whatever day
/// the job really runs on: a worker that was down for a day still reports the period that ended, not
/// the one it happens to run in.
/// </remarks>
public static class ReportPeriods
{
    /// <summary>The period of <paramref name="frequency"/> that contains <paramref name="day"/>.</summary>
    public static DatabaseQueryPeriod Containing(ReportFrequency frequency, DateOnly day) => frequency switch
    {
        ReportFrequency.Weekly => Week(day.AddDays(-(((int)day.DayOfWeek + 6) % 7))),
        ReportFrequency.Monthly => Month(new DateOnly(day.Year, day.Month, 1)),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Not a declared frequency."),
    };

    /// <summary>The period of <paramref name="frequency"/> that starts on <paramref name="from"/>, which
    /// must be a Monday (weekly) or the first of a month (monthly).</summary>
    public static DatabaseQueryPeriod Starting(ReportFrequency frequency, DateOnly from)
    {
        var period = Containing(frequency, from);
        if (period.From != from)
        {
            throw new ArgumentException($"{from:yyyy-MM-dd} does not start a {frequency} period.", nameof(from));
        }

        return period;
    }

    /// <summary>The period right after <paramref name="period"/>.</summary>
    public static DatabaseQueryPeriod After(ReportFrequency frequency, DatabaseQueryPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return Starting(frequency, period.To.AddDays(1));
    }

    /// <summary>The full period right before <paramref name="period"/>.</summary>
    public static DatabaseQueryPeriod Before(ReportFrequency frequency, DatabaseQueryPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return Containing(frequency, period.From.AddDays(-1));
    }

    /// <summary>The first instant at which the report of <paramref name="period"/> may be produced: the
    /// start of the day after it, in <paramref name="timeZone"/>.</summary>
    public static DateTimeOffset DueAt(DatabaseQueryPeriod period, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(period);
        return period.EndExclusive(timeZone);
    }

    private static DatabaseQueryPeriod Week(DateOnly monday) => new(null, monday, monday.AddDays(6));

    private static DatabaseQueryPeriod Month(DateOnly first) => new(null, first, first.AddMonths(1).AddDays(-1));
}
