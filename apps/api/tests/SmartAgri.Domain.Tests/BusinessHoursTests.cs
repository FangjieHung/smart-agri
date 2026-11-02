using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// When a shop is open (store-coordinator design §5 Slice 2, #373): the periods of a date as instants,
/// the first opening of a date, the reopening after a closed day and the next opening after an instant —
/// with regular days off, overnight periods, future dates and daylight saving time.
/// </summary>
public class BusinessHoursTests
{
    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    /// <summary>The valid sample: closed Mondays; Tuesday–Friday 11:00–14:00 and 17:00–21:00; Saturday
    /// 18:00–02:00; Sunday 11:00–14:00; Asia/Taipei.</summary>
    private static BusinessHours Shop => ShopSettingsSamples.ValidDocument().Hours;

    [Fact]
    public void A_dates_periods_are_instants_in_the_shops_time_zone_earliest_first()
    {
        Shop.PeriodsOn(new DateOnly(2026, 11, 3)).ShouldBe(
        [
            new BusinessInterval(new DateTimeOffset(2026, 11, 3, 11, 0, 0, Taipei), new DateTimeOffset(2026, 11, 3, 14, 0, 0, Taipei)),
            new BusinessInterval(new DateTimeOffset(2026, 11, 3, 17, 0, 0, Taipei), new DateTimeOffset(2026, 11, 3, 21, 0, 0, Taipei)),
        ]);
        Shop.PeriodsOn(new DateOnly(2026, 11, 3))[0].Opens.Offset.ShouldBe(Taipei);
    }

    [Fact]
    public void Periods_listed_out_of_order_come_back_earliest_first()
    {
        var hours = Hours("Asia/Taipei", (DayOfWeek.Tuesday, "17:00", "21:00"), (DayOfWeek.Tuesday, "07:00", "10:00"));

        hours.PeriodsOn(new DateOnly(2026, 11, 3)).Select(interval => interval.Opens.Hour).ShouldBe([7, 17]);
        hours.FirstOpeningOn(new DateOnly(2026, 11, 3)).ShouldBe(new DateTimeOffset(2026, 11, 3, 7, 0, 0, Taipei));
    }

    [Fact]
    public void An_overnight_period_belongs_to_the_day_it_opens_and_closes_the_next_day()
    {
        var saturday = Shop.PeriodsOn(new DateOnly(2026, 11, 7)).ShouldHaveSingleItem();

        saturday.ShouldBe(new BusinessInterval(new DateTimeOffset(2026, 11, 7, 18, 0, 0, Taipei), new DateTimeOffset(2026, 11, 8, 2, 0, 0, Taipei)));
        Shop.PeriodsOn(new DateOnly(2026, 11, 8)).ShouldHaveSingleItem().Opens.ShouldBe(new DateTimeOffset(2026, 11, 8, 11, 0, 0, Taipei));
    }

    [Fact]
    public void A_regular_day_off_has_no_periods_and_no_opening()
    {
        var monday = new DateOnly(2026, 11, 2);

        Shop.PeriodsOn(monday).ShouldBeEmpty();
        Shop.FirstOpeningOn(monday).ShouldBeNull();
    }

    [Fact]
    public void The_first_opening_of_a_date_weeks_ahead()
    {
        var christmas = new DateOnly(2026, 12, 25); // a Friday, seven weeks ahead

        Shop.FirstOpeningOn(christmas).ShouldBe(new DateTimeOffset(2026, 12, 25, 11, 0, 0, Taipei));
        Shop.FirstOpeningOn(new DateOnly(2027, 6, 5)).ShouldBe(new DateTimeOffset(2027, 6, 5, 18, 0, 0, Taipei)); // a Saturday
    }

    [Fact]
    public void After_a_closed_day_the_shop_reopens_at_the_next_dates_first_opening()
    {
        // Closed on Tuesday: reopens Wednesday for lunch.
        Shop.FirstOpeningAfter(new DateOnly(2026, 11, 3)).ShouldBe(new DateTimeOffset(2026, 11, 4, 11, 0, 0, Taipei));

        // Closed on Sunday, and Monday is a regular day off: reopens Tuesday.
        Shop.FirstOpeningAfter(new DateOnly(2026, 11, 8)).ShouldBe(new DateTimeOffset(2026, 11, 10, 11, 0, 0, Taipei));

        // Closed on Saturday (its 18:00–02:00 too): Sunday's lunch is the reopening, not Saturday's night.
        Shop.FirstOpeningAfter(new DateOnly(2026, 11, 7)).ShouldBe(new DateTimeOffset(2026, 11, 8, 11, 0, 0, Taipei));

        // Closed on Monday, a day off anyway.
        Shop.FirstOpeningAfter(new DateOnly(2026, 11, 2)).ShouldBe(new DateTimeOffset(2026, 11, 3, 11, 0, 0, Taipei));
    }

    [Fact]
    public void Consecutive_days_off_are_skipped_over()
    {
        var hours = Hours(
            "Asia/Taipei",
            (DayOfWeek.Thursday, "10:00", "20:00"),
            (DayOfWeek.Friday, "10:00", "20:00"));

        // Closed on Friday: Saturday to Wednesday are days off.
        hours.FirstOpeningAfter(new DateOnly(2026, 11, 6)).ShouldBe(new DateTimeOffset(2026, 11, 12, 10, 0, 0, Taipei));
        hours.NextOpeningAfter(new DateTimeOffset(2026, 11, 6, 21, 0, 0, Taipei)).ShouldBe(new DateTimeOffset(2026, 11, 12, 10, 0, 0, Taipei));
        // A shop open one day a week reopens a week later.
        var weekly = Hours("Asia/Taipei", (DayOfWeek.Wednesday, "09:00", "12:00"));
        weekly.FirstOpeningAfter(new DateOnly(2026, 11, 4)).ShouldBe(new DateTimeOffset(2026, 11, 11, 9, 0, 0, Taipei));
    }

    [Fact]
    public void The_next_opening_is_strictly_after_the_instant()
    {
        var tuesday = new DateOnly(2026, 11, 3);

        Shop.NextOpeningAfter(new DateTimeOffset(2026, 11, 3, 9, 0, 0, Taipei)).ShouldBe(new DateTimeOffset(2026, 11, 3, 11, 0, 0, Taipei));
        Shop.NextOpeningAfter(new DateTimeOffset(2026, 11, 3, 11, 0, 0, Taipei)).ShouldBe(new DateTimeOffset(2026, 11, 3, 17, 0, 0, Taipei));
        Shop.NextOpeningAfter(new DateTimeOffset(2026, 11, 3, 12, 30, 0, Taipei)).ShouldBe(new DateTimeOffset(2026, 11, 3, 17, 0, 0, Taipei));
        Shop.NextOpeningAfter(new DateTimeOffset(2026, 11, 3, 22, 0, 0, Taipei)).ShouldBe(new DateTimeOffset(2026, 11, 4, 11, 0, 0, Taipei));
        Shop.NextOpeningAfter(Shop.At(tuesday, new TimeOnly(23, 59))).ShouldBe(new DateTimeOffset(2026, 11, 4, 11, 0, 0, Taipei));

        // In UTC it is still Tuesday 03:30 — Tuesday 11:30 in Taipei: the dinner opening.
        Shop.NextOpeningAfter(new DateTimeOffset(2026, 11, 3, 3, 30, 0, TimeSpan.Zero)).ShouldBe(new DateTimeOffset(2026, 11, 3, 17, 0, 0, Taipei));
    }

    [Fact]
    public void The_next_opening_from_a_day_off_or_inside_an_overnight_period()
    {
        // Sunday 20:00, after Sunday's only period; Monday is off.
        Shop.NextOpeningAfter(new DateTimeOffset(2026, 11, 8, 20, 0, 0, Taipei)).ShouldBe(new DateTimeOffset(2026, 11, 10, 11, 0, 0, Taipei));

        // Monday 10:00, a day off.
        Shop.NextOpeningAfter(new DateTimeOffset(2026, 11, 9, 10, 0, 0, Taipei)).ShouldBe(new DateTimeOffset(2026, 11, 10, 11, 0, 0, Taipei));

        // Sunday 00:30, still inside Saturday's 18:00–02:00.
        Shop.NextOpeningAfter(new DateTimeOffset(2026, 11, 8, 0, 30, 0, Taipei)).ShouldBe(new DateTimeOffset(2026, 11, 8, 11, 0, 0, Taipei));
    }

    [Fact]
    public void A_week_without_periods_has_no_opening()
    {
        var closed = new BusinessHours(new WeeklyHours(new Dictionary<DayOfWeek, IReadOnlyList<BusinessPeriod>>()), TimeZoneInfo.Utc);

        closed.FirstOpeningAfter(new DateOnly(2026, 11, 2)).ShouldBeNull();
        closed.NextOpeningAfter(DateTimeOffset.UnixEpoch).ShouldBeNull();
        closed.FirstOpeningOn(new DateOnly(2026, 11, 2)).ShouldBeNull();
    }

    [Fact]
    public void The_date_at_an_instant_is_the_shops_local_date()
    {
        Shop.DateAt(new DateTimeOffset(2026, 11, 2, 16, 30, 0, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 11, 3));
        Shop.DateAt(new DateTimeOffset(2026, 11, 2, 15, 59, 0, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 11, 2));
    }

    // --- Daylight saving time (New York: 2026-03-08 02:00 → 03:00, 2026-11-01 02:00 → 01:00) ---------

    private static readonly TimeSpan Est = TimeSpan.FromHours(-5);
    private static readonly TimeSpan Edt = TimeSpan.FromHours(-4);

    [Fact]
    public void The_same_wall_clock_opening_moves_an_hour_in_UTC_across_the_spring_change()
    {
        var hours = Hours("America/New_York", (DayOfWeek.Sunday, "10:00", "14:00"));

        hours.FirstOpeningOn(new DateOnly(2026, 3, 1)).ShouldBe(new DateTimeOffset(2026, 3, 1, 10, 0, 0, Est));
        hours.FirstOpeningOn(new DateOnly(2026, 3, 8)).ShouldBe(new DateTimeOffset(2026, 3, 8, 10, 0, 0, Edt));
        hours.FirstOpeningOn(new DateOnly(2026, 3, 8))!.Value.UtcDateTime.Hour.ShouldBe(14);
        hours.FirstOpeningOn(new DateOnly(2026, 3, 8))!.Value.Offset.ShouldBe(Edt);
    }

    [Fact]
    public void A_skipped_opening_time_moves_forward_by_the_gap()
    {
        var hours = Hours("America/New_York", (DayOfWeek.Sunday, "02:30", "04:00"));

        var interval = hours.PeriodsOn(new DateOnly(2026, 3, 8)).ShouldHaveSingleItem();

        interval.Opens.ShouldBe(new DateTimeOffset(2026, 3, 8, 3, 30, 0, Edt));
        interval.Closes.ShouldBe(new DateTimeOffset(2026, 3, 8, 4, 0, 0, Edt));
        interval.Opens.Offset.ShouldBe(Edt);
    }

    [Fact]
    public void An_overnight_period_across_the_spring_change_is_an_hour_shorter()
    {
        var hours = Hours("America/New_York", (DayOfWeek.Saturday, "22:00", "03:00"));

        var interval = hours.PeriodsOn(new DateOnly(2026, 3, 7)).ShouldHaveSingleItem();

        interval.Opens.ShouldBe(new DateTimeOffset(2026, 3, 7, 22, 0, 0, Est));
        interval.Closes.ShouldBe(new DateTimeOffset(2026, 3, 8, 3, 0, 0, Edt));
        (interval.Closes - interval.Opens).ShouldBe(TimeSpan.FromHours(4));
    }

    [Fact]
    public void A_time_that_happens_twice_in_autumn_is_the_first_one()
    {
        var hours = Hours("America/New_York", (DayOfWeek.Sunday, "01:30", "03:00"));

        var interval = hours.PeriodsOn(new DateOnly(2026, 11, 1)).ShouldHaveSingleItem();

        interval.Opens.ShouldBe(new DateTimeOffset(2026, 11, 1, 1, 30, 0, Edt));
        interval.Closes.ShouldBe(new DateTimeOffset(2026, 11, 1, 3, 0, 0, Est));
        (interval.Closes - interval.Opens).ShouldBe(TimeSpan.FromHours(2.5));
    }

    [Fact]
    public void The_next_opening_after_a_closure_across_the_autumn_change()
    {
        var hours = Hours("America/New_York", (DayOfWeek.Saturday, "11:00", "20:00"), (DayOfWeek.Monday, "11:00", "20:00"));

        // Closed on Saturday 2026-10-31 (EDT): reopens Monday 2026-11-02, after the change (EST).
        hours.FirstOpeningAfter(new DateOnly(2026, 10, 31)).ShouldBe(new DateTimeOffset(2026, 11, 2, 11, 0, 0, Est));
        hours.NextOpeningAfter(new DateTimeOffset(2026, 10, 31, 21, 0, 0, Edt)).ShouldBe(new DateTimeOffset(2026, 11, 2, 11, 0, 0, Est));
    }

    private static BusinessHours Hours(string zone, params (DayOfWeek Day, string Open, string Close)[] periods) =>
        new(
            new WeeklyHours(periods
                .GroupBy(period => period.Day)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<BusinessPeriod>)[.. group.Select(period => new BusinessPeriod(TimeOnly.Parse(period.Open, System.Globalization.CultureInfo.InvariantCulture), TimeOnly.Parse(period.Close, System.Globalization.CultureInfo.InvariantCulture)))])),
            TimeZoneInfo.FindSystemTimeZoneById(zone));
}
