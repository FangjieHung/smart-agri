using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Ordering;

/// <summary>
/// 外送區域 (online-ordering design D10, Slice O5, #401): an area the shop delivers to, named by the shop in plain words
/// (「新營區・民權里」, 「3 公里內」) — no map, no distance. The customer chooses one and types the address; the server charges
/// its <see cref="Fee"/> and refuses an order below its <see cref="MinimumSubtotal"/>.
/// </summary>
/// <param name="Id">The import file's id (<see cref="MenuLimits.IsValidId"/>), unique within the shop: what an order names.</param>
/// <param name="Name">Shown to the customer and the staff, at most <see cref="OrderingDeliverySettings.ZoneNameMaxLength"/>.</param>
/// <param name="Fee">運費 in whole NT$, 0 to <see cref="OrderingDeliverySettings.MaxFee"/>.</param>
/// <param name="MinimumSubtotal">外送門檻: the least subtotal (before the fee), 0 to
/// <see cref="OrderingDeliverySettings.MaxMinimumSubtotal"/>.</param>
public sealed record DeliveryZone(string Id, string Name, int Fee, int MinimumSubtotal);

/// <summary>
/// The import file's <c>shop.delivery</c> section (design D10, #401): the zones, and when delivery orders can be delivered
/// — by default the takeout <see cref="OrderingShopSettings.OrderingHours"/>, with a longer lead time since delivery takes
/// longer. The slot rules (<see cref="OrderingShopSettings.SlotMinutes"/>, <see cref="OrderingShopSettings.DaysAhead"/>,
/// <see cref="OrderingShopSettings.MaxOrdersPerSlot"/>) are the takeout ones; a delivery slot's places are counted apart from
/// takeout's.
/// </summary>
/// <param name="OrderingHours">When orders are delivered (<see cref="WeeklyHours"/>' format); <see langword="null"/> = the
/// takeout ordering hours.</param>
/// <param name="MinLeadMinutes">How long after ordering the earliest delivery slot can be.</param>
/// <param name="AsapExtraMinutes">Added to both ends of the ASAP band (<see cref="AsapPrepBand"/>) for 外送（儘快）: the ride.</param>
/// <param name="Note">外送範圍說明, shown where the customer chooses a zone.</param>
public sealed record OrderingDeliverySettings(
    IReadOnlyList<DeliveryZone> Zones,
    WeeklyHours? OrderingHours = null,
    int MinLeadMinutes = OrderingDeliverySettings.DefaultMinLeadMinutes,
    int AsapExtraMinutes = OrderingDeliverySettings.DefaultAsapExtraMinutes,
    string? Note = null)
{
    public const int MaxZones = 50;

    public const int ZoneNameMaxLength = 40;

    /// <summary>Highest <see cref="DeliveryZone.Fee"/> (NT$).</summary>
    public const int MaxFee = 10_000;

    /// <summary>Highest <see cref="DeliveryZone.MinimumSubtotal"/> (NT$).</summary>
    public const int MaxMinimumSubtotal = 100_000;

    /// <summary>Default <see cref="MinLeadMinutes"/>: longer than takeout's 15.</summary>
    public const int DefaultMinLeadMinutes = 45;

    /// <summary>Default <see cref="AsapExtraMinutes"/>.</summary>
    public const int DefaultAsapExtraMinutes = 20;

    /// <summary>Longest <see cref="AsapExtraMinutes"/>.</summary>
    public const int MaxAsapExtraMinutes = 120;

    /// <summary>Longest <see cref="Note"/>.</summary>
    public const int NoteMaxLength = 300;

    /// <summary>The zone <paramref name="id"/>; <see langword="null"/> when there is none.</summary>
    public DeliveryZone? Zone(string? id) => id is null ? null : Zones.FirstOrDefault(zone => zone.Id == id);
}

/// <summary>What a delivery order stores of its zone and address (#401): the zone's id, name and fee as they were when it was
/// sent, and the customer's address and note — personal data, cleared with the rest after 90 days (design D12).</summary>
public sealed record OrderingOrderDelivery(string ZoneId, string ZoneName, int Fee, string Address, string? Note);

/// <summary>Takeout or delivery: whose hours, lead time and slot places a scheduled or ASAP order uses (#401).</summary>
public enum OrderingChannel
{
    Takeout,
    Delivery,
}

/// <summary>What each <see cref="OrderingServiceType"/> is.</summary>
public static class OrderingServiceTypes
{
    /// <summary>外送（預約）or 外送（儘快）.</summary>
    public static bool IsDelivery(this OrderingServiceType type) =>
        type is OrderingServiceType.DeliveryScheduled or OrderingServiceType.DeliveryAsap;

    /// <summary>儘快 (takeout or delivery): no slot, an estimate.</summary>
    public static bool IsAsap(this OrderingServiceType type) =>
        type is OrderingServiceType.TakeoutAsap or OrderingServiceType.DeliveryAsap;

    public static OrderingChannel Channel(this OrderingServiceType type) =>
        type.IsDelivery() ? OrderingChannel.Delivery : OrderingChannel.Takeout;
}
