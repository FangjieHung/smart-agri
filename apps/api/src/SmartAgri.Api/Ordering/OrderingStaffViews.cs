using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Api.Ordering;

// The shapes of the admin's online-ordering API (#399, OrderingStaffEndpoints). Every property is always sent (null when it
// has no value), so the generated TypeScript types match the wire. These carry the customer's full contact details: never
// log them.

/// <summary><c>GET /api/v1/ordering/summary</c>: what the side-nav badge and the orders page poll.</summary>
/// <param name="Now">The server's clock: compare times with it, not the device's.</param>
/// <param name="BusinessDate">The shop's current business day (<c>yyyy-MM-dd</c>): what 「今日完成」 lists.</param>
/// <param name="PendingCount">待確認 orders (any pickup day).</param>
/// <param name="ActiveCount">處理中 orders: accepted and ready (any pickup day).</param>
/// <param name="LatestOrderAt">When the newest order was sent; <see langword="null"/> before the first. A newer value than
/// the one the page saw last means a new order arrived (play the sound).</param>
/// <param name="ChangedAt">When any order last changed status (sent, answered, cancelled by its customer …); a newer value
/// than the one the page saw last means its lists are stale.</param>
/// <param name="ShopVersion">Goes up with every change of the shop's switches and item states (and imports).</param>
/// <param name="Asap">The current ASAP estimate for <see cref="AsapBand"/>, whether or not ASAP is possible right now.</param>
public sealed record OrderingStaffSummaryView(
    DateTimeOffset Now,
    string BusinessDate,
    string TimeZone,
    string ShopName,
    string Slug,
    int PendingCount,
    int ActiveCount,
    DateTimeOffset? LatestOrderAt,
    DateTimeOffset? ChangedAt,
    bool Paused,
    AsapPrepBand AsapBand,
    OrderingAsapView Asap,
    OrderingServicesView Services,
    int MenuVersion,
    int ShopVersion)
{
    public static OrderingStaffSummaryView Of(OrderingStaffSummary summary, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var shop = summary.Shop;
        var (lower, upper) = AsapPrepBands.Minutes(shop.AsapPrepBand);
        return new OrderingStaffSummaryView(
            now,
            OrderingPublicViews.Date(summary.BusinessDate),
            shop.TimeZoneId,
            shop.Name,
            shop.Slug,
            summary.PendingCount,
            summary.ActiveCount,
            summary.LatestOrderAt,
            summary.ChangedAt,
            shop.Paused,
            shop.AsapPrepBand,
            new OrderingAsapView(lower, upper),
            OrderingServicesView.Of(shop),
            summary.MenuVersion,
            shop.Version);
    }
}

/// <summary><c>GET /api/v1/ordering/orders?view=…</c>.</summary>
/// <param name="View"><c>pending</c>, <c>active</c> or <c>done</c>, as asked.</param>
/// <param name="Orders">At most 500 (<see cref="OrderingStaffOrders.MaxListed"/>).</param>
public sealed record OrderingStaffOrdersView(DateTimeOffset Now, string View, IReadOnlyList<OrderingStaffOrderView> Orders);

/// <summary>A member of the staff who did something to an order; <see langword="null"/> where the account is gone.</summary>
public sealed record OrderingStaffMemberView(Guid Id, string DisplayName);

/// <summary>A step (accepted, ready, completed): when and by whom.</summary>
public sealed record OrderingStaffStepView(DateTimeOffset At, OrderingStaffMemberView? By);

/// <summary>Declined: when, by whom, the reason and the note to the customer.</summary>
public sealed record OrderingStaffDeclineView(DateTimeOffset At, OrderingStaffMemberView? By, OrderingDeclineReason Reason, string? Note);

/// <summary>Cancelled: when and by whom — the customer (pending only; no member, reason or note) or the shop.</summary>
public sealed record OrderingStaffCancellationView(
    DateTimeOffset At, OrderingCanceller By, OrderingStaffMemberView? Member, OrderingCancelReason? Reason, string? Note);

/// <summary>An order as the staff see it: everything to make and hand it over, and to call the customer.</summary>
/// <param name="Version">Send it with an action: the action is refused (<c>409 order-changed</c>) when the order changed since.</param>
/// <param name="PickupAt">What the customer asked for. Scheduled: the chosen time. ASAP: the end of the estimate when it was
/// sent.</param>
/// <param name="PromisedAt">The time the shop gave when accepting (<see langword="null"/> before).</param>
/// <param name="CustomerName">「（已刪除）」 once the personal data was cleared (<see cref="PersonalDataClearedAt"/>).</param>
/// <param name="CustomerPhone">Digits only, for <c>tel:</c>; empty once cleared.</param>
/// <param name="CustomerPhoneDisplay">For people: <c>0912-345-678</c>; empty once cleared.</param>
/// <param name="ExpiresAt">When a pending order expires unanswered (#400): 「HH:mm 前未確認將自動取消」.</param>
/// <param name="PersonalDataClearedAt">When the customer's name, phone, Email and comments were cleared, 90 days after the
/// pickup (design D12, #400); <see langword="null"/> while kept.</param>
/// <param name="Actions">What the staff can do now: <c>accept</c>, <c>decline</c>, <c>ready</c>, <c>complete</c>,
/// <c>cancel</c>. For a delivery order <c>ready</c> is 〔已出發〕 (#401).</param>
/// <param name="DeliveryFee">運費 in <see cref="Total"/> (#401); 0 for takeout.</param>
/// <param name="Delivery">A delivery order's zone, fee, full address (empty once cleared) and note (#401);
/// <see langword="null"/> for takeout.</param>
public sealed record OrderingStaffOrderView(
    Guid Id,
    int Number,
    string DisplayNumber,
    OrderingOrderStatus Status,
    int Version,
    OrderingServiceType ServiceType,
    string BusinessDate,
    DateTimeOffset PickupAt,
    OrderingAsapView? Estimate,
    DateTimeOffset? PromisedAt,
    string CustomerName,
    string CustomerPhone,
    string CustomerPhoneDisplay,
    string? CustomerEmail,
    IReadOnlyList<OrderingOrderLine> Lines,
    string? OrderComment,
    bool? Tableware,
    int Subtotal,
    int Total,
    int MenuVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset StatusChangedAt,
    OrderingStaffStepView? Accepted,
    OrderingStaffStepView? Ready,
    OrderingStaffStepView? Completed,
    OrderingStaffDeclineView? Declined,
    OrderingStaffCancellationView? Cancelled,
    IReadOnlyList<OrderingOrderAction> Actions,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? PersonalDataClearedAt,
    int DeliveryFee,
    OrderingDeliveryView? Delivery)
{
    public static OrderingStaffOrderView Of(OrderingOrder order, IReadOnlyDictionary<Guid, string> members)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(members);
        OrderingStaffMemberView? Member(Guid? id) =>
            id is { } accountId && members.TryGetValue(accountId, out var name) ? new OrderingStaffMemberView(accountId, name) : null;
        OrderingStaffStepView? Step(DateTimeOffset? at, Guid? by) => at is { } when ? new OrderingStaffStepView(when, Member(by)) : null;

        return new OrderingStaffOrderView(
            order.Id,
            order.Number,
            OrderingOrder.FormatNumber(order.Number),
            order.Status,
            order.Version,
            order.ServiceType,
            OrderingPublicViews.Date(order.BusinessDate),
            order.PickupAt,
            order.AsapMinMinutes is { } min && order.AsapMaxMinutes is { } max ? new OrderingAsapView(min, max) : null,
            order.PromisedAt,
            OrderingPublicViews.CustomerName(order),
            order.CustomerPhone,
            order.CustomerPhone.Length == 0 ? string.Empty : ReservationContact.FormatPhone(order.CustomerPhone),
            order.CustomerEmail,
            order.Lines,
            order.OrderComment,
            order.Tableware,
            order.Subtotal,
            order.Total,
            order.MenuVersion,
            order.CreatedAt,
            order.StatusChangedAt,
            Step(order.AcceptedAt, order.AcceptedByAccountId),
            Step(order.ReadyAt, order.ReadyByAccountId),
            Step(order.CompletedAt, order.CompletedByAccountId),
            order is { DeclinedAt: { } declinedAt, DeclineReason: { } reason }
                ? new OrderingStaffDeclineView(declinedAt, Member(order.DeclinedByAccountId), reason, order.DeclineNote)
                : null,
            order is { CancelledAt: { } cancelledAt, CancelledBy: { } by }
                ? new OrderingStaffCancellationView(cancelledAt, by, Member(order.CancelledByAccountId), order.CancelReason, order.CancelNote)
                : null,
            order.AllowedActions,
            order.ExpiresAt,
            order.PersonalDataClearedAt,
            order.DeliveryFee,
            OrderingDeliveryView.Of(order));
    }
}

/// <summary><c>GET /api/v1/ordering/items</c>: the current menu's items with what the staff switched.</summary>
public sealed record OrderingStaffMenuView(DateTimeOffset Now, int MenuVersion, IReadOnlyList<OrderingStaffCategoryView> Categories);

public sealed record OrderingStaffCategoryView(string Id, string Name, IReadOnlyList<OrderingStaffItemView> Items);

/// <param name="Availability"><c>available</c>, <c>sold-out-today</c> (until <see cref="ResetsAt"/>) or <c>unavailable</c>.
/// A sold-out item whose time to come back has passed is <c>available</c>.</param>
/// <param name="ResetsAt">When a <c>sold-out-today</c> item is back by itself; otherwise <see langword="null"/>.</param>
public sealed record OrderingStaffItemView(string Id, string Name, int Price, OrderingItemAvailability Availability, DateTimeOffset? ResetsAt)
{
    public static OrderingStaffItemView Of(MenuItem item, IEnumerable<OrderingItemState> states, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        var state = states.FirstOrDefault(candidate => candidate.ItemId == item.Id && !candidate.IsAvailableAt(now));
        return new OrderingStaffItemView(
            item.Id,
            item.Name,
            item.Price,
            state?.Kind switch
            {
                OrderingItemStateKind.SoldOutToday => OrderingItemAvailability.SoldOutToday,
                OrderingItemStateKind.Unavailable => OrderingItemAvailability.Unavailable,
                _ => OrderingItemAvailability.Available,
            },
            state?.ResetsAt);
    }
}

/// <summary><c>POST …/orders/{id}/accept</c>.</summary>
/// <param name="Version">The order's version the member saw.</param>
/// <param name="PromisedAt">預計可取餐時間 when it differs from what the customer asked for: a scheduled order's not earlier
/// than its time, an ASAP order's not earlier than now; at most 24 hours later. Omitted or <see langword="null"/>: the
/// customer's time (an ASAP order's end of estimate, or now when that has passed).</param>
public sealed record OrderingAcceptRequest(int? Version, DateTimeOffset? PromisedAt);

/// <summary><c>POST …/orders/{id}/decline</c>.</summary>
/// <param name="Note">Shown to the customer; at most 100 characters, one line; required with <c>other</c>.</param>
public sealed record OrderingDeclineRequest(int? Version, OrderingDeclineReason? Reason, string? Note);

/// <summary><c>POST …/orders/{id}/ready</c> and <c>…/complete</c>.</summary>
public sealed record OrderingStepRequest(int? Version);

/// <summary><c>POST …/orders/{id}/cancel</c>: the shop cancels an accepted or ready order.</summary>
/// <param name="Note">Shown to the customer; at most 100 characters, one line; required with <c>other</c>.</param>
public sealed record OrderingShopCancelRequest(int? Version, OrderingCancelReason? Reason, string? Note);

/// <summary><c>PUT /api/v1/ordering/pause</c>.</summary>
public sealed record OrderingPauseRequest(bool? Paused);

/// <summary><c>PUT /api/v1/ordering/asap-band</c>: <c>under-15</c>, <c>15-30</c>, <c>30-45</c> or <c>45-60</c>.</summary>
public sealed record OrderingAsapBandRequest(AsapPrepBand? Band);

/// <summary><c>PUT /api/v1/ordering/items/{itemId}/state</c>.</summary>
public sealed record OrderingItemStateRequest(OrderingItemAvailability? State);

/// <summary>
/// <c>409</c> of an order action: RFC 9457 problem details with <see cref="Reason"/> <c>order-changed</c> (the order is
/// at another version than sent: someone else, or the customer, changed it first) or <c>action-not-allowed</c> (the version
/// is current but its status does not allow the action), and the order as it is now — show it instead.
/// </summary>
public sealed record OrderingStaffConflict(string Type, string Title, int Status, string Reason, string Message, OrderingStaffOrderView Order);
