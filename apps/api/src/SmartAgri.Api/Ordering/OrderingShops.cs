using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Ordering;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Persistence;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Ordering;

/// <summary>How an import went (<see cref="OrderingShops.ImportAsync"/>).</summary>
public enum OrderingImportOutcome
{
    /// <summary>The shop had no ordering settings; these and the menu (version 1) are now its.</summary>
    Created,

    /// <summary>Something changed (<see cref="OrderingImportResult.Changed"/>).</summary>
    Updated,

    /// <summary>The same settings and menu as stored: nothing was written.</summary>
    Unchanged,

    /// <summary>The slug is another shop's: nothing was written.</summary>
    SlugTaken,

    /// <summary>Another import of the same shop added a menu version at the same moment: nothing was written.</summary>
    Conflict,
}

/// <summary>An import's outcome, the names of what changed (<c>shop</c> fields and <c>menu</c>) and the current menu
/// version.</summary>
public sealed record OrderingImportResult(OrderingImportOutcome Outcome, IReadOnlyList<string> Changed, int? MenuVersion);

/// <summary>A shop's ordering settings and its current menu (<see cref="OrderingShops.FindAsync"/>).</summary>
public sealed record OrderingShopView(OrderingShop Shop, OrderingMenu? Menu);

/// <summary>
/// Reading and importing a shop's online-ordering settings and menu (online-ordering Slice O1, #397), each in a context
/// acting for that organization (<see cref="FixedOrganizationContext"/>), under the usual filter and write guard — nothing
/// here reads across organizations. A slug another shop has is caught by the unique index.
/// </summary>
public sealed class OrderingShops
{
    /// <summary>EF Core's name for the unique index on <c>OrderingShops.Slug</c>.</summary>
    internal const string SlugIndex = "IX_OrderingShops_Slug";

    /// <summary>EF Core's name for the unique index on <c>OrderingMenus (OrganizationId, Version)</c>.</summary>
    internal const string MenuVersionIndex = "IX_OrderingMenus_OrganizationId_Version";

    private readonly DbContextOptions<AppDbContext> _options;
    private readonly TimeProvider _clock;

    public OrderingShops(DbContextOptions<AppDbContext> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    /// <summary>The ordering settings of <paramref name="organizationId"/> and its current menu; <see langword="null"/>
    /// when none are set.</summary>
    public async Task<OrderingShopView?> FindAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var shop = await dbContext.OrderingShops.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (shop is null)
        {
            return null;
        }

        var menu = await LatestMenuQuery(dbContext).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return new OrderingShopView(shop, menu);
    }

    /// <summary>
    /// Makes <paramref name="import"/> (already validated by <see cref="OrderingShopJson.Parse"/>) the ordering settings of
    /// <paramref name="organizationId"/>, adding a menu version when the menu differs from the current one. When anything
    /// changed, one <see cref="OrganizationActivityAction.OrderingShopUpdated"/> is written in the same save; an identical
    /// import writes nothing. The staff's switches (paused, ASAP band) are kept. The tables (#402) follow the <c>dineIn</c>
    /// section (<see cref="OrderingTable.Sync"/>): a new table id gets a QR code token, a kept one keeps its token, a removed one
    /// is deactivated.
    /// </summary>
    public async Task<OrderingImportResult> ImportAsync(Guid organizationId, OrderingImport import, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(import);
        await using var dbContext = ForOrganization(organizationId);
        var now = _clock.GetUtcNow();
        var shop = await dbContext.OrderingShops.SingleOrDefaultAsync(cancellationToken);
        var latest = await LatestMenuQuery(dbContext).AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        var created = shop is null;
        var changed = new List<string>();
        if (shop is null)
        {
            dbContext.OrderingShops.Add(OrderingShop.Create(organizationId, import.Shop, now));
            changed.AddRange(OrderingShopSettings.FieldNames);
        }
        else
        {
            changed.AddRange(shop.Replace(import.Shop, now));
        }

        int? newVersion = null;
        if (latest is null || OrderingShopJson.WriteMenu(latest.Document) != OrderingShopJson.WriteMenu(import.Menu))
        {
            newVersion = (latest?.Version ?? 0) + 1;
            dbContext.OrderingMenus.Add(OrderingMenu.Create(organizationId, newVersion.Value, import.Menu, now));
            changed.Add("menu");
        }

        var menuVersion = newVersion ?? latest!.Version;
        var tables = await dbContext.OrderingTables.Where(table => table.DeactivatedAt == null).ToListAsync(cancellationToken);
        dbContext.OrderingTables.AddRange(OrderingTable.Sync(organizationId, tables, import.Shop.DineIn, now));
        if (changed.Count == 0)
        {
            // The tables follow the file, so they changed only with it — unless they predate it (none to add then, normally).
            if (dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return new OrderingImportResult(OrderingImportOutcome.Unchanged, [], menuVersion);
        }

        dbContext.OrganizationActivities.Add(OrganizationActivity.OrderingShopUpdated(organizationId, now, created, changed, newVersion));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, SlugIndex))
        {
            return new OrderingImportResult(OrderingImportOutcome.SlugTaken, [], null);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, MenuVersionIndex)
                                                  || DatabaseErrors.IsUniqueViolation(exception, "IX_OrderingShops_OrganizationId"))
        {
            return new OrderingImportResult(OrderingImportOutcome.Conflict, [], null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new OrderingImportResult(OrderingImportOutcome.Conflict, [], null);
        }

        return new OrderingImportResult(created ? OrderingImportOutcome.Created : OrderingImportOutcome.Updated, changed, menuVersion);
    }

    /// <summary>The shop's active tables (#402), in the file's order.</summary>
    public async Task<IReadOnlyList<OrderingTable>> TablesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var shop = await dbContext.OrderingShops.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var tables = await dbContext.OrderingTables.AsNoTracking().Where(table => table.DeactivatedAt == null).ToListAsync(cancellationToken);
        var order = (shop?.DineInSettings?.Tables ?? []).Select((table, index) => (table.Id, index)).ToDictionary(entry => entry.Id, entry => entry.index);
        return [.. tables.OrderBy(table => order.GetValueOrDefault(table.TableId, int.MaxValue)).ThenBy(table => table.TableId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// A new QR code token for table <paramref name="tableId"/> (#402): the old one stops working at once. Writes one
    /// <see cref="OrganizationActivityAction.OrderingShopUpdated"/> naming <c>tables</c>. <see langword="null"/> when the shop has
    /// no active table of that id.
    /// </summary>
    public async Task<OrderingTable?> RegenerateAsync(Guid organizationId, string tableId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var table = await dbContext.OrderingTables.SingleOrDefaultAsync(
            candidate => candidate.TableId == tableId && candidate.DeactivatedAt == null, cancellationToken);
        if (table is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        table.Regenerate(now);
        dbContext.OrganizationActivities.Add(OrganizationActivity.OrderingShopUpdated(organizationId, now, created: false, ["tables"], null));
        await dbContext.SaveChangesAsync(cancellationToken);
        return table;
    }

    private static IQueryable<OrderingMenu> LatestMenuQuery(AppDbContext dbContext) =>
        dbContext.OrderingMenus.OrderByDescending(menu => menu.Version);

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
