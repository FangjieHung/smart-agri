using System.Text.Json.Serialization;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Ordering;

/// <summary>Which ways of getting an order a shop offers (design O2). Delivery (#401) and dine-in (#402) are added
/// beside these.</summary>
/// <param name="TakeoutScheduled">外帶預約: pick up at a chosen time slot (<see cref="OrderingSlots"/>).</param>
/// <param name="TakeoutAsap">外帶即時: as soon as possible (<see cref="OrderingSlots.Asap"/>).</param>
public sealed record OrderingServices(bool TakeoutScheduled, bool TakeoutAsap)
{
    /// <summary>At least one is offered.</summary>
    public bool Any => TakeoutScheduled || TakeoutAsap;
}

/// <summary>How long an ASAP takeout order takes to be ready (design D9), chosen by the staff: shown to the customer as
/// 「約 N–M 分鐘」.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AsapPrepBand>))]
public enum AsapPrepBand
{
    /// <summary>Under 15 minutes.</summary>
    [JsonStringEnumMemberName("under-15")]
    Under15,

    [JsonStringEnumMemberName("15-30")]
    From15To30,

    [JsonStringEnumMemberName("30-45")]
    From30To45,

    [JsonStringEnumMemberName("45-60")]
    From45To60,
}

/// <summary>The minutes of an <see cref="AsapPrepBand"/>.</summary>
public static class AsapPrepBands
{
    /// <summary>The band a new shop starts with.</summary>
    public const AsapPrepBand Default = AsapPrepBand.From15To30;

    /// <summary>The band's lower and upper bound, in minutes (<see cref="AsapPrepBand.Under15"/> is 0–15).</summary>
    public static (int Lower, int Upper) Minutes(AsapPrepBand band) => band switch
    {
        AsapPrepBand.Under15 => (0, 15),
        AsapPrepBand.From15To30 => (15, 30),
        AsapPrepBand.From30To45 => (30, 45),
        AsapPrepBand.From45To60 => (45, 60),
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, "Not an ASAP preparation band."),
    };
}

/// <summary>
/// A shop's online-ordering settings as the import file's <c>shop</c> section has them (online-ordering design D8, D9,
/// Slice O1, #397). The staff's own switches — <see cref="OrderingShop.Paused"/> and <see cref="OrderingShop.AsapPrepBand"/>
/// — are not in the file, so an import never undoes them.
/// </summary>
/// <param name="Slug">The ordering page's name in its URL, unique across all shops and apart from the reservation page's
/// (<see cref="ShopReservationSettings.IsValidSlug"/>).</param>
/// <param name="Phone">Shown on the ordering page (<see cref="ShopReservationSettings.IsValidPhone"/>).</param>
/// <param name="ReminderNotice">提醒事項, shown above the menu.</param>
/// <param name="CheckoutNotice">結帳說明, shown at checkout.</param>
/// <param name="OrderingHours">When orders can be picked up (<see cref="WeeklyHours"/>' format, in <see cref="TimeZoneId"/>):
/// may differ from the business hours.</param>
/// <param name="SlotMinutes">Between two pickup times, from each period's opening (one of
/// <see cref="ShopReservationSettings.SlotMinuteChoices"/>).</param>
/// <param name="DaysAhead">How many days pickup times are offered for, today included.</param>
/// <param name="MinLeadMinutes">How long after ordering the earliest pickup time can be.</param>
/// <param name="MaxOrdersPerSlot">The most orders one pickup time takes; <see langword="null"/> = no limit.</param>
/// <param name="ShowOrderComment">Whether checkout asks for an order comment.</param>
/// <param name="ShowTableware">Whether checkout asks whether tableware is needed.</param>
/// <param name="PendingTimeoutMinutes">How long an order may wait for the staff's answer, counted in ordering hours only
/// (<see cref="OrderingOrder.ExpiryFor"/>), before it expires on its own (design D5, #400).</param>
public sealed record OrderingShopSettings(
    string Slug,
    string Name,
    string TimeZoneId,
    string Phone,
    string? Address,
    string? ReminderNotice,
    string? CheckoutNotice,
    OrderingServices Services,
    WeeklyHours OrderingHours,
    int SlotMinutes = OrderingShopSettings.DefaultSlotMinutes,
    int DaysAhead = OrderingShopSettings.DefaultDaysAhead,
    int MinLeadMinutes = OrderingShopSettings.DefaultMinLeadMinutes,
    int? MaxOrdersPerSlot = null,
    bool ShowOrderComment = true,
    bool ShowTableware = true,
    int PendingTimeoutMinutes = OrderingShopSettings.DefaultPendingTimeoutMinutes)
{
    public const int DefaultSlotMinutes = 30;
    public const int DefaultDaysAhead = 3;
    public const int MaxDaysAhead = 14;
    public const int DefaultMinLeadMinutes = 15;

    /// <summary>Default <see cref="PendingTimeoutMinutes"/> (#400).</summary>
    public const int DefaultPendingTimeoutMinutes = 15;

    /// <summary>Shortest <see cref="PendingTimeoutMinutes"/>.</summary>
    public const int MinPendingTimeoutMinutes = 5;

    /// <summary>Longest <see cref="PendingTimeoutMinutes"/>: two hours.</summary>
    public const int MaxPendingTimeoutMinutes = 120;

    /// <summary>Longest <see cref="MinLeadMinutes"/>: a day.</summary>
    public const int MaxMinLeadMinutes = 24 * 60;

    /// <summary>Largest <see cref="MaxOrdersPerSlot"/>.</summary>
    public const int MaxMaxOrdersPerSlot = 999;

    /// <summary>Longest <see cref="Name"/>.</summary>
    public const int NameMaxLength = ShopSettings.StoreNameMaxLength;

    /// <summary>Longest <see cref="Address"/>.</summary>
    public const int AddressMaxLength = 100;

    /// <summary>Longest notice.</summary>
    public const int NoticeMaxLength = 500;

    /// <summary>The <c>shop</c> section's fields as <see cref="OrderingShopChange"/> names them.</summary>
    public static readonly IReadOnlyList<string> FieldNames =
    [
        "slug", "name", "timeZone", "phone", "address", "notices", "services", "orderingHours", "slotMinutes", "daysAhead",
        "minLeadMinutes", "maxOrdersPerSlot", "showOrderComment", "showTableware", "pendingTimeoutMinutes",
    ];

    /// <summary>The time zone of <see cref="TimeZoneId"/>.</summary>
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>The calculations over <see cref="OrderingHours"/> in <see cref="TimeZone"/>.</summary>
    public BusinessHours Hours => new(OrderingHours, TimeZone);
}

/// <summary>What an import changed: the <c>shop</c> section's fields (<see cref="OrderingShopSettings.FieldNames"/>) and
/// <c>menu</c> — names only, never content.</summary>
public static class OrderingShopChange
{
    /// <summary>The fields that differ between <paramref name="before"/> and <paramref name="after"/>.</summary>
    public static IReadOnlyList<string> Between(OrderingShopSettings before, OrderingShopSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changed = new List<string>();
        void Compare(string field, bool same)
        {
            if (!same)
            {
                changed.Add(field);
            }
        }

        Compare("slug", before.Slug == after.Slug);
        Compare("name", before.Name == after.Name);
        Compare("timeZone", before.TimeZoneId == after.TimeZoneId);
        Compare("phone", before.Phone == after.Phone);
        Compare("address", before.Address == after.Address);
        Compare("notices", before.ReminderNotice == after.ReminderNotice && before.CheckoutNotice == after.CheckoutNotice);
        Compare("services", before.Services == after.Services);
        Compare(
            "orderingHours",
            ShopSettingsJson.WriteWeeklyHours(before.OrderingHours) == ShopSettingsJson.WriteWeeklyHours(after.OrderingHours));
        Compare("slotMinutes", before.SlotMinutes == after.SlotMinutes);
        Compare("daysAhead", before.DaysAhead == after.DaysAhead);
        Compare("minLeadMinutes", before.MinLeadMinutes == after.MinLeadMinutes);
        Compare("maxOrdersPerSlot", before.MaxOrdersPerSlot == after.MaxOrdersPerSlot);
        Compare("showOrderComment", before.ShowOrderComment == after.ShowOrderComment);
        Compare("showTableware", before.ShowTableware == after.ShowTableware);
        Compare("pendingTimeoutMinutes", before.PendingTimeoutMinutes == after.PendingTimeoutMinutes);
        return changed;
    }
}
