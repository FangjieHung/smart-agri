using Microsoft.EntityFrameworkCore;

namespace SmartAgri.Infrastructure.Ordering;

/// <summary>
/// The online-ordering page's one cross-organization read (online-ordering Slice O2, #398): a customer opening
/// <c>/order/{slug}</c> is anonymous, so no organization exists until the slug's shop is found — like
/// <see cref="Coordinator.CoordinatorLookup"/> for the reservation page, this is the one place in <c>apps/api/src</c> allowed
/// to switch the organization filter off for ordering data (a source-scanning test enforces it). It returns nothing but the
/// organization id; everything else — the shop, its menu, its orders, the status token — is read in a context acting for
/// that organization (<c>FixedOrganizationContext</c>), under the usual filter and write guard.
/// </summary>
public sealed class OrderingLookup
{
    private readonly AppDbContext _dbContext;

    public OrderingLookup(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>The organization whose ordering settings have <paramref name="slug"/> (already normalized, lower case);
    /// <see langword="null"/> when none.</summary>
    public async Task<Guid?> FindShopAsync(string slug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);
        return await _dbContext.OrderingShops
            .IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .AsNoTracking()
            .Where(shop => shop.Slug == slug)
            .Select(shop => (Guid?)shop.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
