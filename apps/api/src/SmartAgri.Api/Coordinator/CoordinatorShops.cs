using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Coordinator;
using SmartAgri.Infrastructure.Persistence;
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

    /// <summary>The reservation page's slug is another shop's (#389): nothing was written.</summary>
    SlugTaken,
}

public sealed record ShopImportResult(ShopImportOutcome Outcome, ShopSettingsChange Change);

/// <summary>
/// Reading and replacing a shop's 統籌助理 settings (store-coordinator design §5 Slice 2, #373), each in a
/// context acting for that organization (<see cref="FixedOrganizationContext"/>), under the usual filter
/// and write guard. Slices 3 and 4 read a shop's whole settings with <see cref="FindAsync"/>.
/// </summary>
public sealed class CoordinatorShops
{
    /// <summary>EF Core's name for the unique index on <c>CoordinatorShopSettings.ReservationSlug</c> (#389).</summary>
    internal const string ReservationSlugIndex = "IX_CoordinatorShopSettings_ReservationSlug";

    private readonly DbContextOptions<AppDbContext> _options;
    private readonly CoordinatorLookup _lookup;
    private readonly TimeProvider _clock;

    public CoordinatorShops(DbContextOptions<AppDbContext> options, CoordinatorLookup lookup, TimeProvider clock)
    {
        _options = options;
        _lookup = lookup;
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
    /// an identical import writes nothing. A reservation slug another shop has is
    /// <see cref="ShopImportOutcome.SlugTaken"/> (the unique index settles a race), and nothing is written (#389).
    /// </summary>
    public async Task<ShopImportResult> ImportAsync(Guid organizationId, ShopSettingsDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Reservations?.Slug is { } slug
            && await _lookup.FindReservationShopAsync(slug, cancellationToken) is { } owner
            && owner != organizationId)
        {
            return new ShopImportResult(ShopImportOutcome.SlugTaken, new ShopSettingsChange([], []));
        }

        await using var dbContext = ForOrganization(organizationId);
        var now = _clock.GetUtcNow();
        var settings = await dbContext.CoordinatorShopSettings.SingleOrDefaultAsync(cancellationToken);
        ShopImportResult result;
        if (settings is null)
        {
            dbContext.CoordinatorShopSettings.Add(ShopSettings.Create(organizationId, document, now));
            result = new ShopImportResult(ShopImportOutcome.Created, ShopSettingsChange.Created(document));
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
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, ReservationSlugIndex))
        {
            return new ShopImportResult(ShopImportOutcome.SlugTaken, new ShopSettingsChange([], []));
        }

        return result;
    }

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
