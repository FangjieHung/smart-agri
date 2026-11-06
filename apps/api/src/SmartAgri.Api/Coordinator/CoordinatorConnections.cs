using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;
using SmartAgri.Domain.Secrets;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>How setting a credential went (<see cref="CoordinatorConnections.SetAsync"/>).</summary>
public enum CoordinatorConnectionChange
{
    /// <summary>The shop had none for the service.</summary>
    Created,

    /// <summary>It replaced the stored one (active or revoked).</summary>
    Replaced,
}

/// <summary>
/// The shops' credentials for external services (table <c>CoordinatorConnections</c>, #376), each read and written in
/// a context acting for one organization (<see cref="FixedOrganizationContext"/>), under the usual filter and write
/// guard. Setting or clearing one writes an <see cref="OrganizationActivityAction.CoordinatorConnectionUpdated"/> in
/// the same save; a revocation is a compare-and-set on <see cref="CoordinatorConnection.UpdatedAt"/>, so a credential
/// set again meanwhile stays active. Nothing here decrypts a credential.
/// </summary>
public sealed class CoordinatorConnections
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly TimeProvider _clock;

    public CoordinatorConnections(DbContextOptions<AppDbContext> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    /// <summary>The connection of <paramref name="organizationId"/> to <paramref name="service"/>, active or revoked.</summary>
    public async Task<CoordinatorConnection?> FindAsync(Guid organizationId, CoordinatorService service, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorConnections.AsNoTracking()
            .SingleOrDefaultAsync(connection => connection.Service == service, cancellationToken);
    }

    /// <summary>The usable connection: <see langword="null"/> when there is none or it was revoked.</summary>
    public async Task<CoordinatorConnection?> ActiveAsync(Guid organizationId, CoordinatorService service, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorConnections.AsNoTracking()
            .SingleOrDefaultAsync(
                connection => connection.Service == service && connection.Status == CoordinatorConnectionStatus.Active,
                cancellationToken);
    }

    /// <summary>Whether <paramref name="organizationId"/> has an active connection to <paramref name="service"/>.</summary>
    public async Task<bool> IsActiveAsync(Guid organizationId, CoordinatorService service, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorConnections
            .AnyAsync(connection => connection.Service == service && connection.Status == CoordinatorConnectionStatus.Active, cancellationToken);
    }

    /// <summary>Stores <paramref name="secret"/> (already protected under <see cref="CoordinatorConnection.PurposeOf"/>)
    /// as the active credential of <paramref name="organizationId"/> for <paramref name="service"/>, with one activity.</summary>
    public async Task<CoordinatorConnectionChange> SetAsync(
        Guid organizationId,
        CoordinatorService service,
        ProtectedSecret secret,
        string externalId,
        string displayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        await using var dbContext = ForOrganization(organizationId);
        var now = _clock.GetUtcNow();
        var connection = await dbContext.CoordinatorConnections.SingleOrDefaultAsync(row => row.Service == service, cancellationToken);
        CoordinatorConnectionChange change;
        if (connection is null)
        {
            dbContext.CoordinatorConnections.Add(CoordinatorConnection.Create(organizationId, service, secret, externalId, displayName, now));
            change = CoordinatorConnectionChange.Created;
        }
        else
        {
            connection.Set(secret, externalId, displayName, now);
            change = CoordinatorConnectionChange.Replaced;
        }

        dbContext.OrganizationActivities.Add(OrganizationActivity.CoordinatorConnectionUpdated(organizationId, now, service, cleared: false));
        await dbContext.SaveChangesAsync(cancellationToken);
        return change;
    }

    /// <summary>Removes the credential, with one activity; <see langword="false"/> (and nothing written) when there was
    /// none.</summary>
    public async Task<bool> ClearAsync(Guid organizationId, CoordinatorService service, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var connection = await dbContext.CoordinatorConnections.SingleOrDefaultAsync(row => row.Service == service, cancellationToken);
        if (connection is null)
        {
            return false;
        }

        dbContext.CoordinatorConnections.Remove(connection);
        dbContext.OrganizationActivities.Add(OrganizationActivity.CoordinatorConnectionUpdated(organizationId, _clock.GetUtcNow(), service, cleared: true));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The service refused <paramref name="used"/>'s credential: it becomes <see cref="CoordinatorConnectionStatus.Revoked"/>
    /// — only if it is still the credential that was used (same <see cref="CoordinatorConnection.UpdatedAt"/>) and
    /// active. <see langword="true"/> when this call revoked it.
    /// </summary>
    public async Task<bool> RevokeAsync(CoordinatorConnection used, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(used);
        await using var dbContext = ForOrganization(used.OrganizationId);
        var now = _clock.GetUtcNow();
        return await dbContext.CoordinatorConnections
            .Where(connection => connection.Id == used.Id
                && connection.Status == CoordinatorConnectionStatus.Active
                && connection.UpdatedAt == used.UpdatedAt)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.Status, CoordinatorConnectionStatus.Revoked)
                    .SetProperty(connection => connection.UpdatedAt, now),
                cancellationToken) == 1;
    }

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
