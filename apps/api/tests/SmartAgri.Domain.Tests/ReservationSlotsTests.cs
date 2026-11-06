using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// Which times a shop takes reservations for (store-reservations design R4, §3.1 point 3, #389): the weekly hours cut
/// every <c>slotMinutes</c>, the last seating before each closing, the lead time, the days ahead, overnight periods,
/// daylight saving time, and the confirmed 店休 commands of a day — a 恢復營業 undoing one, unconfirmed ones ignored.
/// </summary>
public class ReservationSlotsTests
{
    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    /// <summary>Monday 2026-11-02, 09:00 in Taipei: the sample shop is closed today.</summary>
    private static readonly DateTimeOffset MondayMorning = new(2026, 11, 2, 9, 0, 0, Taipei);

    private static readonly DateOnly Tuesday = new(2026, 11, 3);

    private static readonly DateOnly Saturday = new(2026, 11, 7);

    /// <summary>The valid sample (Tuesday–Friday 11:00–14:00 and 17:00–21:00, Saturday 18:00–02:00, Sunday 11:00–14:00,
    /// closed Mondays) taking reservations with the defaults: every 30 minutes, the last 60 minutes before closing, 2 hours
    /// ahead, 30 days.</summary>
    private static ShopSettingsDocument Shop(Func<ShopReservationSettings, ShopReservationSettings>? change = null)
    {
        var reservations = new ShopReservationSettings(true, "wangji", "02-2345-6789");
        return ShopSettingsSamples.ValidDocument() with { Reservations = change?.Invoke(reservations) ?? reservations };
    }

    [Fact]
    public void Lunch_and_dinner_are_cut_every_30_minutes_until_an_hour_before_each_closing()
    {
        var slots = ReservationSlots.On(Shop(), Tuesday, MondayMorning, []);

        slots.Select(slot => slot.Label).ShouldBe(
        [
            "11:00", "11:30", "12:00", "12:30", "13:00",
            "17:00", "17:30", "18:00", "18:30", "19:00", "19:30", "20:00",
        ]);
        slots[0].ShouldBe(new ReservationSlot(Tuesday, new DateTimeOffset(2026, 11, 3, 11, 0, 0, Taipei), new DateTime(2026, 11, 3, 11, 0, 0)));
        slots[0].Value.ShouldBe("2026-11-03T11:00");
        slots.ShouldAllBe(slot => slot.Date == Tuesday && !slot.IsNextDay);
    }

    [Fact]
    public void Another_interval_and_last_seating_change_the_times()
    {
        var slots = ReservationSlots.On(Shop(settings => settings with { SlotMinutes = 60, LastSeatingMinutes = 0 }), Tuesday, MondayMorning, []);

        slots.Select(slot => slot.Label).ShouldBe(["11:00", "12:00", "13:00", "17:00", "18:00", "19:00", "20:00"]);
    }

    [Fact]
    public void Times_less_than_the_lead_time_ahead_are_not_offered()
    {
        var tuesdayMidMorning = new DateTimeOffset(2026, 11, 3, 10, 31, 0, Taipei);

        ReservationSlots.On(Shop(), Tuesday, tuesdayMidMorning, []).Select(slot => slot.Label).First().ShouldBe("13:00");
        ReservationSlots.On(Shop(), Tuesday, tuesdayMidMorning.AddMinutes(-1), []).Select(slot => slot.Label).First().ShouldBe("12:30");
        ReservationSlots.On(Shop(settings => settings with { MinLeadMinutes = 30 }), Tuesday, tuesdayMidMorning, [])
            .Select(slot => slot.Label).First().ShouldBe("11:30");
    }

    [Fact]
    public void An_overnight_period_belongs_to_the_day_it_opens_and_its_small_hours_say_so()
    {
        var saturday = ReservationSlots.On(Shop(), Saturday, MondayMorning, []);

        saturday.Select(slot => slot.Label).ShouldBe(
        [
            "18:00", "18:30", "19:00", "19:30", "20:00", "20:30", "21:00", "21:30", "22:00", "22:30", "23:00", "23:30",
            "00:00（隔天）", "00:30（隔天）", "01:00（隔天）",
        ]);
        var last = saturday[^1];
        last.ShouldBe(new ReservationSlot(Saturday, new DateTimeOffset(2026, 11, 8, 1, 0, 0, Taipei), new DateTime(2026, 11, 8, 1, 0, 0)));
        last.IsNextDay.ShouldBeTrue();
        last.Value.ShouldBe("2026-11-08T01:00");

        // Sunday's own times start at its lunch: the night before is Saturday's.
        ReservationSlots.On(Shop(), Saturday.AddDays(1), MondayMorning, []).Select(slot => slot.Label).First().ShouldBe("11:00");
    }

    [Fact]
    public void A_night_closing_at_01_00_offers_midnight_as_its_last_time()
    {
        var shop = Shop() with
        {
            WeeklyHours = Week((DayOfWeek.Saturday, "17:00", "01:00")),
        };

        ReservationSlots.On(shop, Saturday, MondayMorning, []).Select(slot => slot.Label).TakeLast(2).ShouldBe(["23:30", "00:00（隔天）"]);
    }

    [Fact]
    public void Shortly_after_midnight_last_nights_period_can_still_be_booked()
    {
        var shop = Shop(settings => settings with { MinLeadMinutes = 1, PendingExpiryMinutes = 0 }) with
        {
            WeeklyHours = Week((DayOfWeek.Saturday, "17:00", "03:00")),
        };
        var sundaySmallHours = new DateTimeOffset(2026, 11, 8, 0, 5, 0, Taipei);

        ReservationSlots.Dates(shop, sundaySmallHours, [])[0].ShouldBe(Saturday);
        ReservationSlots.On(shop, Saturday, sundaySmallHours, []).Select(slot => slot.Label)
            .ShouldBe(["00:30（隔天）", "01:00（隔天）", "01:30（隔天）", "02:00（隔天）"]);
    }

    [Fact]
    public void Dates_run_from_today_to_the_last_day_ahead_skipping_days_without_times()
    {
        var dates = ReservationSlots.Dates(Shop(), MondayMorning, []);

        dates[0].ShouldBe(Tuesday, "closed on Monday");
        dates[^1].ShouldBe(new DateOnly(2026, 12, 2), "today plus 30 days, a Wednesday");
        dates.ShouldNotContain(new DateOnly(2026, 11, 9), "the next Monday");
        dates.Count.ShouldBe(26, "31 days less five Mondays");
        ReservationSlots.On(Shop(), new DateOnly(2026, 12, 3), MondayMorning, []).ShouldBeEmpty("31 days ahead");
        ReservationSlots.On(Shop(), new DateOnly(2026, 10, 31), MondayMorning, []).ShouldBeEmpty("in the past");

        var sevenDays = ReservationSlots.Dates(Shop(settings => settings with { MaxDaysAhead = 7 }), MondayMorning, []);
        sevenDays[^1].ShouldBe(new DateOnly(2026, 11, 8));
    }

    [Fact]
    public void Today_after_the_last_time_is_not_offered()
    {
        var tuesdayEvening = new DateTimeOffset(2026, 11, 3, 18, 1, 0, Taipei);

        ReservationSlots.Dates(Shop(), tuesdayEvening, [])[0].ShouldBe(new DateOnly(2026, 11, 4));
        ReservationSlots.On(Shop(), Tuesday, tuesdayEvening, []).ShouldBeEmpty();
    }

    // --- Daylight saving time -------------------------------------------------------------------------

    [Fact]
    public void When_clocks_go_forward_the_skipped_times_are_the_ones_after_the_gap_offered_once()
    {
        // New York skips 02:00–03:00 on Sunday 2026-03-08.
        var shop = Shop(settings => settings with { LastSeatingMinutes = 60 }) with
        {
            TimeZoneId = "America/New_York",
            WeeklyHours = Week((DayOfWeek.Sunday, "01:00", "05:00")),
        };
        var sunday = new DateOnly(2026, 3, 8);
        var before = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(-5));

        var slots = ReservationSlots.On(shop, sunday, before, []);

        slots.Select(slot => slot.Label).ShouldBe(["01:00", "01:30", "03:00", "03:30", "04:00"]);
        slots.Select(slot => slot.At.UtcDateTime.Hour * 60 + slot.At.UtcDateTime.Minute).ShouldBe([360, 390, 420, 450, 480], "30 real minutes apart");
        slots[2].At.Offset.ShouldBe(TimeSpan.FromHours(-4));
    }

    [Fact]
    public void When_clocks_go_back_each_time_on_the_wall_is_offered_once()
    {
        // New York has 01:00–02:00 twice on Sunday 2026-11-01.
        var shop = Shop(settings => settings with { LastSeatingMinutes = 0 }) with
        {
            TimeZoneId = "America/New_York",
            WeeklyHours = Week((DayOfWeek.Sunday, "00:00", "03:00")),
        };
        var sunday = new DateOnly(2026, 11, 1);
        var before = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(-4));

        var slots = ReservationSlots.On(shop, sunday, before, []);

        slots.Select(slot => slot.Label).ShouldBe(["00:00", "00:30", "01:00", "01:30", "02:00", "02:30"]);
        slots[2].At.Offset.ShouldBe(TimeSpan.FromHours(-4), "01:00 is the first one");
        slots[4].At.Offset.ShouldBe(TimeSpan.FromHours(-5));
        slots.Select(slot => slot.Value).Distinct().Count().ShouldBe(6);
    }

    // --- 店休 commands --------------------------------------------------------------------------------

    [Fact]
    public void A_confirmed_temporary_closure_takes_the_whole_day()
    {
        var closure = Command(ShopScenario.TemporaryClosure, Tuesday);

        ReservationSlots.On(Shop(), Tuesday, MondayMorning, [closure]).ShouldBeEmpty();
        ReservationSlots.Dates(Shop(), MondayMorning, [closure])[0].ShouldBe(new DateOnly(2026, 11, 4));
        ReservationSlots.Find(Shop(), Tuesday, "2026-11-03T18:00", MondayMorning, [closure]).ShouldBeNull();
        ReservationSlots.On(Shop(), new DateOnly(2026, 11, 4), MondayMorning, [closure]).Count.ShouldBe(12, "only that day");
    }

    [Fact]
    public void An_early_closing_ends_the_day_at_its_time_and_the_last_seating_counts_from_it()
    {
        var earlyClosing = Command(ShopScenario.CloseEarly, Tuesday, new TimeOnly(19, 0));

        ReservationSlots.On(Shop(), Tuesday, MondayMorning, [earlyClosing]).Select(slot => slot.Label)
            .ShouldBe(["11:00", "11:30", "12:00", "12:30", "13:00", "17:00", "17:30", "18:00"]);

        var beforeDinner = Command(ShopScenario.CloseEarly, Tuesday, new TimeOnly(15, 0));
        ReservationSlots.On(Shop(), Tuesday, MondayMorning, [beforeDinner]).Select(slot => slot.Label)
            .ShouldBe(["11:00", "11:30", "12:00", "12:30", "13:00"], "dinner opens after the new closing");
    }

    [Fact]
    public void A_late_opening_starts_the_day_at_its_time_keeping_the_usual_times()
    {
        var lateOpening = Command(ShopScenario.OpenLate, Tuesday, new TimeOnly(12, 10));

        ReservationSlots.On(Shop(), Tuesday, MondayMorning, [lateOpening]).Select(slot => slot.Label)
            .ShouldBe(["12:30", "13:00", "17:00", "17:30", "18:00", "18:30", "19:00", "19:30", "20:00"]);

        var afterLunch = Command(ShopScenario.OpenLate, Tuesday, new TimeOnly(18, 0));
        ReservationSlots.On(Shop(), Tuesday, MondayMorning, [afterLunch]).Select(slot => slot.Label)
            .ShouldBe(["18:00", "18:30", "19:00", "19:30", "20:00"]);
    }

    [Fact]
    public void An_early_closing_in_the_small_hours_of_an_overnight_period_is_on_the_next_day()
    {
        var earlyClosing = Command(ShopScenario.CloseEarly, Saturday, new TimeOnly(0, 30));

        ReservationSlots.On(Shop(), Saturday, MondayMorning, [earlyClosing]).Select(slot => slot.Label).TakeLast(2)
            .ShouldBe(["23:00", "23:30"]);
    }

    [Fact]
    public void A_confirmed_reopen_undoes_the_command_and_the_day_can_be_booked_again()
    {
        var closure = Command(ShopScenario.TemporaryClosure, Tuesday);
        var reopen = Command(ShopScenario.Reopen, Tuesday) with { ReopensCommandId = closure.Id };

        ReservationSlots.On(Shop(), Tuesday, MondayMorning, [closure, reopen]).Count.ShouldBe(12);
        ReservationSlots.InEffect([closure, reopen]).ShouldBeEmpty();
    }

    [Fact]
    public void Commands_that_are_not_confirmed_change_nothing()
    {
        var previewed = Command(ShopScenario.TemporaryClosure, Tuesday) with { Status = CoordinatorCommandStatus.Previewed };
        var cancelled = Command(ShopScenario.CloseEarly, Tuesday, new TimeOnly(12, 0)) with { Status = CoordinatorCommandStatus.Cancelled };
        var awaiting = Command(ShopScenario.OpenLate, Tuesday, null) with { Status = CoordinatorCommandStatus.AwaitingDetails };
        var closure = Command(ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 4));
        var unconfirmedReopen = Command(ShopScenario.Reopen, new DateOnly(2026, 11, 4)) with
        {
            Status = CoordinatorCommandStatus.Previewed,
            ReopensCommandId = closure.Id,
        };

        ReservationSlots.On(Shop(), Tuesday, MondayMorning, [previewed, cancelled, awaiting]).Count.ShouldBe(12);
        ReservationSlots.On(Shop(), new DateOnly(2026, 11, 4), MondayMorning, [closure, unconfirmedReopen]).ShouldBeEmpty(
            "a reopen that is not confirmed undoes nothing");
    }

    [Fact]
    public void Find_takes_only_one_of_the_days_own_times()
    {
        var shop = Shop();

        ReservationSlots.Find(shop, Tuesday, "2026-11-03T18:30", MondayMorning, [])!.At.ShouldBe(new DateTimeOffset(2026, 11, 3, 18, 30, 0, Taipei));
        ReservationSlots.Find(shop, Tuesday, "2026-11-03T20:30", MondayMorning, []).ShouldBeNull("within the last hour");
        ReservationSlots.Find(shop, Tuesday, "2026-11-03T15:00", MondayMorning, []).ShouldBeNull("between lunch and dinner");
        ReservationSlots.Find(shop, Tuesday, "2026-11-03T18:15", MondayMorning, []).ShouldBeNull("not on the 30 minutes");
        ReservationSlots.Find(shop, new DateOnly(2026, 11, 4), "2026-11-03T18:30", MondayMorning, []).ShouldBeNull("another day's time");
        ReservationSlots.Find(shop, Tuesday, null, MondayMorning, []).ShouldBeNull();
    }

    [Fact]
    public void A_shop_without_reservation_settings_has_no_times_to_compute()
    {
        Should.Throw<ArgumentException>(() => ReservationSlots.On(ShopSettingsSamples.ValidDocument(), Tuesday, MondayMorning, []));
    }

    private static ShopCommandFact Command(ShopScenario scenario, DateOnly date, TimeOnly? time = null) =>
        new(Guid.NewGuid(), scenario, CoordinatorCommandStatus.Confirmed, date, time, null);

    private static WeeklyHours Week(params (DayOfWeek Day, string Open, string Close)[] periods) =>
        new(periods
            .GroupBy(period => period.Day)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<BusinessPeriod>)[.. group.Select(period => new BusinessPeriod(TimeOnly.Parse(period.Open), TimeOnly.Parse(period.Close)))]));
}
