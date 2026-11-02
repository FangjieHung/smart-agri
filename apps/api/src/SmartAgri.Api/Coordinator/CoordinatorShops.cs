using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>How an import went (<see cref="CoordinatorShops.ImportAsync"/>).</summary>
public enum ShopImportOutcome
{
    /// <summary>The shop had no settings; these are now its settings.</summary>
    Created,

    /// <summary>Something changed (<see cref="ShopImportResult.Change"/>).</summary>
    Updated,

    /// <summary>The same as the stored settings: nothing was written.</summary>
    Unchanged,
}

public sealed record ShopImportResult(ShopImportOutcome Outcome, ShopSettingsChange Change);

/// <summary>
/// Reading and replacing a shop's 統籌助理 settings (store-coordinator design §5 Slice 2, #373), each in a
/// context acting for that organization (<see cref="FixedOrganizationContext"/>), under the usual filter
/// and write guard. Slices 3 and 4 read a shop's whole settings with <see cref="FindAsync"/>.
/// </summary>
public sealed class CoordinatorShops
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly TimeProvider _clock;

    public CoordinatorShops(DbContextOptions<AppDbContext> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    /// <summary>The settings of <paramref name="organizationId"/>; <see langword="null"/> when none are set.</summary>
    public async Task<ShopSettingsDocument?> FindAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var settings = await dbContext.CoordinatorShopSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return settings?.Document;
    }

    /// <summary>
    /// Makes <paramref name="document"/> (already validated by <see cref="ShopSettingsJson.Parse"/>) the
    /// settings of <paramref name="organizationId"/>. When anything changed, one
    /// <see cref="OrganizationActivityAction.CoordinatorShopSettingsUpdated"/> is written in the same save;
    /// an identical import writes nothing.
    /// </summary>
    public async Task<ShopImportResult> ImportAsync(Guid organizationId, ShopSettingsDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        await using var dbContext = ForOrganization(organizationId);
        var now = _clock.GetUtcNow();
        var settings = await dbContext.CoordinatorShopSettings.SingleOrDefaultAsync(cancellationToken);
        ShopImportResult result;
        if (settings is null)
        {
            dbContext.CoordinatorShopSettings.Add(ShopSettings.Create(organizationId, document, now));
            result = new ShopImportResult(ShopImportOutcome.Created, ShopSettingsChange.All);
        }
        else
        {
            var change = settings.Replace(document, now);
            if (change.IsEmpty)
            {
                return new ShopImportResult(ShopImportOutcome.Unchanged, change);
            }

            result = new ShopImportResult(ShopImportOutcome.Updated, change);
        }

        dbContext.OrganizationActivities.Add(OrganizationActivity.CoordinatorShopSettingsUpdated(
            organizationId, now, created: result.Outcome == ShopImportOutcome.Created, result.Change));
        await dbContext.SaveChangesAsync(cancellationToken);
        return result;
    }

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
