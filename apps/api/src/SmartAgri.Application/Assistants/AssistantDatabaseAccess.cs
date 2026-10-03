using System.Linq.Expressions;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Assistants;

/// <summary>
/// Which databases an assistant's owner may connect to it, and keep using through it (M4 #148):
/// the databases the owner may open (<see cref="DatabaseAccess.ListedFor"/>) — their own, plus
/// those they are a designated data manager of while holding <c>read-consented-submissions</c>.
/// This is <see cref="AssistantKnowledgeAccess"/>'s counterpart for databases; a data-manager
/// designation is how a database is "shared" with another account.
/// </summary>
/// <remarks>
/// The same rule is evaluated three times, never cached: when the owner connects a database, when
/// the assistant offers a form in a conversation, and when a member submits it. Removing the
/// designation or the permission (or deleting the database) therefore stops new form requests and
/// submissions on the very next request, although the connection row itself stays until the owner
/// disconnects it.
/// </remarks>
public static class AssistantDatabaseAccess
{
    public static Expression<Func<Database, bool>> ConnectableBy(
        Guid ownerAccountId, bool ownerHasReadPermission, IQueryable<DatabaseDataManager> designations) =>
        DatabaseAccess.ListedFor(ownerAccountId, ownerHasReadPermission, designations);
}
