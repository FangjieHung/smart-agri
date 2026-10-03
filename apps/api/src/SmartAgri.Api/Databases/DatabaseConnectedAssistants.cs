using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Databases;

/// <summary>An assistant connected to a database, as its detail page lists it (M4 #148).</summary>
public sealed record DatabaseConnectedAssistantView(Guid Id, string Name, AssistantStatus Status);

/// <summary>
/// Which of the viewer's <b>own</b> assistants are connected to each database (M4 #148), like the
/// mock's <c>connectedAssistants</c>: another account's assistants are never named, so a data
/// manager does not learn which assistants collect into the database.
/// </summary>
public static class DatabaseConnectedAssistants
{
    public static async Task<ILookup<Guid, DatabaseConnectedAssistantView>> ForAsync(
        AppDbContext dbContext, Guid viewerId, IReadOnlyCollection<Guid> databaseIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var rows = await (
                from link in dbContext.AssistantDatabases.AsNoTracking()
                join assistant in dbContext.Assistants.AsNoTracking() on link.AssistantId equals assistant.Id
                where databaseIds.Contains(link.DatabaseId) && assistant.OwnerAccountId == viewerId
                orderby link.ConnectedAt, assistant.Id
                select new { link.DatabaseId, assistant.Id, assistant.Name, assistant.Status })
            .ToListAsync(cancellationToken);
        return rows.ToLookup(row => row.DatabaseId, row => new DatabaseConnectedAssistantView(row.Id, row.Name, row.Status));
    }
}
