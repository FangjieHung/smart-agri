using SmartAgri.Application.Databases;

namespace SmartAgri.Application.Organizations;

/// <summary>
/// The payload of a <see cref="Kind"/> background job (M6 plan §3 G): one organization's daily
/// conversation cleanup, due at <see cref="RunAt"/> (03:00 of a <c>Statistics:TimeZone</c> day). Each
/// organization with a retention in days has one chain of these: the job that wins the
/// compare-and-set on <c>Organization.RetentionCleanupNextRunAt</c> (equal to <see cref="RunAt"/>)
/// queues the next one, so a duplicate delivery neither deletes twice nor starts a second chain.
/// </summary>
public sealed record RetentionCleanupJob(DateTimeOffset RunAt)
{
    public const string Kind = "retention-cleanup";
}

/// <summary>
/// The calendar of the conversation retention (M6 plan §3 G): days are those of
/// <c>Statistics:TimeZone</c> (the zone the statistics and reports count in), not UTC days.
/// </summary>
public static class RetentionCleanupRules
{
    /// <summary>The local time of day the daily cleanup runs at.</summary>
    public static readonly TimeOnly RunTime = new(3, 0);

    /// <summary>Threads (and answer outcomes) deleted per batch, each batch its own transaction, so
    /// one cleanup never locks a large number of rows at once.</summary>
    public const int BatchSize = 1000;

    /// <summary>
    /// The cutoff for a retention of <paramref name="days"/> as of <paramref name="asOf"/>: 00:00 of
    /// the local day <paramref name="days"/> days before <paramref name="asOf"/>'s local day, as a UTC
    /// instant. A thread whose last message is strictly before it has expired; one at or after it is
    /// kept whole.
    /// </summary>
    public static DateTimeOffset Cutoff(DateTimeOffset asOf, int days, TimeZoneInfo timeZone)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        ArgumentNullException.ThrowIfNull(timeZone);
        return DatabaseFixedQueries.StartOfDay(DatabaseFixedQueries.DayOf(asOf, timeZone).AddDays(-days), timeZone);
    }

    /// <summary>The first local <see cref="RunTime"/> strictly after <paramref name="instant"/>, as a UTC
    /// instant (a time inside a DST gap moves one hour later).</summary>
    public static DateTimeOffset NextRunAfter(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var day = DatabaseFixedQueries.DayOf(instant, timeZone);
        var today = RunOn(day, timeZone);
        return today > instant ? today : RunOn(day.AddDays(1), timeZone);
    }

    private static DateTimeOffset RunOn(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(RunTime), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
