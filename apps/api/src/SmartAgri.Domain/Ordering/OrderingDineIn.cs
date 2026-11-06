using System.Buffers.Text;
using System.Security.Cryptography;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Ordering;

/// <summary>A table of the import file's <c>dineIn.tables</c> (online-ordering design D11, Slice O6, #402).</summary>
/// <param name="Id">The import file's id (<see cref="MenuLimits.IsValidId"/>), unique within the shop: its QR code stays the
/// same as long as the id does.</param>
/// <param name="Name">「A1」「窗邊 2」: shown to the customer and the staff, one line of at most
/// <see cref="OrderingDineInSettings.TableNameMaxLength"/>.</param>
public sealed record DiningTable(string Id, string Name);

/// <summary>
/// The import file's <c>shop.dineIn</c> section (design D11, #402): the tables, whether a dine-in order is accepted at once,
/// when dine-in orders are taken (by default the takeout <see cref="OrderingShopSettings.OrderingHours"/>), what the customer
/// is told about paying, and how many pending orders one table may have.
/// </summary>
/// <param name="AutoAccept">Accept a dine-in order as it is sent (「自動接單」): no one has to press 〔接單〕.</param>
/// <param name="OrderingHours">When dine-in orders are taken (<see cref="WeeklyHours"/>' format); <see langword="null"/> = the
/// takeout ordering hours.</param>
/// <param name="Note">「用餐後請至櫃台結帳」: shown where takeout shows how to pay.</param>
/// <param name="MaxPendingPerTable">The most pending orders one table may have (instead of the per-phone limit).</param>
public sealed record OrderingDineInSettings(
    IReadOnlyList<DiningTable> Tables,
    bool AutoAccept = false,
    WeeklyHours? OrderingHours = null,
    string? Note = null,
    int MaxPendingPerTable = OrderingDineInSettings.DefaultMaxPendingPerTable)
{
    public const int MaxTables = 200;

    public const int TableNameMaxLength = 20;

    public const int NoteMaxLength = 300;

    public const int DefaultMaxPendingPerTable = 5;

    public const int MaxMaxPendingPerTable = 20;

    /// <summary>The table <paramref name="id"/>; <see langword="null"/> when there is none.</summary>
    public DiningTable? Table(string? id) => id is null ? null : Tables.FirstOrDefault(table => table.Id == id);
}

/// <summary>
/// 桌號 QR code (table <c>OrderingTables</c>, #402): the random token printed on one table, made by the server the first time
/// the table's id appears in an import and kept across imports. A table taken out of the file is deactivated — its token
/// stops working, and the same id added back gets a new one; <see cref="Regenerate"/> replaces a token on demand.
/// </summary>
/// <remarks>The token is stored as it is (not hashed like an order's status token): it is printed on the table for everyone to
/// see anyway, and the operator prints it again from here (<c>ordering-shop tables</c>). It only lets someone order to that
/// table and see the table's open session (numbers, statuses, items, totals — no customer details). It travels in the
/// <c>X-Ordering-Table</c> request header and the URL fragment, never in a path or query.</remarks>
public sealed class OrderingTable : IOrganizationScoped
{
    /// <summary>Characters of a token (32 random bytes, base64url without padding).</summary>
    public const int TokenLength = 43;

    /// <summary>For EF Core materialization.</summary>
    private OrderingTable()
    {
        TableId = string.Empty;
        Name = string.Empty;
        Token = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The import file's id (<see cref="DiningTable.Id"/>).</summary>
    public string TableId { get; private set; }

    /// <summary>The name as the file has it now.</summary>
    public string Name { get; private set; }

    /// <summary>What the QR code carries; unique.</summary>
    public string Token { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When <see cref="Token"/> was made (<see cref="CreatedAt"/> until <see cref="Regenerate"/>).</summary>
    public DateTimeOffset TokenIssuedAt { get; private set; }

    /// <summary>When the table left the file; its token no longer works. <see langword="null"/> while active.</summary>
    public DateTimeOffset? DeactivatedAt { get; private set; }

    public bool IsActive => DeactivatedAt is null;

    public static OrderingTable Create(Guid organizationId, DiningTable table, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A table always belongs to an organization.", nameof(organizationId));
        }

        ArgumentNullException.ThrowIfNull(table);
        return new OrderingTable
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            TableId = table.Id,
            Name = table.Name,
            Token = NewToken(),
            CreatedAt = now.ToUniversalTime(),
            TokenIssuedAt = now.ToUniversalTime(),
        };
    }

    /// <summary>A new token: the old one stops working at once.</summary>
    public void Regenerate(DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("A deactivated table gets no new token.");
        }

        Token = NewToken();
        TokenIssuedAt = now.ToUniversalTime();
    }

    /// <summary>A new token: 32 random bytes, base64url without padding.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Exactly <see cref="TokenLength"/> characters of base64url.</summary>
    public static bool IsToken(string? token) =>
        token is { Length: TokenLength }
        && token.All(character => character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>
    /// Brings a shop's tables in line with its <c>dineIn</c> section (#402): a table whose id is new gets a row and a token;
    /// one still in the file keeps its token (its name follows the file); one gone from the file — or every table, when there
    /// is no section — is deactivated. Returns the rows to add; <paramref name="existing"/> are changed in place.
    /// </summary>
    /// <param name="existing">The shop's active tables.</param>
    public static IReadOnlyList<OrderingTable> Sync(
        Guid organizationId, IReadOnlyList<OrderingTable> existing, OrderingDineInSettings? settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var wanted = settings?.Tables ?? [];
        var byId = existing.Where(table => table.IsActive).ToDictionary(table => table.TableId, StringComparer.Ordinal);
        var added = new List<OrderingTable>();
        foreach (var table in wanted)
        {
            if (byId.Remove(table.Id, out var row))
            {
                row.Name = table.Name;
            }
            else
            {
                added.Add(Create(organizationId, table, now));
            }
        }

        foreach (var gone in byId.Values)
        {
            gone.DeactivatedAt = now.ToUniversalTime();
        }

        return added;
    }
}

/// <summary>
/// 桌次 (table <c>OrderingTableSessions</c>, design D11, #402): the orders of one table from its first order until the staff
/// press 〔已結帳〕. The first order on a table without an open session opens one; later orders join it; after it is closed
/// the same QR code starts a new one. Holds no personal data: the table's id and name as they were, and when.
/// </summary>
/// <remarks>At most one open session per table (a unique index over the open ones). Orders join and sessions close under the
/// shop's row lock, so an order never joins a session that is being closed. <see cref="Version"/> goes up with every order
/// that joins and with the close: the staff's 〔已結帳〕 names the version they saw (compare-and-set), so a session that got
/// another order since is not closed on a total the staff did not see.</remarks>
public sealed class OrderingTableSession : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private OrderingTableSession()
    {
        TableId = string.Empty;
        TableName = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string TableId { get; private set; }

    /// <summary>The table's name when the session opened.</summary>
    public string TableName { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    /// <summary>When an order last joined it, or it closed: what tells the admin its list is stale.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>〔已結帳〕; <see langword="null"/> while open.</summary>
    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>The member who closed it.</summary>
    public Guid? ClosedByAccountId { get; private set; }

    /// <summary>1 when opened; one more with every order that joins and with the close.</summary>
    public int Version { get; private set; }

    public bool IsOpen => ClosedAt is null;

    /// <summary>The session the first order on <paramref name="table"/> opens.</summary>
    public static OrderingTableSession Open(OrderingTable table, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!table.IsActive)
        {
            throw new InvalidOperationException("A deactivated table takes no order.");
        }

        return new OrderingTableSession
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = table.OrganizationId,
            TableId = table.TableId,
            TableName = table.Name,
            OpenedAt = now.ToUniversalTime(),
            UpdatedAt = now.ToUniversalTime(),
            Version = 1,
        };
    }

    /// <summary>Another order joins the open session.</summary>
    public void Join(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("A closed session takes no more orders.");
        }

        Version++;
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>Whether a session with orders of <paramref name="statuses"/> can be closed: none is still pending (the staff
    /// accept or decline it first).</summary>
    public static bool CanClose(IEnumerable<OrderingOrderStatus> statuses) =>
        !statuses.Any(status => status == OrderingOrderStatus.Pending);

    /// <summary>
    /// 〔已結帳〕 by <paramref name="accountId"/>: the session is closed and every accepted or ready order of it completed
    /// (paid at the counter). <paramref name="orders"/> are the session's orders; none may be pending
    /// (<see cref="CanClose"/>). Returns the orders it completed.
    /// </summary>
    public IReadOnlyList<OrderingOrder> Close(Guid accountId, IReadOnlyList<OrderingOrder> orders, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(orders);
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("A member closes a session.", nameof(accountId));
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("The session is already closed.");
        }

        if (orders.Any(order => order.TableSessionId != Id))
        {
            throw new ArgumentException("Only the session's own orders.", nameof(orders));
        }

        if (!CanClose(orders.Select(order => order.Status)))
        {
            throw new InvalidOperationException("A session with a pending order cannot be closed.");
        }

        var completed = new List<OrderingOrder>();
        foreach (var order in orders.Where(order => OrderingOrder.Allows(order.Status, OrderingOrderAction.Complete)))
        {
            order.Complete(accountId, now);
            completed.Add(order);
        }

        ClosedAt = now.ToUniversalTime();
        ClosedByAccountId = accountId;
        UpdatedAt = ClosedAt.Value;
        Version++;
        return completed;
    }

    /// <summary>What the session's orders come to: those not declined, cancelled or expired.</summary>
    public static int Total(IEnumerable<OrderingOrder> orders) =>
        orders.Where(Counts).Sum(order => order.Total);

    /// <summary>An order that is (to be) paid at the counter: pending, accepted, ready or completed.</summary>
    public static bool Counts(OrderingOrder order) =>
        order.Status is OrderingOrderStatus.Pending or OrderingOrderStatus.Accepted or OrderingOrderStatus.Ready or OrderingOrderStatus.Completed;
}

/// <summary>What a dine-in order stores of its table (#402): the table's id and name as they were, and the session it joined.</summary>
public sealed record OrderingOrderTable(string TableId, string TableName, Guid SessionId);

/// <summary>When dine-in orders are taken (#402).</summary>
public static class OrderingDineIn
{
    /// <summary>The shop offers dine-in, is not paused, and <paramref name="now"/> is inside a dine-in period.</summary>
    public static bool CanOrder(OrderingShop shop, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(shop);
        return shop.DineInOffered && !shop.Paused && IsOpen(shop.DineInBusinessHours, now);
    }

    /// <summary><paramref name="now"/> is inside a period of <paramref name="hours"/> (an overnight period's small hours
    /// count).</summary>
    public static bool IsOpen(BusinessHours hours, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hours);
        var today = hours.DateAt(now);
        return new[] { today.AddDays(-1), today }
            .SelectMany(hours.PeriodsOn)
            .Any(period => period.Opens <= now && now < period.Closes);
    }
}
