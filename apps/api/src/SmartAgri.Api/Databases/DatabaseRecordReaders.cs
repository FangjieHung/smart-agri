using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authorization;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Accounts;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Databases;

/// <summary>
/// The one place endpoints ask "may this account read the consented records of this database?"
/// (M4 #144; #146 and #147 call it before returning any record, count or trend). It evaluates
/// <see cref="DatabaseRecordAccess"/> against the database as it is for <b>this request</b>: the
/// account's permission comes from <see cref="RequestAccountPermissions"/> (read from the store
/// per request, never from the token) and the designation from <c>DatabaseDataManagers</c>, so
/// revoking either takes effect on the next request.
/// </summary>
public static class DatabaseRecordReaders
{
    /// <summary>Whether <paramref name="accountId"/> holds <c>read-consented-submissions</c> now.</summary>
    public static async Task<bool> HasReadPermissionAsync(
        RequestAccountPermissions permissions, Guid accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return (await permissions.GetAsync(accountId, cancellationToken)).Contains(AccountPermission.ReadConsentedSubmissions);
    }

    /// <summary>Whether the account may read the records of <paramref name="databaseId"/> (a
    /// database of another organization or that does not exist is simply <see langword="false"/>).</summary>
    public static async Task<bool> CanReadAsync(
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        Guid accountId,
        Guid databaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var hasPermission = await HasReadPermissionAsync(permissions, accountId, cancellationToken);
        return hasPermission
            && await dbContext.Databases
                .Where(database => database.Id == databaseId)
                .Where(DatabaseRecordAccess.ReadableBy(accountId, hasPermission, dbContext.DatabaseDataManagers))
                .AnyAsync(cancellationToken);
    }

    /// <summary>The databases whose records the account may read.</summary>
    public static async Task<IReadOnlyList<Guid>> ReadableDatabaseIdsAsync(
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var hasPermission = await HasReadPermissionAsync(permissions, accountId, cancellationToken);
        if (!hasPermission)
        {
            return [];
        }

        return await dbContext.Databases
            .AsNoTracking()
            .Where(DatabaseRecordAccess.ReadableBy(accountId, hasPermission, dbContext.DatabaseDataManagers))
            .Select(database => database.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The accounts that may read the records of <paramref name="databaseId"/> right
    /// now: designated <b>and</b> currently holding the permission (the owner's "who can actually
    /// read" preview).</summary>
    public static async Task<IReadOnlySet<Guid>> EffectiveReaderIdsAsync(
        AppDbContext dbContext, Guid databaseId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var ids = await (
            from designation in dbContext.DatabaseDataManagers.AsNoTracking()
            where designation.DatabaseId == databaseId
            join grant in dbContext.AccountPermissions.AsNoTracking() on designation.AccountId equals grant.AccountId
            where grant.Permission == AccountPermission.ReadConsentedSubmissions
            select designation.AccountId).ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }
}
