using System.Linq.Expressions;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Databases;

/// <summary>
/// Who may <b>read the consented records</b> of a database: the single rule behind every read of
/// records, counts or trends (M4 #144; used by #146 and #147). Reading needs two things at the
/// same moment, and owning the database implies neither:
/// <list type="number">
/// <item>the account is a designated data manager of this database (<see cref="DatabaseDataManager"/>);</item>
/// <item>the account currently holds <c>read-consented-submissions</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// Mirrors the frontend's <c>canReadConsentedRecords</c>. Nothing here is remembered: callers pass
/// the permission as read from the database for <b>this</b> request and the designations as a
/// query, so revoking either takes effect on the very next request, with the same access token.
/// Written as an <see cref="Expression{TDelegate}"/> so EF Core translates it and unit tests run
/// it in memory, like <see cref="DatabaseAccess"/>.
/// </remarks>
public static class DatabaseRecordAccess
{
    /// <summary>The decision for one database whose designation is already known.</summary>
    public static bool CanRead(bool isDesignatedDataManager, bool accountHasReadPermission) =>
        isDesignatedDataManager && accountHasReadPermission;

    /// <summary>Databases whose records <paramref name="accountId"/> may read.</summary>
    /// <param name="accountHasReadPermission">Whether the account holds
    /// <c>read-consented-submissions</c> right now.</param>
    /// <param name="designations">The designations (already organization-filtered when they come
    /// from the context).</param>
    public static Expression<Func<Database, bool>> ReadableBy(
        Guid accountId, bool accountHasReadPermission, IQueryable<DatabaseDataManager> designations)
    {
        ArgumentNullException.ThrowIfNull(designations);
        return database =>
            accountHasReadPermission
            && designations.Any(designation => designation.DatabaseId == database.Id && designation.AccountId == accountId);
    }
}
