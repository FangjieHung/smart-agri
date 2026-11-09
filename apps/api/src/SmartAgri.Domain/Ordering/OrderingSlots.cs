using System.Globalization;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Ordering;

/// <summary>One pickup time a customer can choose for a scheduled takeout order.</summary>
/// <param name="Date">The business day: the date its ordering period opens on (Saturday's 01:00 of an 18:00–02:00 night is
/// Saturday's).</param>
/// <param name="At">The instant, at the shop's UTC offset then.</param>
/// <param name="Local">The shop's wall clock at <see cref="At"/>.</param>
/// <param name="IsFull">The time already has <see cref="OrderingShop.MaxOrdersPerSlot"/> orders: shown, but not
/// choosable.</param>
public sealed record OrderingSlot(DateOnly Date, DateTimeOffset At, DateTime Local, bool IsFull)
{
    /// <summary>The form's value: the wall clock, <c>2026-11-08T00:30</c> — unique within a business day.</summary>
    public string Value => Local.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    /// <summary>After midnight of the business day (the small hours of an overnight period).</summary>
    public bool IsNextDay => DateOnly.FromDateTime(Local) != Date;
}

/// <summary>A business day a customer can choose, with its pickup times.</summary>
public sealed record OrderingDate(DateOnly Date, IReadOnlyList<OrderingSlot> Slots);

/// <summary>「儘快（約 N–M 分鐘）」: an ASAP takeout order's estimate (design D9).</summary>
public sealed record AsapEstimate(int MinMinutes, int MaxMinutes);

/// <summary>
/// When a takeout order can be picked up or a delivery order delivered (online-ordering design D8–D10, Slice O1, #397,
/// #401) — pure, the instant given, so the ordering page and the server's check of a submitted order compute the same
/// thing.
/// </summary>
/// <remarks>
/// <para>
/// Scheduled (<see cref="OrderingShop.TakeoutScheduled"/>, <see cref="OrderingShop.Delivery"/>): each period of a business
/// day (<see cref="BusinessHours.PeriodsOn"/>, in the shop's time zone, an overnight period belonging to the day it opens) is
/// cut every <see cref="OrderingShop.SlotMinutes"/> by the wall clock from its opening up to and including its closing — an
/// 11:00–14:00 period offers 11:00 … 14:00; a time the clock skips (daylight saving) is the instant after the gap, and an
/// instant is offered once. A time must be at least the channel's lead time after now
/// (<see cref="OrderingShop.MinLeadMinutesFor"/>). The business days are today to today + <see cref="OrderingShop.DaysAhead"/>
/// − 1, plus yesterday while its overnight period still has times left. A time with
/// <see cref="OrderingShop.MaxOrdersPerSlot"/> orders of the same channel (counted by the caller) is full.
/// </para>
/// <para>The periods are the channel's: takeout's <see cref="OrderingShop.OrderingHours"/>, delivery's
/// <see cref="OrderingShop.DeliveryHours"/> (#401).</para>
/// <para>ASAP (<see cref="Asap"/>): while the shop is inside a period of the channel and the estimate's upper bound still ends
/// before the period closes; delivery adds <see cref="OrderingDeliverySettings.AsapExtraMinutes"/> to both bounds.</para>
/// <para>A paused shop (<see cref="OrderingShop.Paused"/>) offers neither.</para>
/// </remarks>
public static class OrderingSlots
{
    /// <summary>The business days a customer can choose at <paramref name="now"/>, earliest first: those with at least one
    /// time that is not full.</summary>
    /// <param name="ordersAt">How many orders of <paramref name="channel"/> a pickup instant already has; none when
    /// <see langword="null"/>.</param>
    public static IReadOnlyList<OrderingDate> Scheduled(
        OrderingShop shop, DateTimeOffset now, Func<DateTimeOffset, int>? ordersAt = null, OrderingChannel channel = OrderingChannel.Takeout)
    {
        ArgumentNullException.ThrowIfNull(shop);
        var hours = shop.HoursFor(channel);
        var today = hours.DateAt(now);
        var dates = new List<OrderingDate>();
        for (var date = today.AddDays(-1); date < today.AddDays(shop.DaysAhead); date = date.AddDays(1))
        {
            var slots = On(shop, hours, channel, date, now, ordersAt);
            if (slots.Exists(slot => !slot.IsFull))
            {
                dates.Add(new OrderingDate(date, slots));
            }
        }

        return dates;
    }

    /// <summary>The pickup times of business day <paramref name="date"/> at <paramref name="now"/>, earliest first; empty
    /// for a date outside the range, a channel the shop does not offer scheduled, or a paused shop.</summary>
    public static IReadOnlyList<OrderingSlot> On(
        OrderingShop shop,
        DateOnly date,
        DateTimeOffset now,
        Func<DateTimeOffset, int>? ordersAt = null,
        OrderingChannel channel = OrderingChannel.Takeout)
    {
        ArgumentNullException.ThrowIfNull(shop);
        return On(shop, shop.HoursFor(channel), channel, date, now, ordersAt);
    }

    /// <summary>The time of <paramref name="date"/> whose <see cref="OrderingSlot.Value"/> is <paramref name="value"/>, if it
    /// can be chosen at <paramref name="now"/> (offered and not full).</summary>
    public static OrderingSlot? Find(
        OrderingShop shop,
        DateOnly date,
        string? value,
        DateTimeOffset now,
        Func<DateTimeOffset, int>? ordersAt = null,
        OrderingChannel channel = OrderingChannel.Takeout) =>
        value is null ? null : On(shop, date, now, ordersAt, channel).FirstOrDefault(slot => slot.Value == value && !slot.IsFull);

    /// <summary>The ASAP estimate of <paramref name="channel"/> at <paramref name="now"/>; <see langword="null"/> when that
    /// ASAP service is off, the shop is paused or outside the channel's hours, or the order could not be ready (delivered)
    /// before the period closes.</summary>
    public static AsapEstimate? Asap(OrderingShop shop, DateTimeOffset now, OrderingChannel channel = OrderingChannel.Takeout)
    {
        ArgumentNullException.ThrowIfNull(shop);
        var offered = channel == OrderingChannel.Delivery ? shop.DeliveryOffered && shop.DeliveryAsap : shop.TakeoutAsap;
        if (!offered || shop.Paused)
        {
            return null;
        }

        var (lower, upper) = AsapPrepBands.Minutes(shop.AsapPrepBand);
        if (channel == OrderingChannel.Delivery)
        {
            var extra = shop.DeliverySettings!.AsapExtraMinutes;
            (lower, upper) = (lower + extra, upper + extra);
        }

        var hours = shop.HoursFor(channel);
        var today = hours.DateAt(now);
        foreach (var date in new[] { today.AddDays(-1), today })
        {
            foreach (var period in hours.PeriodsOn(date))
            {
                if (period.Opens <= now && now < period.Closes && now.AddMinutes(upper) <= period.Closes)
                {
                    return new AsapEstimate(lower, upper);
                }
            }
        }

        return null;
    }

    private static List<OrderingSlot> On(
        OrderingShop shop, BusinessHours hours, OrderingChannel channel, DateOnly date, DateTimeOffset now, Func<DateTimeOffset, int>? ordersAt)
    {
        var today = hours.DateAt(now);
        var offered = channel == OrderingChannel.Delivery ? shop.DeliveryOffered : shop.TakeoutScheduled;
        if (!offered || shop.Paused || date < today.AddDays(-1) || date >= today.AddDays(shop.DaysAhead))
        {
            return [];
        }

        var earliest = now.AddMinutes(shop.MinLeadMinutesFor(channel));
        var slots = new List<OrderingSlot>();
        foreach (var period in hours.PeriodsOn(date))
        {
            var wall = TimeZoneInfo.ConvertTime(period.Opens, hours.Zone).DateTime;
            for (var at = period.Opens;
                 at <= period.Closes;
                 wall = wall.AddMinutes(shop.SlotMinutes), at = hours.At(DateOnly.FromDateTime(wall), TimeOnly.FromDateTime(wall)))
            {
                if (at >= earliest && !slots.Exists(slot => slot.At == at))
                {
                    var full = shop.MaxOrdersPerSlot is { } max && ordersAt is not null && ordersAt(at) >= max;
                    slots.Add(new OrderingSlot(date, at, TimeZoneInfo.ConvertTime(at, hours.Zone).DateTime, full));
                }
            }
        }

        return [.. slots.OrderBy(slot => slot.At)];
    }
}
