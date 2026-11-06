using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Ordering;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Ordering;

/// <summary>A table session (#402) with its orders, oldest first.</summary>
public sealed record OrderingSessionOrders(OrderingTableSession Session, IReadOnlyList<OrderingOrder> Orders);

/// <summary>A table as its QR code opens it (#402): the active table and its open session (with its orders), if any.</summary>
public sealed record OrderingTableInfo(OrderingTable Table, OrderingSessionOrders? Session);

/// <summary>How 〔已結帳〕 went (<see cref="OrderingDineInTables.CloseAsync"/>).</summary>
public enum OrderingCloseOutcome
{
    /// <summary>Closed; its accepted and ready orders completed.</summary>
    Closed,

    /// <summary>No such session in the organization.</summary>
    NotFound,

    /// <summary>The session is at another version than the caller saw (another order joined, or someone closed it):
    /// <see cref="OrderingCloseResult.Session"/> as it is now.</summary>
    Changed,

    /// <summary>An order of the session is still pending: accept or decline it first.</summary>
    HasPending,
}

/// <param name="Session">The session and its orders as they are now (after the close when <see cref="OrderingCloseOutcome.Closed"/>).</param>
/// <param name="Completed">The orders the close completed.</param>
public sealed record OrderingCloseResult(OrderingCloseOutcome Outcome, OrderingSessionOrders? Session = null, IReadOnlyList<OrderingOrder>? Completed = null);

/// <summary>
/// 內用桌次 (online-ordering design D11, Slice O6, #402): a table's QR code as the customer opens it, and the open sessions the
/// staff close with 〔已結帳〕 — each in a context acting for one organization (<see cref="FixedOrganizationContext"/>).
/// </summary>
/// <remarks>
/// 〔已結帳〕 first locks the shop's <c>OrderingShops</c> row, as a submission does (<see cref="OrderingOrders.SubmitAsync"/>),
/// so an order sent at the same moment either joins the session before it is read here — and the close is refused (the
/// version moved, or the order is pending) — or comes after it and opens a new session. The session's <c>Version</c> is the
/// compare-and-set; the orders it completes are stored with theirs, so a member answering one of them at the same moment
/// makes the close fail as a whole (<see cref="OrderingCloseOutcome.Changed"/>) rather than half.
/// </remarks>
public sealed class OrderingDineInTables
{
    private readonly DbContextOptions<AppDbContext> _options;

    public OrderingDineInTables(DbContextOptions<AppDbContext> options)
    {
        _options = options;
    }

    /// <summary>The table of <paramref name="token"/> and its open session; <see langword="null"/> when the shop does not offer
    /// dine-in or has no active table with that token.</summary>
    public async Task<OrderingTableInfo?> TableAsync(OrderingShop shop, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shop);
        await using var dbContext = ForOrganization(shop.OrganizationId);
        if (await OrderingOrders.ActiveTableAsync(dbContext, shop, token, cancellationToken) is not { } table)
        {
            return null;
        }

        var session = await dbContext.OrderingTableSessions.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TableId == table.TableId && candidate.ClosedAt == null, cancellationToken);
        return new OrderingTableInfo(table, session is null ? null : new OrderingSessionOrders(session, await OrdersOfAsync(dbContext, [session.Id], cancellationToken)));
    }

    /// <summary>Every open session, the oldest first, with its orders.</summary>
    public async Task<IReadOnlyList<OrderingSessionOrders>> OpenSessionsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var sessions = await dbContext.OrderingTableSessions.AsNoTracking()
            .Where(session => session.ClosedAt == null)
            .OrderBy(session => session.OpenedAt)
            .ToListAsync(cancellationToken);
        var orders = await OrdersOfAsync(dbContext, [.. sessions.Select(session => session.Id)], cancellationToken);
        return [.. sessions.Select(session => new OrderingSessionOrders(session, [.. orders.Where(order => order.TableSessionId == session.Id)]))];
    }

    /// <summary>〔已結帳〕 on session <paramref name="sessionId"/> at <paramref name="version"/> by <paramref name="accountId"/>
    /// (see the remarks).</summary>
    public async Task<OrderingCloseResult> CloseAsync(
        Guid organizationId, Guid sessionId, int version, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using (var dbContext = ForOrganization(organizationId))
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.OrderingShops.ExecuteUpdateAsync(
                setters => setters.SetProperty(shop => shop.Version, shop => shop.Version), cancellationToken);
            var session = await dbContext.OrderingTableSessions.SingleOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);
            if (session is null)
            {
                return new OrderingCloseResult(OrderingCloseOutcome.NotFound);
            }

            var orders = await dbContext.OrderingOrders
                .Where(order => order.TableSessionId == sessionId)
                .OrderBy(order => order.CreatedAt)
                .ToListAsync(cancellationToken);
            if (session.Version != version || !session.IsOpen)
            {
                return new OrderingCloseResult(OrderingCloseOutcome.Changed, new OrderingSessionOrders(session, orders));
            }

            if (!OrderingTableSession.CanClose(orders.Select(order => order.Status)))
            {
                return new OrderingCloseResult(OrderingCloseOutcome.HasPending, new OrderingSessionOrders(session, orders));
            }

            var completed = session.Close(accountId, orders, now);
            try
            {
                // The compare-and-set on the session and on every order it completes.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                session = null;
            }

            if (session is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new OrderingCloseResult(OrderingCloseOutcome.Closed, new OrderingSessionOrders(session, orders), completed);
            }
        }

        return await SessionAsync(organizationId, sessionId, cancellationToken) is { } current
            ? new OrderingCloseResult(OrderingCloseOutcome.Changed, current)
            : new OrderingCloseResult(OrderingCloseOutcome.NotFound);
    }

    /// <summary>Session <paramref name="sessionId"/> with its orders; <see langword="null"/> when the organization has none.</summary>
    public async Task<OrderingSessionOrders?> SessionAsync(Guid organizationId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var session = await dbContext.OrderingTableSessions.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);
        return session is null ? null : new OrderingSessionOrders(session, await OrdersOfAsync(dbContext, [sessionId], cancellationToken));
    }

    /// <summary>The display names of the members named by <paramref name="sessions"/> and their orders.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> MembersAsync(
        Guid organizationId, IEnumerable<OrderingSessionOrders> sessions, CancellationToken cancellationToken)
    {
        var ids = sessions
            .SelectMany(entry => entry.Orders
                .SelectMany(order => new[]
                {
                    order.AcceptedByAccountId, order.DeclinedByAccountId, order.ReadyByAccountId, order.CompletedByAccountId,
                    order.CancelledByAccountId,
                })
                .Append(entry.Session.ClosedByAccountId))
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

    private static async Task<List<OrderingOrder>> OrdersOfAsync(AppDbContext dbContext, List<Guid> sessionIds, CancellationToken cancellationToken) =>
        sessionIds.Count == 0
            ? []
            : await dbContext.OrderingOrders.AsNoTracking()
                .Where(order => order.TableSessionId != null && sessionIds.Contains(order.TableSessionId.Value))
                .OrderBy(order => order.CreatedAt)
                .ToListAsync(cancellationToken);

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
