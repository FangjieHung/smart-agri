using System.Globalization;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Api.Ordering;

// The shapes of the anonymous online-ordering API (#398, OrderingPublicEndpoints). Every property is always sent (null
// when it has no value), so the generated TypeScript types match the wire.

/// <summary><c>GET /api/v1/public/ordering/{slug}</c>: the shop, whether it takes orders now and how.</summary>
/// <param name="Now">The server's clock: what <see cref="Open"/>, <see cref="Asap"/> and the slots were computed at.</param>
/// <param name="TimeZone">The shop's IANA time zone; times of day are on its clock.</param>
/// <param name="Asap">「儘快（約 N–M 分鐘）」 when an ASAP order can be sent now; otherwise <see langword="null"/> (ASAP off,
/// paused, outside the ordering hours, or too close to closing).</param>
/// <param name="MenuVersion">The current menu version: send it with an order.</param>
/// <param name="Delivery">外送 (#401) when the shop offers it: zones, hours, whether 外送（儘快） can be sent now; otherwise
/// <see langword="null"/>.</param>
public sealed record OrderingShopInfoView(
    string Slug,
    string Name,
    string Phone,
    string? Address,
    OrderingNoticesView Notices,
    OrderingServicesView Services,
    bool Paused,
    string TimeZone,
    DateTimeOffset Now,
    OrderingOpenView Open,
    OrderingAsapView? Asap,
    OrderingSlotRulesView SlotRules,
    bool ShowOrderComment,
    bool ShowTableware,
    int MenuVersion,
    IReadOnlyList<OrderingDayHoursView> OrderingHours,
    OrderingLimitsView Limits,
    OrderingDeliveryInfoView? Delivery);

/// <summary>The shop's delivery (#401): no map, no distance — the customer picks a zone the shop named and types the address.</summary>
/// <param name="Note">外送範圍說明, shown with the zones.</param>
/// <param name="Open">By the delivery hours.</param>
/// <param name="Asap">「外送儘快（約 N–M 分鐘）」 when an ASAP delivery can be sent now; otherwise <see langword="null"/>.</param>
/// <param name="MinLeadMinutes">The earliest delivery slot is at least this long from now.</param>
/// <param name="OrderingHours">When orders are delivered (the takeout hours unless the shop set its own).</param>
/// <param name="AddressMaxLength">Longest address.</param>
/// <param name="NoteMaxLength">Longest 給外送的備註.</param>
public sealed record OrderingDeliveryInfoView(
    IReadOnlyList<OrderingZoneView> Zones,
    string? Note,
    OrderingOpenView Open,
    OrderingAsapView? Asap,
    int MinLeadMinutes,
    IReadOnlyList<OrderingDayHoursView> OrderingHours,
    int AddressMaxLength,
    int NoteMaxLength);

/// <summary>A delivery zone: its fee (added to the total) and the least subtotal (before the fee) it delivers.</summary>
public sealed record OrderingZoneView(string Id, string Name, int Fee, int MinimumSubtotal)
{
    public static OrderingZoneView Of(DeliveryZone zone) => new(zone.Id, zone.Name, zone.Fee, zone.MinimumSubtotal);
}

/// <param name="Reminder">提醒事項, shown above the menu.</param>
/// <param name="Checkout">結帳說明, shown at checkout.</param>
public sealed record OrderingNoticesView(string? Reminder, string? Checkout);

/// <summary>Which ways of getting an order the shop offers (whether they can be used now: <see cref="OrderingShopInfoView.Paused"/>,
/// <see cref="OrderingShopInfoView.Asap"/>, the slots).</summary>
/// <param name="Delivery">外送（預約）(#401).</param>
/// <param name="DeliveryAsap">外送（儘快）(#401).</param>
public sealed record OrderingServicesView(bool TakeoutScheduled, bool TakeoutAsap, bool Delivery, bool DeliveryAsap)
{
    public static OrderingServicesView Of(OrderingShop shop) =>
        new(shop.TakeoutScheduled, shop.TakeoutAsap, shop.DeliveryOffered, shop.DeliveryOffered && shop.DeliveryAsap);
}

/// <summary>「營業中」 or 「打烊中．明天 11:00 開始營業」 by the ordering hours.</summary>
/// <param name="IsOpen">Inside an ordering period now.</param>
/// <param name="CurrentPeriodOpens">That period's opening; <see langword="null"/> when closed.</param>
/// <param name="CurrentPeriodCloses">That period's closing; <see langword="null"/> when closed.</param>
/// <param name="NextOpening">The next period's opening after now; <see langword="null"/> only for hours without any period.</param>
public sealed record OrderingOpenView(
    bool IsOpen, DateTimeOffset? CurrentPeriodOpens, DateTimeOffset? CurrentPeriodCloses, DateTimeOffset? NextOpening);

/// <summary>An ASAP estimate in minutes from sending.</summary>
public sealed record OrderingAsapView(int MinMinutes, int MaxMinutes)
{
    public static OrderingAsapView Of(AsapEstimate estimate) => new(estimate.MinMinutes, estimate.MaxMinutes);
}

/// <param name="SlotMinutes">Between two pickup times.</param>
/// <param name="DaysAhead">Days with pickup times, today included.</param>
/// <param name="MinLeadMinutes">The earliest pickup time is at least this long from now.</param>
public sealed record OrderingSlotRulesView(int SlotMinutes, int DaysAhead, int MinLeadMinutes);

/// <param name="Day"><c>mon</c> … <c>sun</c>, Monday first.</param>
public sealed record OrderingDayHoursView(string Day, IReadOnlyList<OrderingPeriodView> Periods);

/// <summary>A period on the shop's clock, <c>HH:mm</c>; <see cref="Close"/> before <see cref="Open"/> runs past midnight.</summary>
public sealed record OrderingPeriodView(string Open, string Close);

/// <summary>What the server refuses beyond (the checkout form can say so first).</summary>
/// <param name="MaxTotal">The highest order total and line total (NT$).</param>
public sealed record OrderingLimitsView(
    int MaxLines, int MaxQuantity, int NameMaxLength, int LineCommentMaxLength, int OrderCommentMaxLength, int MaxTotal);

/// <summary><c>GET …/menu</c>: the current menu with what cannot be ordered.</summary>
public sealed record OrderingMenuView(int Version, IReadOnlyList<OrderingCategoryView> Categories);

/// <param name="Availability">When the category is offered, by pickup time; <see langword="null"/> = always. Hide it for a
/// pickup time outside; the server refuses its items then (<c>category-unavailable</c>).</param>
public sealed record OrderingCategoryView(
    string Id, string Name, string? Description, OrderingAvailabilityView? Availability, IReadOnlyList<OrderingItemView> Items);

/// <summary>A category's window on the shop's clock at pickup: on <see cref="Days"/> (all when <see langword="null"/>) from
/// <see cref="From"/> (inclusive; the day's start when <see langword="null"/>) to <see cref="To"/> (exclusive; the day's end
/// when <see langword="null"/>); <see cref="To"/> before <see cref="From"/> runs past midnight and belongs to the day it starts.</summary>
public sealed record OrderingAvailabilityView(IReadOnlyList<string>? Days, string? From, string? To);

/// <param name="State">Why it cannot be ordered, or <see langword="null"/> when it can. A <c>sold-out-today</c> item can be
/// ordered for a pickup at or after <see cref="OrderingItemStateView.ResetsAt"/>; an <c>unavailable</c> one not at all.</param>
public sealed record OrderingItemView(
    string Id,
    string Name,
    int Price,
    string? Description,
    string? ImageUrl,
    IReadOnlyList<OrderingGroupView> OptionGroups,
    OrderingItemStateView? State);

public sealed record OrderingItemStateView(OrderingItemStateKind Kind, DateTimeOffset? ResetsAt);

/// <summary>Choose <see cref="Min"/>–<see cref="Max"/> (counting each option's count); an option more than once only
/// when <see cref="Repeatable"/>.</summary>
public sealed record OrderingGroupView(string Id, string Name, int Min, int Max, bool Repeatable, IReadOnlyList<OrderingOptionView> Options);

/// <param name="Price">Added per count.</param>
/// <param name="OptionGroups">Chosen only with this option (the next level).</param>
public sealed record OrderingOptionView(string Id, string Name, int Price, IReadOnlyList<OrderingGroupView> OptionGroups);

/// <summary><c>GET …/slots</c>: the business days with scheduled pickup times (empty when scheduled takeout is off or the
/// shop is paused), and with delivery times (#401: the delivery hours and lead time, places counted apart; empty when the
/// shop does not deliver or is paused).</summary>
public sealed record OrderingSlotsView(DateTimeOffset Now, IReadOnlyList<OrderingDateView> Dates, IReadOnlyList<OrderingDateView> DeliveryDates);

/// <param name="Date">The business day, <c>yyyy-MM-dd</c>: send it as <c>pickupDate</c>.</param>
public sealed record OrderingDateView(string Date, IReadOnlyList<OrderingSlotView> Slots);

/// <param name="Value">Send it as <c>pickupSlot</c>: the wall clock, <c>yyyy-MM-ddTHH:mm</c>.</param>
/// <param name="Time"><c>HH:mm</c> on the shop's clock.</param>
/// <param name="IsNextDay">After midnight of the business day (「00:30（隔天）」).</param>
/// <param name="IsFull">Shown but not choosable.</param>
public sealed record OrderingSlotView(string Value, DateTimeOffset At, string Time, bool IsNextDay, bool IsFull);

/// <summary><c>POST …/orders</c>'s body. Never a price: the server prices every line on the current menu.</summary>
/// <param name="PickupDate">Scheduled only: <see cref="OrderingDateView.Date"/>.</param>
/// <param name="PickupSlot">Scheduled only: <see cref="OrderingSlotView.Value"/>.</param>
/// <param name="MenuVersion">The <see cref="OrderingMenuView.Version"/> the cart was built on.</param>
/// <param name="OrderComment">Kept only when the shop shows it (<see cref="OrderingShopInfoView.ShowOrderComment"/>).</param>
/// <param name="Tableware">Kept only when the shop shows it (<see cref="OrderingShopInfoView.ShowTableware"/>).</param>
/// <param name="SubmissionKey">A UUID the client makes once per checkout and sends again with every retry of it.</param>
/// <param name="DeliveryZoneId">Delivery only (#401): <see cref="OrderingZoneView.Id"/>. The fee is the server's.</param>
/// <param name="DeliveryAddress">Delivery only: required, one line, at most 120 characters.</param>
/// <param name="DeliveryNote">Delivery only: 給外送的備註, at most 100 characters.</param>
public sealed record OrderingOrderRequest(
    OrderingServiceType? ServiceType,
    string? PickupDate,
    string? PickupSlot,
    int? MenuVersion,
    IReadOnlyList<OrderingLineRequest>? Lines,
    OrderingCustomerRequest? Customer,
    string? OrderComment,
    bool? Tableware,
    string? SubmissionKey,
    string? DeliveryZoneId = null,
    string? DeliveryAddress = null,
    string? DeliveryNote = null);

/// <param name="Quantity">1–99.</param>
/// <param name="Selections">The chosen options, nested under the option they belong to.</param>
/// <param name="Comment">品項備註, at most 100 characters.</param>
public sealed record OrderingLineRequest(string? ItemId, int Quantity, IReadOnlyList<OrderingSelectionRequest>? Selections, string? Comment);

/// <param name="Count">How many of the option (more than 1 only in a repeatable group).</param>
/// <param name="Selections">Choices in this option's own groups.</param>
public sealed record OrderingSelectionRequest(string? GroupId, string? OptionId, int Count, IReadOnlyList<OrderingSelectionRequest>? Selections)
{
    public OptionSelection ToSelection() =>
        new(GroupId ?? string.Empty, OptionId ?? string.Empty, Count, Selections?.Select(selection => selection.ToSelection()).ToList());
}

/// <param name="Phone">A Taiwanese mobile or landline number in any common writing (<c>0912-345-678</c>).</param>
public sealed record OrderingCustomerRequest(string? Name, string? Phone, string? Email);

/// <summary><c>201</c> of <c>POST …/orders</c>.</summary>
/// <param name="Token">The status page's token, given only here: keep it. The status page is <c>/order/{slug}/o#&lt;token&gt;</c>
/// (in the fragment, never sent), and the API takes it in the <c>X-Ordering-Token</c> header.</param>
public sealed record OrderingSubmittedView(string Token, OrderingOrderView Order);

/// <summary>An order as its customer sees it: never the Email, and only the last three digits of the phone. Once its
/// personal data was cleared (90 days after the pickup, #400) the name is 「（已刪除）」, the phone digits empty, and the
/// comments <see langword="null"/>.</summary>
/// <param name="DisplayNumber">「#001」.</param>
/// <param name="BusinessDate"><c>yyyy-MM-dd</c>, the business day the number counts in.</param>
/// <param name="PickupAt">Scheduled: the chosen time. ASAP: the end of the estimate (sent + <see cref="OrderingAsapView.MaxMinutes"/>).</param>
/// <param name="Estimate">ASAP only: the estimate when it was sent.</param>
/// <param name="CanCancel">Only before the shop answers.</param>
/// <param name="PromisedAt">預計可取餐時間 the shop gave when accepting (#399): for a scheduled order its time or later,
/// for an ASAP order the time it will be ready; <see langword="null"/> until accepted (show <see cref="PickupAt"/> then).</param>
/// <param name="DeclineReason">Why the shop declined it (<c>declined</c> only).</param>
/// <param name="DeclineNote">The shop's words with <see cref="DeclineReason"/>, if any.</param>
/// <param name="CancelledBy"><c>customer</c> or <c>shop</c> (<c>cancelled</c> only).</param>
/// <param name="CancelReason">Why the shop cancelled it (cancelled by the shop only).</param>
/// <param name="CancelNote">The shop's words with <see cref="CancelReason"/>, if any.</param>
/// <param name="DeliveryFee">運費 in <see cref="Total"/> (#401); 0 for takeout.</param>
/// <param name="Delivery">A delivery order's zone and address (#401); <see langword="null"/> for takeout. Its <c>ready</c>
/// status means 已出發.</param>
public sealed record OrderingOrderView(
    int Number,
    string DisplayNumber,
    OrderingOrderStatus Status,
    OrderingServiceType ServiceType,
    string BusinessDate,
    DateTimeOffset PickupAt,
    OrderingAsapView? Estimate,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CancelledAt,
    IReadOnlyList<OrderingOrderLine> Lines,
    int Subtotal,
    int Total,
    int MenuVersion,
    string CustomerName,
    string PhoneLastDigits,
    string? OrderComment,
    bool? Tableware,
    bool CanCancel,
    DateTimeOffset? PromisedAt,
    OrderingDeclineReason? DeclineReason,
    string? DeclineNote,
    OrderingCanceller? CancelledBy,
    OrderingCancelReason? CancelReason,
    string? CancelNote,
    int DeliveryFee,
    OrderingDeliveryView? Delivery)
{
    public static OrderingOrderView Of(OrderingOrder order) => new(
        order.Number,
        OrderingOrder.FormatNumber(order.Number),
        order.Status,
        order.ServiceType,
        OrderingPublicViews.Date(order.BusinessDate),
        order.PickupAt,
        order.AsapMinMinutes is { } min && order.AsapMaxMinutes is { } max ? new OrderingAsapView(min, max) : null,
        order.CreatedAt,
        order.CancelledAt,
        order.Lines,
        order.Subtotal,
        order.Total,
        order.MenuVersion,
        OrderingPublicViews.CustomerName(order),
        order.CustomerPhone.Length <= 3 ? order.CustomerPhone : order.CustomerPhone[^3..],
        order.OrderComment,
        order.Tableware,
        order.CanCancel,
        order.PromisedAt,
        order.DeclineReason,
        order.DeclineNote,
        order.CancelledBy,
        order.CancelReason,
        order.CancelNote,
        order.DeliveryFee,
        OrderingDeliveryView.Of(order));
}

/// <summary>A delivery order's zone (as it was when sent), fee, address and note (#401). The address is empty and the note
/// <see langword="null"/> once the personal data was cleared.</summary>
public sealed record OrderingDeliveryView(string ZoneId, string ZoneName, int Fee, string Address, string? Note)
{
    public static OrderingDeliveryView? Of(OrderingOrder order) =>
        order.Delivery is { } delivery ? new(delivery.ZoneId, delivery.ZoneName, delivery.Fee, delivery.Address, delivery.Note) : null;
}

/// <summary>Every refusal of the ordering API: RFC 9457 problem details with a stable <see cref="Reason"/> to switch on and
/// a <see cref="Message"/> in Traditional Chinese to show as is; the other members are <see langword="null"/> unless the
/// reason says otherwise (OrderingPublicEndpoints lists them).</summary>
/// <param name="Errors">Messages by field (<c>customer.phone</c>, <c>lines[2].comment</c> …), for <c>invalid-order</c>.</param>
/// <param name="LineErrors">What is wrong with which line, for <c>invalid-order</c> and <c>items-unavailable</c>.</param>
/// <param name="MenuVersion">The current menu version, for <c>menu-changed</c>.</param>
/// <param name="OrderNumber">The order sent before with the same key, for <c>duplicate-submission</c> (「#003」).</param>
/// <param name="OrderStatus">The order's status now, for <c>not-cancellable</c>.</param>
/// <param name="MinimumSubtotal">The zone's minimum, for <c>below-minimum</c> (#401).</param>
/// <param name="Shortfall">How much more the subtotal needs, for <c>below-minimum</c> (#401).</param>
public sealed record OrderingProblem(
    string Type,
    string Title,
    int Status,
    string Reason,
    string Message,
    IReadOnlyDictionary<string, string[]>? Errors,
    IReadOnlyList<OrderingLineError>? LineErrors,
    int? MenuVersion,
    string? OrderNumber,
    OrderingOrderStatus? OrderStatus,
    int? MinimumSubtotal,
    int? Shortfall);

internal static class OrderingPublicViews
{
    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Time(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>What the views show as the customer's name once it was cleared (design D12, #400).</summary>
    public const string ClearedName = "（已刪除）";

    /// <summary>The customer's name, or <see cref="ClearedName"/> once the personal data was cleared.</summary>
    public static string CustomerName(OrderingOrder order) => order.PersonalDataClearedAt is null ? order.CustomerName : ClearedName;

    public static OrderingShopInfoView Shop(OrderingShop shop, int menuVersion, DateTimeOffset now)
    {
        var asap = OrderingSlots.Asap(shop, now);
        return new OrderingShopInfoView(
            shop.Slug,
            shop.Name,
            shop.Phone,
            shop.Address,
            new OrderingNoticesView(shop.ReminderNotice, shop.CheckoutNotice),
            OrderingServicesView.Of(shop),
            shop.Paused,
            shop.TimeZoneId,
            now,
            Open(shop.Hours, now),
            asap is null ? null : OrderingAsapView.Of(asap),
            new OrderingSlotRulesView(shop.SlotMinutes, shop.DaysAhead, shop.MinLeadMinutes),
            shop.ShowOrderComment,
            shop.ShowTableware,
            menuVersion,
            Week(shop.OrderingHours),
            new OrderingLimitsView(
                OrderingOrder.MaxLines, MenuLimits.MaxQuantity, OrderingContact.NameMaxLength, OrderingContact.LineCommentMaxLength,
                OrderingContact.OrderCommentMaxLength, OrderingOrder.MaxTotal),
            shop.DeliveryOffered ? Delivery(shop, now) : null);
    }

    private static OrderingDeliveryInfoView Delivery(OrderingShop shop, DateTimeOffset now)
    {
        var settings = shop.DeliverySettings!;
        var asap = OrderingSlots.Asap(shop, now, OrderingChannel.Delivery);
        return new OrderingDeliveryInfoView(
            [.. settings.Zones.Select(OrderingZoneView.Of)],
            settings.Note,
            Open(shop.HoursFor(OrderingChannel.Delivery), now),
            asap is null ? null : OrderingAsapView.Of(asap),
            settings.MinLeadMinutes,
            Week(shop.DeliveryHours),
            OrderingContact.AddressMaxLength,
            OrderingContact.DeliveryNoteMaxLength);
    }

    /// <summary>Open now by <paramref name="hours"/>, and the next opening.</summary>
    private static OrderingOpenView Open(BusinessHours hours, DateTimeOffset now)
    {
        var today = hours.DateAt(now);
        var current = new[] { today.AddDays(-1), today }
            .SelectMany(hours.PeriodsOn)
            .FirstOrDefault(period => period.Opens <= now && now < period.Closes);
        return new OrderingOpenView(current is not null, current?.Opens, current?.Closes, hours.NextOpeningAfter(now));
    }

    private static List<OrderingDayHoursView> Week(WeeklyHours hours) =>
    [
        .. WeeklyHours.Days.Select(day => new OrderingDayHoursView(
            WeeklyHours.WireName(day),
            [.. hours[day].Select(period => new OrderingPeriodView(Time(period.Open), Time(period.Close)))])),
    ];

    /// <summary>The menu, with the state of every item that cannot be ordered now or is sold out until later.</summary>
    public static OrderingMenuView Menu(OrderingMenu menu, IReadOnlyList<OrderingItemState> states, DateTimeOffset now) =>
        new(menu.Version, [.. menu.Document.Categories.Select(category => Category(category, states, now))]);

    public static OrderingSlotsView Slots(IReadOnlyList<OrderingDate> dates, IReadOnlyList<OrderingDate> deliveryDates, DateTimeOffset now) =>
        new(now, Dates(dates), Dates(deliveryDates));

    private static List<OrderingDateView> Dates(IReadOnlyList<OrderingDate> dates) =>
    [
        .. dates.Select(date => new OrderingDateView(
            Date(date.Date),
            [.. date.Slots.Select(slot => new OrderingSlotView(slot.Value, slot.At, Time(TimeOnly.FromDateTime(slot.Local)), slot.IsNextDay, slot.IsFull))])),
    ];

    private static OrderingCategoryView Category(MenuCategory category, IReadOnlyList<OrderingItemState> states, DateTimeOffset now) => new(
        category.Id,
        category.Name,
        category.Description,
        category.Availability is { } availability
            ? new OrderingAvailabilityView(
                availability.Days?.Select(WeeklyHours.WireName).ToList(),
                availability.From is { } from ? Time(from) : null,
                availability.To is { } to ? Time(to) : null)
            : null,
        [
            .. category.Items.Select(item => new OrderingItemView(
                item.Id,
                item.Name,
                item.Price,
                item.Description,
                item.ImageUrl,
                Groups(item.OptionGroups),
                State(states, item.Id, now))),
        ]);

    /// <summary>The item's state while it matters: 停售, or 今日售完 until it is back.</summary>
    private static OrderingItemStateView? State(IReadOnlyList<OrderingItemState> states, string itemId, DateTimeOffset now) =>
        states.FirstOrDefault(state => state.ItemId == itemId && !state.IsAvailableAt(now)) is { } state
            ? new OrderingItemStateView(state.Kind, state.ResetsAt)
            : null;

    private static List<OrderingGroupView> Groups(IReadOnlyList<OptionGroup> groups) =>
    [
        .. groups.Select(group => new OrderingGroupView(
            group.Id,
            group.Name,
            group.Min,
            group.Max,
            group.Repeatable,
            [.. group.Options.Select(option => new OrderingOptionView(option.Id, option.Name, option.Price, Groups(option.OptionGroups)))])),
    ];
}
