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
/// Ported from the frontend mock (<c>mock-demo-repository.ts</c>): <c>listDatabaseSummaries</c>
/// keeps only databases whose owner is the viewer, and <c>ownedDatabase</c> (detail and every
/// change) allows only the owner — mapping <c>S+OWN</c>. A caller who is not the owner gets the
/// same <c>403 database</c> as for an id that does not exist.
/// </para>
/// <para>
/// Reading <b>submitted records</b> is a separate rule (data-manager designation and the
/// <c>read-consented-submissions</c> permission, #144/#146): owning a database never implies it.
/// Until then no response carries a record or subject count. When data managers can list the
/// databases they manage, <see cref="ListedFor"/> widens while <see cref="ManageableBy"/> stays
/// owner-only.
/// </para>
/// </remarks>
public static class DatabaseAccess
{
    /// <summary>Databases that appear in <paramref name="viewerAccountId"/>'s list.</summary>
    public static Expression<Func<Database, bool>> ListedFor(Guid viewerAccountId) =>
        database => database.OwnerAccountId == viewerAccountId;

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
