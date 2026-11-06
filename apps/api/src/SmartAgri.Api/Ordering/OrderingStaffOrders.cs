using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Ordering;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Ordering;

/// <summary>Which orders the admin lists (<see cref="OrderingStaffOrders.ListAsync"/>).</summary>
public enum OrderingOrderList
{
    /// <summary>待確認: every pending order, oldest first.</summary>
    Pending,

    /// <summary>處理中: accepted and ready, soonest promised first.</summary>
    Active,

    /// <summary>今日完成: completed, declined, cancelled and expired of the current business day, latest first.</summary>
    Done,
}

/// <summary>How a staff action went (<see cref="OrderingStaffOrders.ActAsync"/>).</summary>
public enum OrderingActOutcome
{
    /// <summary>Stored: <see cref="OrderingActResult.Order"/> as it is now.</summary>
    Done,

    /// <summary>No such order in the organization.</summary>
    NotFound,

    /// <summary>The order is at another version than the caller saw (someone else, or the customer, changed it first):
    /// <see cref="OrderingActResult.Order"/> as it is now.</summary>
    Changed,

    /// <summary>The caller saw the current version, but its status does not allow the action.</summary>
    NotAllowed,

    /// <summary>The promised time is out of range: <see cref="OrderingActResult.Problem"/>.</summary>
    InvalidPromisedAt,
}

public sealed record OrderingActResult(OrderingActOutcome Outcome, OrderingOrder? Order = null, string? Problem = null);

/// <summary>A staff action's input: the version the member saw and the action's own fields (already checked except the
/// promised time, which depends on the order).</summary>
public sealed record OrderingStaffAction(
    OrderingOrderAction Action,
    int Version,
    DateTimeOffset? PromisedAt = null,
    OrderingDeclineReason? DeclineReason = null,
    OrderingCancelReason? CancelReason = null,
    string? Note = null);

/// <summary>An organization's orders and shop switches as the admin sees them (<see cref="OrderingStaffOrders.SummaryAsync"/>).</summary>
public sealed record OrderingStaffSummary(
    OrderingShop Shop,
    int MenuVersion,
    DateOnly BusinessDate,
    int PendingCount,
    int ActiveCount,
    DateTimeOffset? LatestOrderAt,
    DateTimeOffset? ChangedAt,
    int OpenTableCount = 0);

/// <summary>How setting an item's state went.</summary>
public enum OrderingItemStateOutcome
{
    /// <summary>Stored (or it already was so): <see cref="OrderingItemStateResult.State"/> is the row now, or
    /// <see langword="null"/> when available.</summary>
    Done,

    /// <summary>The organization has no ordering settings or no menu.</summary>
    NotConfigured,

    /// <summary>The current menu has no item of that id.</summary>
    UnknownItem,
}

public sealed record OrderingItemStateResult(OrderingItemStateOutcome Outcome, MenuItem? Item = null, OrderingItemState? State = null);

/// <summary>What the staff set an item to (<c>PUT …/items/{itemId}/state</c>).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<OrderingItemAvailability>))]
public enum OrderingItemAvailability
{
    /// <summary>可點: no state.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("available")]
    Available,

    /// <summary>今日售完: back when the next business day's ordering hours open.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("sold-out-today")]
    SoldOutToday,

    /// <summary>停售: until set available again.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

/// <summary>
/// The staff's side of online ordering (Slice O3, #399): listing and answering a shop's orders and switching the shop
/// (paused, ASAP band, item states), each operation in a context acting for the caller's organization
/// (<see cref="FixedOrganizationContext"/>), under the usual filter and write guard.
/// </summary>
/// <remarks>
/// <para>
/// An action is a compare-and-set: the caller names the <c>Version</c> it saw; the order is changed by its domain method
/// (<see cref="OrderingOrder.Allows"/>) and stored with <c>Version</c> as the concurrency token, so of two members (or a
/// member and the customer cancelling) acting on the same version exactly one is stored and the other gets the order as it
/// is now. The winner calls <see cref="IOrderingOrderOutcomes.Decided"/> in its transaction for accept, decline and cancel.
/// </para>
/// <para>
/// The shop switches are frequent toggles: they raise <c>OrderingShops.Version</c> when they change something (so a
/// second open tab sees it in the summary) and write no organization activity. An item state change first locks the
/// shop's row (as a submission does), so it is checked against the order being submitted at the same moment either before
/// or after, never halfway.
/// </para>
/// </remarks>
public sealed class OrderingStaffOrders
{
    /// <summary>The most orders one list returns.</summary>
    public const int MaxListed = 500;

    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IOrderingOrderOutcomes _outcomes;

    public OrderingStaffOrders(DbContextOptions<AppDbContext> options, IOrderingOrderOutcomes outcomes)
    {
        _options = options;
        _outcomes = outcomes;
    }

    /// <summary>The organization's ordering settings; <see langword="null"/> when it has none or no menu.</summary>
    public async Task<(OrderingShop Shop, OrderingMenu Menu)?> ShopAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var shop = await dbContext.OrderingShops.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var menu = shop is null
            ? null
            : await dbContext.OrderingMenus.AsNoTracking().OrderByDescending(candidate => candidate.Version).FirstOrDefaultAsync(cancellationToken);
        return shop is null || menu is null ? null : (shop, menu);
    }

    /// <summary>The counts and cursors the admin polls; <see langword="null"/> when ordering is not set up.</summary>
    public async Task<OrderingStaffSummary?> SummaryAsync(Guid organizationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await ShopAsync(organizationId, cancellationToken) is not var (shop, menu))
        {
            return null;
        }

        await using var dbContext = ForOrganization(organizationId);
        var counts = await dbContext.OrderingOrders.AsNoTracking()
            .Where(order => order.Status == OrderingOrderStatus.Pending
                || order.Status == OrderingOrderStatus.Accepted
                || order.Status == OrderingOrderStatus.Ready)
            .GroupBy(order => order.Status == OrderingOrderStatus.Pending)
            .Select(group => new { Pending = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var latest = await dbContext.OrderingOrders.AsNoTracking().MaxAsync(order => (DateTimeOffset?)order.CreatedAt, cancellationToken);
        var changed = await dbContext.OrderingOrders.AsNoTracking().MaxAsync(order => (DateTimeOffset?)order.StatusChangedAt, cancellationToken);
        // #402: a table session opening, getting an order or closing moves the cursor too.
        var sessionChanged = await dbContext.OrderingTableSessions.AsNoTracking().MaxAsync(session => (DateTimeOffset?)session.UpdatedAt, cancellationToken);
        if (sessionChanged > changed || changed is null)
        {
            changed = sessionChanged;
        }

        var openTables = await dbContext.OrderingTableSessions.AsNoTracking().CountAsync(session => session.ClosedAt == null, cancellationToken);
        return new OrderingStaffSummary(
            shop,
            menu.Version,
            OrderingOrder.AsapBusinessDate(shop.Hours, now),
            counts.Where(entry => entry.Pending).Sum(entry => entry.Count),
            counts.Where(entry => !entry.Pending).Sum(entry => entry.Count),
            latest,
            changed,
            openTables);
    }

    /// <summary>The orders of <paramref name="view"/> at <paramref name="now"/> (see <see cref="OrderingOrderList"/>) and
    /// the display names of the members named in them, by account id.</summary>
    public async Task<(IReadOnlyList<OrderingOrder> Orders, IReadOnlyDictionary<Guid, string> Members)> ListAsync(
        Guid organizationId, OrderingShop shop, OrderingOrderList view, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shop);
        await using var dbContext = ForOrganization(organizationId);
        var orders = dbContext.OrderingOrders.AsNoTracking();
        List<OrderingOrder> listed;
        switch (view)
        {
            case OrderingOrderList.Pending:
                listed = await orders.Where(order => order.Status == OrderingOrderStatus.Pending)
                    .OrderBy(order => order.CreatedAt).Take(MaxListed).ToListAsync(cancellationToken);
                break;
            case OrderingOrderList.Active:
                listed = await orders.Where(order => order.Status == OrderingOrderStatus.Accepted || order.Status == OrderingOrderStatus.Ready)
                    .OrderBy(order => order.PromisedAt).ThenBy(order => order.CreatedAt).Take(MaxListed).ToListAsync(cancellationToken);
                break;
            default:
                var hours = shop.Hours;
                var today = OrderingOrder.AsapBusinessDate(hours, now);
                // The business day's orders, and whatever else was closed since the day began (a decline of tomorrow's order).
                var dayStarts = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), hours.Zone.GetUtcOffset(today.ToDateTime(TimeOnly.MinValue)))
                    .ToUniversalTime();
                listed = await orders.Where(order => (order.Status == OrderingOrderStatus.Completed
                            || order.Status == OrderingOrderStatus.Declined
                            || order.Status == OrderingOrderStatus.Cancelled
                            || order.Status == OrderingOrderStatus.Expired)
                        && (order.BusinessDate == today || order.StatusChangedAt >= dayStarts))
                    .OrderByDescending(order => order.StatusChangedAt).Take(MaxListed).ToListAsync(cancellationToken);
                break;
        }

        var memberIds = listed
            .SelectMany(order => new[]
            {
                order.AcceptedByAccountId, order.DeclinedByAccountId, order.ReadyByAccountId, order.CompletedByAccountId,
                order.CancelledByAccountId,
            })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var members = memberIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.Accounts.AsNoTracking()
                .Where(account => memberIds.Contains(account.Id))
                .ToDictionaryAsync(account => account.Id, account => account.DisplayName, cancellationToken);
        return (listed, members);
    }

    /// <summary>Order <paramref name="orderId"/> of the organization; <see langword="null"/> when it has none.</summary>
    public async Task<OrderingOrder?> FindAsync(Guid organizationId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.OrderingOrders.AsNoTracking().SingleOrDefaultAsync(order => order.Id == orderId, cancellationToken);
    }

    /// <summary>The display names of the members <paramref name="order"/> names.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> MembersAsync(Guid organizationId, OrderingOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var ids = new[]
            {
                order.AcceptedByAccountId, order.DeclinedByAccountId, order.ReadyByAccountId, order.CompletedByAccountId, order.CancelledByAccountId,
            }
            .OfType<Guid>()
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.Accounts.AsNoTracking()
            .Where(account => ids.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, account => account.DisplayName, cancellationToken);
    }

    /// <summary>Takes <paramref name="action"/> on order <paramref name="orderId"/> for member <paramref name="accountId"/>
    /// (see the remarks).</summary>
    public async Task<OrderingActResult> ActAsync(
        Guid organizationId, Guid orderId, OrderingStaffAction action, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using (var dbContext = ForOrganization(organizationId))
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var order = await dbContext.OrderingOrders.SingleOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
            if (order is null)
            {
                return new OrderingActResult(OrderingActOutcome.NotFound);
            }

            if (order.Version != action.Version)
            {
                return new OrderingActResult(OrderingActOutcome.Changed, order);
            }

            if (!OrderingOrder.Allows(order.Status, action.Action))
            {
                return new OrderingActResult(OrderingActOutcome.NotAllowed, order);
            }

            switch (action.Action)
            {
                case OrderingOrderAction.Accept:
                    if (order.PromisedAtProblem(action.PromisedAt, now) is { } problem)
                    {
                        return new OrderingActResult(OrderingActOutcome.InvalidPromisedAt, order, problem);
                    }

                    order.Accept(accountId, action.PromisedAt, now);
                    break;
                case OrderingOrderAction.Decline:
                    order.Decline(accountId, action.DeclineReason!.Value, action.Note, now);
                    break;
                case OrderingOrderAction.MarkReady:
                    order.MarkReady(accountId, now);
                    break;
                case OrderingOrderAction.Complete:
                    order.Complete(accountId, now);
                    break;
                default:
                    order.CancelByShop(accountId, action.CancelReason!.Value, action.Note, now);
                    break;
            }

            try
            {
                // The compare-and-set: UPDATE … WHERE Version = the version read.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Changed after it was read (another member, or the customer cancelling): below, as it is now.
                order = null;
            }

            if (order is not null)
            {
                // Won: what follows is added in the same transaction, so it commits with the change or not at all.
                if (action.Action is OrderingOrderAction.Accept or OrderingOrderAction.Decline or OrderingOrderAction.Cancel)
                {
                    _outcomes.Decided(dbContext, order, now);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                return new OrderingActResult(OrderingActOutcome.Done, order);
            }
        }

        await using var again = ForOrganization(organizationId);
        var current = await again.OrderingOrders.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
        return current is null ? new OrderingActResult(OrderingActOutcome.NotFound) : new OrderingActResult(OrderingActOutcome.Changed, current);
    }

    /// <summary>Pauses or resumes taking orders; raises the shop's version when it changed. <see langword="false"/> when
    /// ordering is not set up.</summary>
    public Task<bool> SetPausedAsync(Guid organizationId, bool paused, DateTimeOffset now, CancellationToken cancellationToken) =>
        SwitchAsync(
            organizationId,
            shops => shops.Where(shop => shop.Paused != paused),
            (setters, updatedAt) => setters
                .SetProperty(shop => shop.Paused, paused)
                .SetProperty(shop => shop.Version, shop => shop.Version + 1)
                .SetProperty(shop => shop.UpdatedAt, updatedAt),
            now,
            cancellationToken);

    /// <summary>Sets the ASAP preparation band; raises the shop's version when it changed. <see langword="false"/> when
    /// ordering is not set up.</summary>
    public Task<bool> SetAsapPrepBandAsync(Guid organizationId, AsapPrepBand band, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(band))
        {
            throw new ArgumentOutOfRangeException(nameof(band), band, "Not an ASAP preparation band.");
        }

        return SwitchAsync(
            organizationId,
            shops => shops.Where(shop => shop.AsapPrepBand != band),
            (setters, updatedAt) => setters
                .SetProperty(shop => shop.AsapPrepBand, band)
                .SetProperty(shop => shop.Version, shop => shop.Version + 1)
                .SetProperty(shop => shop.UpdatedAt, updatedAt),
            now,
            cancellationToken);
    }

    /// <summary>The shop's item states.</summary>
    public async Task<IReadOnlyList<OrderingItemState>> ItemStatesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.OrderingItemStates.AsNoTracking().ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Sets item <paramref name="itemId"/> of the current menu available, 今日售完 (back at
    /// <see cref="OrderingItemState.SoldOutResetAt"/>) or 停售. Nothing is written when it already is so (a sold-out item
    /// whose reset time has passed counts as available); otherwise the shop's version goes up with it.
    /// </summary>
    public async Task<OrderingItemStateResult> SetItemStateAsync(
        Guid organizationId, string itemId, OrderingItemAvailability availability, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Lock the shop's row (as a submission does) without changing it.
        if (await dbContext.OrderingShops.ExecuteUpdateAsync(
                setters => setters.SetProperty(shop => shop.Version, shop => shop.Version), cancellationToken) == 0)
        {
            return new OrderingItemStateResult(OrderingItemStateOutcome.NotConfigured);
        }

        var shop = await dbContext.OrderingShops.AsNoTracking().SingleAsync(cancellationToken);
        var menu = await dbContext.OrderingMenus.AsNoTracking().OrderByDescending(candidate => candidate.Version).FirstOrDefaultAsync(cancellationToken);
        if (menu is null)
        {
            return new OrderingItemStateResult(OrderingItemStateOutcome.NotConfigured);
        }

        var item = menu.Document.Categories.SelectMany(category => category.Items).FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null)
        {
            return new OrderingItemStateResult(OrderingItemStateOutcome.UnknownItem);
        }

        var existing = await dbContext.OrderingItemStates.Where(state => state.ItemId == itemId).ToListAsync(cancellationToken);
        var effective = existing.FirstOrDefault(state => !state.IsAvailableAt(now));
        var unchanged = availability switch
        {
            OrderingItemAvailability.Available => effective is null,
            OrderingItemAvailability.SoldOutToday => effective?.Kind == OrderingItemStateKind.SoldOutToday,
            _ => effective?.Kind == OrderingItemStateKind.Unavailable,
        };
        if (unchanged)
        {
            return new OrderingItemStateResult(OrderingItemStateOutcome.Done, item, effective);
        }

        dbContext.OrderingItemStates.RemoveRange(existing);
        OrderingItemState? added = availability switch
        {
            OrderingItemAvailability.SoldOutToday => OrderingItemState.SoldOutToday(organizationId, itemId, shop.Hours, now),
            OrderingItemAvailability.Unavailable => OrderingItemState.Unavailable(organizationId, itemId, now),
            _ => null,
        };
        if (added is not null)
        {
            dbContext.OrderingItemStates.Add(added);
        }

        // The removal goes first, so the unique (OrganizationId, ItemId) index sees one row at a time.
        await dbContext.SaveChangesAsync(cancellationToken);
        var updatedAt = Microseconds(now);
        await dbContext.OrderingShops.ExecuteUpdateAsync(
            setters => setters.SetProperty(candidate => candidate.Version, candidate => candidate.Version + 1).SetProperty(candidate => candidate.UpdatedAt, updatedAt),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OrderingItemStateResult(OrderingItemStateOutcome.Done, item, added);
    }

    private async Task<bool> SwitchAsync(
        Guid organizationId,
        Func<IQueryable<OrderingShop>, IQueryable<OrderingShop>> differing,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<OrderingShop>, DateTimeOffset> set,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var updatedAt = Microseconds(now);
        await differing(dbContext.OrderingShops).ExecuteUpdateAsync(setters => set(setters, updatedAt), cancellationToken);
        return await dbContext.OrderingShops.AnyAsync(cancellationToken);
    }

    /// <summary>PostgreSQL keeps microseconds; a bulk update bypasses the interceptor that truncates.</summary>
    private static DateTimeOffset Microseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
