using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Databases;

/// <summary>
/// One account designated as a data manager (資料管理者) of one database (M4 #144): the
/// database-level half of "who may read consented records". The other half is the account's own
/// <c>read-consented-submissions</c> permission; <b>both</b> must hold at the moment of every
/// read (<c>SmartAgri.Application.Databases.DatabaseRecordAccess</c>), so neither is cached
/// anywhere. Removing a designation deletes the row (the history stays in
/// <see cref="DatabaseDataManagerChange"/>) and never touches any record.
/// </summary>
/// <remarks>
/// The database enforces, by composite foreign keys, that the account and the database belong
/// to <see cref="OrganizationId"/>. A plain POCO; EF mapping is in
/// <c>SmartAgri.Infrastructure.Databases</c>.
/// </remarks>
public sealed class DatabaseDataManager : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private DatabaseDataManager()
    {
    }

    public Guid DatabaseId { get; private set; }

    public Guid AccountId { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>Who designated the account. An audit value: deliberately no foreign key, so it
    /// outlives that account.</summary>
    public Guid AssignedByAccountId { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    public static DatabaseDataManager Create(Database database, Guid accountId, Guid assignedByAccountId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(accountId));
        }

        if (assignedByAccountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(assignedByAccountId));
        }

        return new DatabaseDataManager
        {
            DatabaseId = database.Id,
            AccountId = accountId,
            OrganizationId = database.OrganizationId,
            AssignedByAccountId = assignedByAccountId,
            AssignedAt = now,
        };
    }
}
