using Shouldly;
using SmartAgri.Application.Organizations;

namespace SmartAgri.Application.Tests.Organizations;

/// <summary>The retention calendar is <c>Statistics:TimeZone</c>'s, not UTC's (M6 plan §3 G, issue #241).</summary>
public class RetentionCleanupRulesTests
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    [Fact]
    public void The_cutoff_is_local_midnight_n_days_before_the_local_today()
    {
        // 2026-03-01 17:00 UTC is already 2026-03-02 01:00 in Taipei: the days differ.
        var asOf = new DateTimeOffset(2026, 3, 1, 17, 0, 0, TimeSpan.Zero);

        // Taipei: today 03-02, minus 30 days = 01-31, 00:00 +08 = 01-30 16:00 UTC.
        RetentionCleanupRules.Cutoff(asOf, 30, Taipei).ShouldBe(new DateTimeOffset(2026, 1, 30, 16, 0, 0, TimeSpan.Zero));

        // The same instant counted in UTC days would be a day earlier.
        RetentionCleanupRules.Cutoff(asOf, 30, TimeZoneInfo.Utc).ShouldBe(new DateTimeOffset(2026, 1, 30, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_cutoff_moves_only_at_local_midnight()
    {
        // 15:59 UTC is 23:59 in Taipei, 16:00 UTC is 00:00 the next day.
        var beforeMidnight = new DateTimeOffset(2026, 10, 6, 15, 59, 59, TimeSpan.Zero);
        var atMidnight = new DateTimeOffset(2026, 10, 6, 16, 0, 0, TimeSpan.Zero);

        RetentionCleanupRules.Cutoff(beforeMidnight, 90, Taipei).ShouldBe(new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.FromHours(8)));
        RetentionCleanupRules.Cutoff(atMidnight, 90, Taipei).ShouldBe(new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.FromHours(8)));
        Should.Throw<ArgumentOutOfRangeException>(() => RetentionCleanupRules.Cutoff(atMidnight, 0, Taipei));
    }

    [Fact]
    public void The_next_run_is_the_first_local_three_oclock_strictly_after()
    {
        var threeInTaipei = new DateTimeOffset(2026, 10, 6, 19, 0, 0, TimeSpan.Zero); // 10-07 03:00 +08

        RetentionCleanupRules.NextRunAfter(threeInTaipei.AddSeconds(-1), Taipei).ShouldBe(threeInTaipei);
        RetentionCleanupRules.NextRunAfter(threeInTaipei, Taipei).ShouldBe(threeInTaipei.AddDays(1));
        // 18:00 in Taipei on 10-06 (10:00 UTC): the coming night.
        RetentionCleanupRules.NextRunAfter(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero), Taipei).ShouldBe(threeInTaipei);

        // The same instant in UTC days: 03:00 UTC of 10-07.
        RetentionCleanupRules.NextRunAfter(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc)
            .ShouldBe(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero));
        RetentionCleanupRules.BatchSize.ShouldBe(1000);
    }
}
