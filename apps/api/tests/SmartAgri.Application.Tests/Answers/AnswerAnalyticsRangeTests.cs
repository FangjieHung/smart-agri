using Shouldly;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Databases;

namespace SmartAgri.Application.Tests.Answers;

/// <summary>
/// The date range of the answer analytics, the operations summary and the case statistics (#345): days
/// are calendar days of <c>Statistics:TimeZone</c>, cut exactly like the database fixed queries
/// (<see cref="DatabaseFixedQueries.StartOfDay"/>/<see cref="DatabaseFixedQueries.DayOf"/>), not UTC
/// days; the default (newest 30) and the limit (180) count local days, DST included.
/// </summary>
public class AnswerAnalyticsRangeTests
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static DateTimeOffset At(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

    private static AnswerAnalyticsRange Day(string day, TimeZoneInfo zone)
    {
        var date = DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture);
        var (range, error) = AnswerAnalyticsRange.Resolve(date, date, At("2026-10-13T00:00:00Z"), zone);
        error.ShouldBeNull();
        return range.ShouldNotBeNull();
    }

    private static bool Contains(AnswerAnalyticsRange range, DateTimeOffset at) => at >= range.FromUtc && at < range.ToExclusiveUtc;

    [Fact]
    public void A_Taipei_day_runs_from_local_midnight_to_local_midnight_not_from_08_00()
    {
        var day = Day("2026-10-03", Taipei);

        day.FromUtc.ShouldBe(At("2026-10-02T16:00:00Z"), "00:00 in Taipei is 16:00 UTC the day before");
        day.ToExclusiveUtc.ShouldBe(At("2026-10-03T16:00:00Z"));

        // Same UTC date (Oct 3), different Taipei days: 08:00 on the 3rd and 07:59 on the 4th.
        Contains(day, At("2026-10-03T00:00:00Z")).ShouldBeTrue("Taipei 10-03 08:00");
        Contains(day, At("2026-10-03T23:59:00Z")).ShouldBeFalse("Taipei 10-04 07:59, though UTC is still Oct 3");
        Contains(Day("2026-10-04", Taipei), At("2026-10-03T23:59:00Z")).ShouldBeTrue();
        // And 07:59 on the 3rd (UTC Oct 2) is Taipei's Oct 3, not Oct 2.
        Contains(day, At("2026-10-02T23:59:00Z")).ShouldBeTrue("Taipei 10-03 07:59");
        Contains(Day("2026-10-02", Taipei), At("2026-10-02T23:59:00Z")).ShouldBeFalse("UTC Oct 2 23:59 is not Taipei's Oct 2");

        // Both sides of Taipei midnight: 23:59 is still Oct 3, 00:00 is Oct 4.
        Contains(day, At("2026-10-03T15:59:00Z")).ShouldBeTrue("Taipei 10-03 23:59");
        Contains(day, At("2026-10-03T16:00:00Z")).ShouldBeFalse("Taipei 10-04 00:00");
        Contains(Day("2026-10-04", Taipei), At("2026-10-03T16:00:00Z")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("2026-10-02T23:59:00Z")]
    [InlineData("2026-10-03T00:00:00Z")]
    [InlineData("2026-10-03T15:59:59Z")]
    [InlineData("2026-10-03T16:00:00Z")]
    [InlineData("2026-03-08T04:59:59Z")]
    [InlineData("2026-03-08T05:00:00Z")]
    [InlineData("2026-11-01T04:30:00Z")]
    [InlineData("2026-11-01T05:30:00Z")]
    public void An_instant_falls_in_the_day_the_database_fixed_queries_give_it(string instant)
    {
        foreach (var zone in new[] { Taipei, NewYork })
        {
            var at = At(instant);
            var day = DatabaseFixedQueries.DayOf(at, zone);
            var range = Day(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), zone);
            Contains(range, at).ShouldBeTrue($"{instant} in {zone.Id}");
            (range.FromUtc, range.ToExclusiveUtc).ShouldBe(
                (DatabaseFixedQueries.StartOfDay(day, zone), DatabaseFixedQueries.StartOfDay(day.AddDays(1), zone)));
        }
    }

    [Fact]
    public void Today_and_the_default_newest_30_days_are_local_days()
    {
        // 2026-10-12 23:30 UTC is already 10-13 07:30 in Taipei.
        var (range, error) = AnswerAnalyticsRange.Resolve(null, null, At("2026-10-12T23:30:00Z"), Taipei);
        error.ShouldBeNull();
        (range!.From, range.To).ShouldBe((new DateOnly(2026, 9, 14), new DateOnly(2026, 10, 13)));

        var (beforeMidnight, _) = AnswerAnalyticsRange.Resolve(null, null, At("2026-10-13T15:59:00Z"), Taipei);
        beforeMidnight!.To.ShouldBe(new DateOnly(2026, 10, 13), "Taipei 23:59 is still the 13th");
        var (afterMidnight, _) = AnswerAnalyticsRange.Resolve(null, null, At("2026-10-13T16:00:00Z"), Taipei);
        afterMidnight!.To.ShouldBe(new DateOnly(2026, 10, 14), "Taipei 00:00 is the 14th, though UTC is still the 13th");
    }

    [Fact]
    public void The_limit_counts_local_days()
    {
        var to = new DateOnly(2026, 10, 13);
        AnswerAnalyticsRange.Resolve(to.AddDays(-(AnswerAnalyticsRange.MaxDays - 1)), to, At("2026-10-13T00:00:00Z"), Taipei).Range
            .ShouldNotBeNull();
        AnswerAnalyticsRange.Resolve(to.AddDays(-AnswerAnalyticsRange.MaxDays), to, At("2026-10-13T00:00:00Z"), Taipei).Error
            .ShouldBe("日期範圍最長 180 天。");
        AnswerAnalyticsRange.Resolve(to.AddDays(1), to, At("2026-10-13T00:00:00Z"), Taipei).Error
            .ShouldBe("起始日期必須不晚於結束日期。");
    }

    [Fact]
    public void A_DST_day_is_converted_with_the_zones_rules()
    {
        // New York sprang forward at 02:00 on 2026-03-08 (UTC-5 -> UTC-4): a 23-hour local day.
        var spring = Day("2026-03-08", NewYork);
        (spring.FromUtc, spring.ToExclusiveUtc).ShouldBe((At("2026-03-08T05:00:00Z"), At("2026-03-09T04:00:00Z")));
        (spring.ToExclusiveUtc - spring.FromUtc).ShouldBe(TimeSpan.FromHours(23));

        // And fell back at 02:00 on 2026-11-01 (UTC-4 -> UTC-5): a 25-hour local day.
        var fall = Day("2026-11-01", NewYork);
        (fall.FromUtc, fall.ToExclusiveUtc).ShouldBe((At("2026-11-01T04:00:00Z"), At("2026-11-02T05:00:00Z")));
        (fall.ToExclusiveUtc - fall.FromUtc).ShouldBe(TimeSpan.FromHours(25));

        // A range across the change still spans whole local days.
        var (march, _) = AnswerAnalyticsRange.Resolve(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), At("2026-10-13T00:00:00Z"), NewYork);
        (march!.FromUtc, march.ToExclusiveUtc).ShouldBe((At("2026-03-01T05:00:00Z"), At("2026-04-01T04:00:00Z")));
    }
}
