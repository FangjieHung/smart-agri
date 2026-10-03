using System.Linq.Expressions;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Databases;

/// <summary>
/// Who may see and manage a database's settings (name, purpose, form). Organization isolation is
/// not decided here: the persistence layer's query filter already limits every query to the
/// caller's organization. These rules decide among the organization's own databases.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the frontend mock (<c>mock-demo-repository.ts</c>): the owner lists, opens and
/// changes their databases (mapping <c>S+OWN</c>). Since #144 a data manager also <b>sees</b>
/// the databases they manage — read-only: <see cref="ListedFor"/> adds the databases where
/// <see cref="DatabaseRecordAccess.ReadableBy"/> holds (designated <b>and</b> holding
/// <c>read-consented-submissions</c>), while <see cref="ManageableBy"/> stays owner-only. A
/// caller who may not even see it gets the same <c>403 database</c> as for an id that does not
/// exist.
/// </para>
/// <para>
/// Reading <b>records</b> is <see cref="DatabaseRecordAccess"/>: owning a database never implies
/// it, and until #146 no response carries a record or subject count.
/// </para>
/// </remarks>
public static class DatabaseAccess
{
    /// <summary>Databases that appear in <paramref name="viewerAccountId"/>'s list and that they
    /// may open (read-only unless <see cref="ManageableBy"/>): their own, plus those they may read
    /// the records of (<see cref="DatabaseRecordAccess.ReadableBy"/>).</summary>
    public static Expression<Func<Database, bool>> ListedFor(
        Guid viewerAccountId, bool viewerHasReadPermission, IQueryable<DatabaseDataManager> designations)
    {
        ArgumentNullException.ThrowIfNull(designations);
        return database =>
            database.OwnerAccountId == viewerAccountId
            || (viewerHasReadPermission
                && designations.Any(designation => designation.DatabaseId == database.Id && designation.AccountId == viewerAccountId));
    }

    /// <summary>Databases <paramref name="viewerAccountId"/> may open and change.</summary>
    public static Expression<Func<Database, bool>> ManageableBy(Guid viewerAccountId) =>
        database => database.OwnerAccountId == viewerAccountId;

    /// <summary><see cref="ManageableBy"/> for one loaded database: the <c>viewerCanManage</c>
    /// flag in API responses, so the frontend never compares owner ids itself.</summary>
    public static bool CanManage(Database database, Guid viewerAccountId)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.OwnerAccountId == viewerAccountId;
    }
}
