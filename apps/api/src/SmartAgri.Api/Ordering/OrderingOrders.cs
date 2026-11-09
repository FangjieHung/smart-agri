using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Ordering;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Ordering;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Persistence;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Ordering;

/// <summary>An order to store: everything checked that does not need the shop's lock (<see cref="OrderingOrders.SubmitAsync"/>).</summary>
/// <param name="PickupDate">Scheduled: the slot's business day.</param>
/// <param name="PickupSlot">Scheduled: the slot's value.</param>
/// <param name="Phone">Normalized (digits only).</param>
/// <param name="SubmissionKey">Normalized (<see cref="OrderingOrder.NormalizeSubmissionKey"/>).</param>
/// <param name="DeliveryZoneId">Delivery (#401): the zone chosen; its fee and minimum are the shop's under the lock.</param>
/// <param name="DeliveryAddress">Delivery: trimmed, checked.</param>
/// <param name="DeliveryNote">Delivery: trimmed, checked; <see langword="null"/> when empty.</param>
/// <param name="TableToken">Dine-in (#402): the <c>X-Ordering-Table</c> header as sent; the table is the shop's under the lock.</param>
public sealed record OrderingSubmission(
    Guid OrganizationId,
    OrderingServiceType ServiceType,
    DateOnly? PickupDate,
    string? PickupSlot,
    int MenuVersion,
    IReadOnlyList<OrderingLineInput> Lines,
    string Name,
    string Phone,
    string? Email,
    string? OrderComment,
    bool? Tableware,
    string SubmissionKey,
    string? DeliveryZoneId = null,
    string? DeliveryAddress = null,
    string? DeliveryNote = null,
    string? TableToken = null);

/// <summary>How a submission went.</summary>
public enum OrderingSubmitOutcome
{
    /// <summary>Stored: <see cref="OrderingSubmitResult.Order"/> and <see cref="OrderingSubmitResult.Token"/>.</summary>
    Stored,

    /// <summary>The same key was sent before: <see cref="OrderingSubmitResult.Order"/> is that order (its token is not
    /// known any more).</summary>
    Duplicate,

    /// <summary>The shop paused taking orders.</summary>
    Paused,

    /// <summary>The service is off, or ASAP is not possible now.</summary>
    ServiceUnavailable,

    /// <summary>Delivery (#401): the shop has no zone of that id (any more).</summary>
    UnknownZone,

    /// <summary>The scheduled time is not offered (gone, never was).</summary>
    SlotUnavailable,

    /// <summary>The scheduled time has its most orders.</summary>
    SlotFull,

    /// <summary>The menu has another version now: <see cref="OrderingSubmitResult.MenuVersion"/>.</summary>
    MenuChanged,

    /// <summary>A line does not price on the menu: <see cref="OrderingSubmitResult.LineErrors"/>.</summary>
    InvalidLines,

    /// <summary>The total is above <see cref="OrderingOrder.MaxTotal"/>.</summary>
    TotalTooLarge,

    /// <summary>An item cannot be had at the pickup time: <see cref="OrderingSubmitResult.LineErrors"/>.</summary>
    ItemsUnavailable,

    /// <summary>Delivery (#401): the subtotal is below the zone's minimum; <see cref="OrderingSubmitResult.Zone"/> and
    /// <see cref="OrderingSubmitResult.Shortfall"/>.</summary>
    BelowMinimum,

    /// <summary>The phone already has <see cref="OrderingOrder.MaxPendingPerPhone"/> pending orders at the shop.</summary>
    TooManyPending,

    /// <summary>Dine-in (#402): no active table of the shop has that token, or the shop does not offer dine-in.</summary>
    TableNotFound,

    /// <summary>Dine-in (#402): the table already has <see cref="OrderingDineInSettings.MaxPendingPerTable"/> pending orders.</summary>
    TooManyPendingAtTable,
}

public sealed record OrderingSubmitResult(
    OrderingSubmitOutcome Outcome,
    OrderingOrder? Order = null,
    string? Token = null,
    int? MenuVersion = null,
    IReadOnlyList<OrderingLineError>? LineErrors = null,
    DeliveryZone? Zone = null,
    int? Shortfall = null);

/// <summary>How a cancellation went.</summary>
public enum OrderingCancelOutcome
{
    Cancelled,
    NotFound,

    /// <summary>Not pending any more (or changed at the same moment): <see cref="OrderingCancelResult.Order"/> as it is now.</summary>
    NotCancellable,
}

public sealed record OrderingCancelResult(OrderingCancelOutcome Outcome, OrderingOrder? Order);

/// <summary>How an expiry went (<see cref="OrderingOrders.ExpireAsync"/>).</summary>
public enum OrderingExpireOutcome
{
    /// <summary>Pending → expired; the customer's Email follows (<see cref="IOrderingOrderOutcomes"/>).</summary>
    Expired,

    /// <summary>The order is gone (its shop deleted).</summary>
    NotFound,

    /// <summary>Answered first — accepted, declined, cancelled — or a compare-and-set at the same moment won: nothing changed.</summary>
    Answered,

    /// <summary>Still pending but its <see cref="OrderingOrder.ExpiresAt"/> is later: nothing changed.</summary>
    NotDue,
}

public sealed record OrderingExpireResult(OrderingExpireOutcome Outcome, OrderingOrder? Order);

/// <summary>
/// A shop's orders (online-ordering Slice O2, #398), each operation in a context acting for that organization
/// (<see cref="FixedOrganizationContext"/>), under the usual filter and write guard.
/// </summary>
/// <remarks>
/// A submission is one transaction that first locks the shop's <c>OrderingShops</c> row (an update that changes nothing,
/// held until commit), so the submissions of one shop are checked and stored one at a time: the last place of a time slot,
/// the day's next order number and a phone's pending limit cannot be taken twice. Under the lock it reads the shop (paused,
/// services, slots), the current menu and the item states again, so what the customer saw a moment ago is checked anew.
/// </remarks>
public sealed class OrderingOrders
{
    private const string SubmissionKeyIndex = "IX_OrderingOrders_OrganizationId_SubmissionKey";

    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IOrderingOrderOutcomes _outcomes;

    public OrderingOrders(DbContextOptions<AppDbContext> options, IOrderingOrderOutcomes outcomes)
    {
        _options = options;
        _outcomes = outcomes;
    }

    /// <summary>The shop's item states (今日售完, 停售).</summary>
    public async Task<IReadOnlyList<OrderingItemState>> ItemStatesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.OrderingItemStates.AsNoTracking().ToListAsync(cancellationToken);
    }

    /// <summary>How many orders of <paramref name="channel"/> take a place at each scheduled instant from
    /// <paramref name="from"/> on (takeout and delivery are counted apart, #401).</summary>
    public async Task<Func<DateTimeOffset, int>> SlotCountsAsync(
        Guid organizationId, DateTimeOffset from, OrderingChannel channel, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await SlotCountsAsync(dbContext, from, channel, cancellationToken);
    }

    /// <summary>The order <paramref name="orderId"/> of the shop with the shop's settings (both <see langword="null"/> when
    /// gone): what a job reads when it runs.</summary>
    public async Task<(OrderingOrder? Order, OrderingShop? Shop)> FindWithShopAsync(Guid organizationId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var order = await dbContext.OrderingOrders.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
        var shop = order is null ? null : await dbContext.OrderingShops.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return (order, shop);
    }

    /// <summary>
    /// Expires <paramref name="orderId"/> when it is still pending at its <see cref="OrderingOrder.ExpiresAt"/> (#400): the
    /// same compare-and-set on <c>Version</c> as the staff's steps and the customer's cancellation, so of an expiry and a
    /// press at the same moment exactly one wins. The winner calls <see cref="IOrderingOrderOutcomes.Decided"/> in its
    /// transaction (the customer's Email).
    /// </summary>
    public async Task<OrderingExpireResult> ExpireAsync(Guid organizationId, Guid orderId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var order = await dbContext.OrderingOrders.SingleOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
        if (order is null)
        {
            return new OrderingExpireResult(OrderingExpireOutcome.NotFound, null);
        }

        if (order.Status != OrderingOrderStatus.Pending)
        {
            return new OrderingExpireResult(OrderingExpireOutcome.Answered, order);
        }

        if (now < order.ExpiresAt)
        {
            return new OrderingExpireResult(OrderingExpireOutcome.NotDue, order);
        }

        order.Expire(now);
        try
        {
            // The compare-and-set: UPDATE … WHERE Version = the version read.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A member or the customer answered after it was read.
            return new OrderingExpireResult(OrderingExpireOutcome.Answered, null);
        }

        _outcomes.Decided(dbContext, order, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OrderingExpireResult(OrderingExpireOutcome.Expired, order);
    }

    /// <summary>A new expiry job of <paramref name="order"/>, to run at its <see cref="OrderingOrder.ExpiresAt"/> (not
    /// before <paramref name="now"/>).</summary>
    public async Task ScheduleExpiryAsync(OrderingOrder order, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        await using var dbContext = ForOrganization(order.OrganizationId);
        dbContext.BackgroundJobs.Add(ExpiryJob(order, now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The expiry job of <paramref name="order"/> at its <see cref="OrderingOrder.ExpiresAt"/>, not before
    /// <paramref name="now"/>.</summary>
    private static BackgroundJob ExpiryJob(OrderingOrder order, DateTimeOffset now) =>
        BackgroundJob.Create(
            order.OrganizationId,
            OrderingExpireOrderJob.Kind,
            new OrderingExpireOrderJob(order.Id),
            now.ToUniversalTime(),
            runAfter: (order.ExpiresAt > now ? order.ExpiresAt : now).ToUniversalTime());

    /// <summary>The order whose status token is <paramref name="token"/>; <see langword="null"/> when the shop has none (or
    /// the token is not one).</summary>
    public async Task<OrderingOrder?> FindByTokenAsync(Guid organizationId, string? token, CancellationToken cancellationToken)
    {
        if (!OrderingOrder.IsStatusToken(token))
        {
            return null;
        }

        var hash = OrderingOrder.HashStatusToken(token!);
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.OrderingOrders.AsNoTracking().SingleOrDefaultAsync(order => order.StatusTokenHash == hash, cancellationToken);
    }

    /// <summary>Checks <paramref name="submission"/> against the shop as it is under the lock and stores it as the
    /// business day's next number (see the remarks).</summary>
    public async Task<OrderingSubmitResult> SubmitAsync(OrderingSubmission submission, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await using var dbContext = ForOrganization(submission.OrganizationId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.OrderingShops.ExecuteUpdateAsync(
            setters => setters.SetProperty(shop => shop.Version, shop => shop.Version), cancellationToken);

        if (await dbContext.OrderingOrders.AsNoTracking()
                .SingleOrDefaultAsync(order => order.SubmissionKey == submission.SubmissionKey, cancellationToken) is { } sentBefore)
        {
            return new OrderingSubmitResult(OrderingSubmitOutcome.Duplicate, sentBefore);
        }

        var shop = await dbContext.OrderingShops.AsNoTracking().SingleAsync(cancellationToken);
        var dineIn = submission.ServiceType == OrderingServiceType.DineIn;
        OrderingTable? table = null;
        if (dineIn && (table = await ActiveTableAsync(dbContext, shop, submission.TableToken, cancellationToken)) is null)
        {
            return new OrderingSubmitResult(OrderingSubmitOutcome.TableNotFound);
        }

        if (shop.Paused)
        {
            return new OrderingSubmitResult(OrderingSubmitOutcome.Paused);
        }

        var channel = submission.ServiceType.Channel();
        var hours = dineIn ? shop.DineInBusinessHours : shop.HoursFor(channel);
        DeliveryZone? zone = null;
        if (channel == OrderingChannel.Delivery)
        {
            if (!shop.DeliveryOffered || (submission.ServiceType == OrderingServiceType.DeliveryAsap && !shop.DeliveryAsap))
            {
                return new OrderingSubmitResult(OrderingSubmitOutcome.ServiceUnavailable);
            }

            // The zone as the shop has it now: its fee and minimum, never the client's.
            if ((zone = shop.DeliverySettings!.Zone(submission.DeliveryZoneId)) is null)
            {
                return new OrderingSubmitResult(OrderingSubmitOutcome.UnknownZone);
            }
        }

        DateOnly businessDate;
        DateTimeOffset pickupAt;
        AsapEstimate? asap = null;
        if (dineIn)
        {
            // No time chosen: inside the dine-in hours now, picked up (numbered, expired, retained) as of now.
            if (!OrderingDineIn.IsOpen(hours, now))
            {
                return new OrderingSubmitResult(OrderingSubmitOutcome.ServiceUnavailable);
            }

            businessDate = OrderingOrder.AsapBusinessDate(hours, now);
            pickupAt = now;
        }
        else if (submission.ServiceType.IsAsap())
        {
            if ((asap = OrderingSlots.Asap(shop, now, channel)) is null)
            {
                return new OrderingSubmitResult(OrderingSubmitOutcome.ServiceUnavailable);
            }

            businessDate = OrderingOrder.AsapBusinessDate(hours, now);
            pickupAt = now.AddMinutes(asap.MaxMinutes);
        }
        else
        {
            if (channel == OrderingChannel.Takeout && !shop.TakeoutScheduled)
            {
                return new OrderingSubmitResult(OrderingSubmitOutcome.ServiceUnavailable);
            }

            var counts = await SlotCountsAsync(dbContext, now, channel, cancellationToken);
            var slot = OrderingSlots.On(shop, submission.PickupDate!.Value, now, counts, channel)
                .FirstOrDefault(candidate => candidate.Value == submission.PickupSlot);
            if (slot is null)
            {
                return new OrderingSubmitResult(OrderingSubmitOutcome.SlotUnavailable);
            }

            if (slot.IsFull)
            {
                return new OrderingSubmitResult(OrderingSubmitOutcome.SlotFull);
            }

            businessDate = slot.Date;
            pickupAt = slot.At;
        }

        var menu = await dbContext.OrderingMenus.AsNoTracking().OrderByDescending(candidate => candidate.Version).FirstAsync(cancellationToken);
        if (menu.Version != submission.MenuVersion)
        {
            return new OrderingSubmitResult(OrderingSubmitOutcome.MenuChanged, MenuVersion: menu.Version);
        }

        var cart = OrderingCart.Price(menu.Document, submission.Lines);
        if (!cart.IsValid)
        {
            return new OrderingSubmitResult(OrderingSubmitOutcome.InvalidLines, LineErrors: cart.Errors);
        }

        if (cart.Subtotal + (zone?.Fee ?? 0) > OrderingOrder.MaxTotal)
        {
            return new OrderingSubmitResult(OrderingSubmitOutcome.TotalTooLarge);
        }

        var states = await dbContext.OrderingItemStates.AsNoTracking().ToListAsync(cancellationToken);
        var unobtainable = OrderingCart.Unobtainable(menu.Document, states, cart.Lines, pickupAt, shop.Hours.Zone);
        if (unobtainable.Count > 0)
        {
            return new OrderingSubmitResult(OrderingSubmitOutcome.ItemsUnavailable, LineErrors: unobtainable);
        }

        if (zone is not null && cart.Subtotal < zone.MinimumSubtotal)
        {
            return new OrderingSubmitResult(OrderingSubmitOutcome.BelowMinimum, Zone: zone, Shortfall: (int)(zone.MinimumSubtotal - cart.Subtotal));
        }

        if (table is not null)
        {
            // #402: a table's pending limit instead of a phone's (dine-in has no phone).
            var pendingAtTable = await dbContext.OrderingOrders.CountAsync(
                order => order.TableId == table.TableId && order.Status == OrderingOrderStatus.Pending, cancellationToken);
            if (pendingAtTable >= shop.DineInSettings!.MaxPendingPerTable)
            {
                return new OrderingSubmitResult(OrderingSubmitOutcome.TooManyPendingAtTable);
            }
        }
        else
        {
            var pending = await dbContext.OrderingOrders.CountAsync(
                order => order.CustomerPhone == submission.Phone && order.Status == OrderingOrderStatus.Pending && order.PickupAt >= now,
                cancellationToken);
            if (pending >= OrderingOrder.MaxPendingPerPhone)
            {
                return new OrderingSubmitResult(OrderingSubmitOutcome.TooManyPending);
            }
        }

        // #402: the table's open session, or a new one; under the shop's lock, as a close is, so the order never joins a session
        // that is being closed.
        OrderingTableSession? session = null;
        if (table is not null)
        {
            session = await dbContext.OrderingTableSessions.SingleOrDefaultAsync(
                candidate => candidate.TableId == table.TableId && candidate.ClosedAt == null, cancellationToken);
            if (session is null)
            {
                session = OrderingTableSession.Open(table, now);
                dbContext.OrderingTableSessions.Add(session);
            }
            else
            {
                session.Join(now);
            }
        }

        var number = (await dbContext.OrderingOrders
            .Where(order => order.BusinessDate == businessDate)
            .MaxAsync(order => (int?)order.Number, cancellationToken) ?? 0) + 1;
        var token = OrderingOrder.NewStatusToken();
        var stored = OrderingOrder.Submit(
            submission.OrganizationId, submission.ServiceType, businessDate, number, pickupAt, asap, submission.Name, submission.Phone,
            submission.Email, submission.OrderComment, submission.Tableware, cart.Lines, menu.Version, token, submission.SubmissionKey,
            hours, shop.PendingTimeoutMinutes, now,
            zone is null ? null : new OrderingOrderDelivery(zone.Id, zone.Name, zone.Fee, submission.DeliveryAddress!, submission.DeliveryNote),
            table is null ? null : new OrderingOrderTable(table.TableId, table.Name, session!.Id));
        var autoAccepted = table is not null && shop.DineInSettings!.AutoAccept;
        if (autoAccepted)
        {
            // #402: 「自動接單」 in the same transaction; no expiry then.
            stored.AcceptAutomatically(now);
        }

        dbContext.OrderingOrders.Add(stored);
        if (!autoAccepted)
        {
            // #400: nobody answering it by ExpiresAt expires it (OrderingExpireOrderJob), stored with the order.
            dbContext.BackgroundJobs.Add(ExpiryJob(stored, now));
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, SubmissionKeyIndex))
        {
            // Cannot happen under the lock (the key is checked above); kept so a surprise is still one order.
            await transaction.RollbackAsync(cancellationToken);
            await using var again = ForOrganization(submission.OrganizationId);
            var first = await again.OrderingOrders.AsNoTracking()
                .SingleAsync(order => order.SubmissionKey == submission.SubmissionKey, cancellationToken);
            return new OrderingSubmitResult(OrderingSubmitOutcome.Duplicate, first);
        }

        if (autoAccepted)
        {
            // The outcomes seam as for a member's 〔接單〕 (a dine-in order has no Email, so nothing is sent today).
            _outcomes.Decided(dbContext, stored, now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new OrderingSubmitResult(OrderingSubmitOutcome.Stored, stored, token);
    }

    /// <summary>The active table of <paramref name="token"/> when the shop offers dine-in (#402); <see langword="null"/>
    /// otherwise (a malformed token, a deactivated table, another shop's token).</summary>
    internal static async Task<OrderingTable?> ActiveTableAsync(
        AppDbContext dbContext, OrderingShop shop, string? token, CancellationToken cancellationToken) =>
        shop.DineInOffered && OrderingTable.IsToken(token)
            ? await dbContext.OrderingTables.AsNoTracking()
                .SingleOrDefaultAsync(table => table.Token == token && table.DeactivatedAt == null, cancellationToken)
            : null;

    /// <summary>
    /// Cancels the order of <paramref name="token"/> while it is pending: a compare-and-set on its <c>Version</c> (the
    /// concurrency token), so of a cancellation and the shop's answer (#399) at the same moment exactly one wins. The winner
    /// calls <see cref="IOrderingOrderOutcomes.Decided"/> in its transaction.
    /// </summary>
    public async Task<OrderingCancelResult> CancelAsync(Guid organizationId, string? token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!OrderingOrder.IsStatusToken(token))
        {
            return new OrderingCancelResult(OrderingCancelOutcome.NotFound, null);
        }

        var hash = OrderingOrder.HashStatusToken(token!);
        await using (var dbContext = ForOrganization(organizationId))
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var order = await dbContext.OrderingOrders.SingleOrDefaultAsync(candidate => candidate.StatusTokenHash == hash, cancellationToken);
            if (order is null)
            {
                return new OrderingCancelResult(OrderingCancelOutcome.NotFound, null);
            }

            if (!order.CanCancel)
            {
                return new OrderingCancelResult(OrderingCancelOutcome.NotCancellable, order);
            }

            order.CancelByCustomer(now);
            var won = true;
            try
            {
                // The compare-and-set: UPDATE … WHERE Version = the version read.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The shop answered (or another cancel won) after it was read: below, as it is now.
                won = false;
            }

            if (won)
            {
                _outcomes.Decided(dbContext, order, now);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new OrderingCancelResult(OrderingCancelOutcome.Cancelled, order);
            }
        }

        return new OrderingCancelResult(OrderingCancelOutcome.NotCancellable, await FindByTokenAsync(organizationId, token, cancellationToken));
    }

    private static async Task<Func<DateTimeOffset, int>> SlotCountsAsync(
        AppDbContext dbContext, DateTimeOffset from, OrderingChannel channel, CancellationToken cancellationToken)
    {
        var start = from.ToUniversalTime().AddDays(-1);
        var type = channel == OrderingChannel.Delivery ? OrderingServiceType.DeliveryScheduled : OrderingServiceType.TakeoutScheduled;
        var counts = await dbContext.OrderingOrders.AsNoTracking()
            .Where(order => order.ServiceType == type && order.PickupAt >= start
                // Every status but the three that free the place (pending, accepted, ready, completed take one).
                && order.Status != OrderingOrderStatus.Cancelled
                && order.Status != OrderingOrderStatus.Declined
                && order.Status != OrderingOrderStatus.Expired)
            .GroupBy(order => order.PickupAt)
            .Select(group => new { At = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var byInstant = counts.ToDictionary(entry => entry.At.UtcDateTime, entry => entry.Count);
        return at => byInstant.GetValueOrDefault(at.UtcDateTime);
    }

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
