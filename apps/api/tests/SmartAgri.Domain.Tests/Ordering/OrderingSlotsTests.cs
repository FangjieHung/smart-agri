using Shouldly;
using SmartAgri.Domain.Ordering;
using static SmartAgri.Domain.Tests.Ordering.OrderingSamples;

namespace SmartAgri.Domain.Tests.Ordering;

/// <summary>
/// When a takeout order can be picked up (online-ordering design D8, D9, Slice O1, #397): scheduled times every slot from
/// each period's opening to its closing inclusive, after the minimum lead time, over the days ahead — with periods past
/// midnight, a daylight-saving zone, full times and a paused shop — and whether ASAP is offered at the edge of its band;
/// and when a category with an availability window is shown.
/// </summary>
public class OrderingSlotsTests
{
    [Fact]
    public void Each_period_is_cut_every_slot_from_its_opening_to_its_closing_inclusive_over_the_days_ahead()
    {
        var shop = Shop(Week());

        var dates = OrderingSlots.Scheduled(shop, AtTaipei(11, 10, 9));

        dates.Select(date => date.Date).ShouldBe([new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12)]);
        var tuesday = dates[0].Slots;
        tuesday.Count.ShouldBe(7 + 9);
        tuesday[0].At.ShouldBe(AtTaipei(11, 10, 11));
        tuesday[6].Value.ShouldBe("2026-11-10T14:00");
        tuesday[7].Value.ShouldBe("2026-11-10T17:00");
        tuesday[^1].Value.ShouldBe("2026-11-10T21:00");
        tuesday.ShouldAllBe(slot => !slot.IsFull && !slot.IsNextDay);
        OrderingSlots.On(shop, new DateOnly(2026, 11, 13), AtTaipei(11, 10, 9)).ShouldBeEmpty();
        OrderingSlots.On(shop, new DateOnly(2026, 11, 9), AtTaipei(11, 10, 9)).ShouldBeEmpty();
    }

    [Fact]
    public void Todays_times_start_after_the_minimum_lead_time()
    {
        var shop = Shop(Week(), minLeadMinutes: 20);

        var today = OrderingSlots.On(shop, new DateOnly(2026, 11, 10), AtTaipei(11, 10, 12, 10));

        today.Select(slot => slot.Value[11..]).ShouldBe(
            ["12:30", "13:00", "13:30", "14:00", "17:00", "17:30", "18:00", "18:30", "19:00", "19:30", "20:00", "20:30", "21:00"]);
        OrderingSlots.On(shop, new DateOnly(2026, 11, 10), AtTaipei(11, 10, 12, 11))[0].Value.ShouldEndWith("T13:00");
        OrderingSlots.On(shop, new DateOnly(2026, 11, 10), AtTaipei(11, 10, 20, 41)).ShouldBeEmpty();
    }

    [Fact]
    public void A_period_past_midnight_belongs_to_the_day_it_opens_and_its_small_hours_stay_choosable_after_midnight()
    {
        var shop = Shop(Week());

        var saturday = OrderingSlots.On(shop, new DateOnly(2026, 11, 14), AtTaipei(11, 14, 9));
        saturday.Count.ShouldBe(17);
        saturday[^1].Local.ShouldBe(new DateTime(2026, 11, 15, 1, 0, 0));
        saturday[^1].IsNextDay.ShouldBeTrue();
        saturday[^1].Date.ShouldBe(new DateOnly(2026, 11, 14));

        var afterMidnight = OrderingSlots.Scheduled(shop, AtTaipei(11, 15, 0, 20));
        afterMidnight.Select(date => date.Date).ShouldBe([new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 17)]);
        afterMidnight[0].Slots.Select(slot => slot.Value).ShouldBe(["2026-11-15T00:30", "2026-11-15T01:00"]);
    }

    [Fact]
    public void A_paused_shop_or_one_without_scheduled_takeout_offers_no_times()
    {
        var paused = Shop(Week());
        paused.SetPaused(true, AtTaipei(11, 10, 8)).ShouldBeTrue();

        OrderingSlots.Scheduled(paused, AtTaipei(11, 10, 9)).ShouldBeEmpty();
        OrderingSlots.Asap(paused, AtTaipei(11, 10, 12)).ShouldBeNull();
        OrderingSlots.Scheduled(Shop(Week(), scheduled: false), AtTaipei(11, 10, 9)).ShouldBeEmpty();
        OrderingSlots.Asap(Shop(Week(), scheduled: false), AtTaipei(11, 10, 12)).ShouldNotBeNull();
    }

    [Fact]
    public void A_time_with_the_most_orders_is_full_and_cannot_be_chosen()
    {
        var shop = Shop([(DayOfWeek.Tuesday, "11:00", "12:00")], maxOrdersPerSlot: 8, daysAhead: 1);
        var full = AtTaipei(11, 10, 11, 30);
        int Orders(DateTimeOffset at) => at == full ? 8 : 7;

        var slots = OrderingSlots.On(shop, new DateOnly(2026, 11, 10), AtTaipei(11, 10, 9), Orders);

        slots.Select(slot => (slot.Value[11..], slot.IsFull)).ShouldBe([("11:00", false), ("11:30", true), ("12:00", false)]);
        OrderingSlots.Find(shop, new DateOnly(2026, 11, 10), "2026-11-10T11:30", AtTaipei(11, 10, 9), Orders).ShouldBeNull();
        OrderingSlots.Find(shop, new DateOnly(2026, 11, 10), "2026-11-10T12:00", AtTaipei(11, 10, 9), Orders).ShouldNotBeNull().At
            .ShouldBe(AtTaipei(11, 10, 12));
        OrderingSlots.Scheduled(shop, AtTaipei(11, 10, 9), _ => 8).ShouldBeEmpty();
        OrderingSlots.Scheduled(Shop([(DayOfWeek.Tuesday, "11:00", "12:00")], daysAhead: 1), AtTaipei(11, 10, 9), _ => 99)
            .ShouldHaveSingleItem().Slots.ShouldAllBe(slot => !slot.IsFull);
    }

    [Fact]
    public void A_time_the_clock_skips_is_the_instant_after_the_gap_offered_once()
    {
        // New York skips 02:00–03:00 on Sunday 2026-03-08.
        var shop = Shop([(DayOfWeek.Sunday, "01:00", "04:00")], timeZone: "America/New_York");
        var now = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.FromHours(-5));

        var slots = OrderingSlots.On(shop, new DateOnly(2026, 3, 8), now);

        slots.Select(slot => slot.Value[11..]).ShouldBe(["01:00", "01:30", "03:00", "03:30", "04:00"]);
        slots[1].At.Offset.ShouldBe(TimeSpan.FromHours(-5));
        slots[2].At.Offset.ShouldBe(TimeSpan.FromHours(-4));
        (slots[2].At - slots[1].At).ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Asap_is_offered_inside_a_period_while_the_bands_upper_bound_still_ends_by_the_closing()
    {
        var shop = Shop(Week());

        OrderingSlots.Asap(shop, AtTaipei(11, 10, 13, 30)).ShouldBe(new AsapEstimate(15, 30));
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 13, 31)).ShouldBeNull();
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 10, 59)).ShouldBeNull();
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 11)).ShouldNotBeNull();
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 21)).ShouldBeNull();
        OrderingSlots.Asap(shop, AtTaipei(11, 15, 0, 20)).ShouldBe(new AsapEstimate(15, 30));
        OrderingSlots.Asap(Shop(Week(), asap: false), AtTaipei(11, 10, 12)).ShouldBeNull();

        shop.SetAsapPrepBand(AsapPrepBand.Under15, AtTaipei(11, 10, 13)).ShouldBeTrue();
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 13, 45)).ShouldBe(new AsapEstimate(0, 15));
        shop.SetAsapPrepBand(AsapPrepBand.From45To60, AtTaipei(11, 10, 13)).ShouldBeTrue();
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 13)).ShouldBe(new AsapEstimate(45, 60));
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 13, 1)).ShouldBeNull();
    }

    [Fact]
    public void A_category_is_shown_for_pickup_times_inside_its_window()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        var lunch = OrderingSamples.Example().Menu.Categories.Single(category => category.Id == "lunch-special");
        var always = OrderingSamples.Example().Menu.Categories[0];

        lunch.IsVisibleAt(AtTaipei(11, 10, 12), zone).ShouldBeTrue();
        lunch.IsVisibleAt(AtTaipei(11, 10, 11), zone).ShouldBeTrue();
        lunch.IsVisibleAt(AtTaipei(11, 10, 14), zone).ShouldBeFalse();
        lunch.IsVisibleAt(AtTaipei(11, 9, 12), zone).ShouldBeFalse();
        lunch.IsVisibleAt(AtTaipei(11, 14, 12), zone).ShouldBeFalse();
        lunch.IsVisibleAt(new DateTimeOffset(2026, 11, 10, 4, 0, 0, TimeSpan.Zero), zone).ShouldBeTrue();
        always.IsVisibleAt(AtTaipei(11, 9, 3), zone).ShouldBeTrue();

        var lateNight = new MenuAvailability([DayOfWeek.Friday], new TimeOnly(22, 0), new TimeOnly(2, 0));
        lateNight.Includes(AtTaipei(11, 13, 23), zone).ShouldBeTrue();
        lateNight.Includes(AtTaipei(11, 14, 1, 59), zone).ShouldBeTrue();
        lateNight.Includes(AtTaipei(11, 14, 2), zone).ShouldBeFalse();
        lateNight.Includes(AtTaipei(11, 14, 23), zone).ShouldBeFalse();
        lateNight.Includes(AtTaipei(11, 13, 1), zone).ShouldBeFalse();

        var evenings = new MenuAvailability(null, new TimeOnly(17, 0), null);
        evenings.Includes(AtTaipei(11, 9, 18), zone).ShouldBeTrue();
        evenings.Includes(AtTaipei(11, 9, 16, 59), zone).ShouldBeFalse();
    }
}
