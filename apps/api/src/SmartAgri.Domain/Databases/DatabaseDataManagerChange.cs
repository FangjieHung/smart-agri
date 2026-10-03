using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Databases;

/// <summary>
/// One designation or removal of a data manager, appended and never changed (M4 #144: "指定變更
/// 留存操作人與時間"). <see cref="DatabaseDataManager"/> only holds the current state, so a
/// removal would otherwise leave no trace of who removed whom and when. A single save that
/// changes several accounts writes one row each, all with the same <see cref="ChangedAt"/>.
/// </summary>
/// <remarks>
/// The account ids are audit values with no foreign key, so the history survives the accounts.
/// </remarks>
public sealed class DatabaseDataManagerChange : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private DatabaseDataManagerChange()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid DatabaseId { get; private set; }

    /// <summary>The account that was designated or removed.</summary>
    public Guid AccountId { get; private set; }

    /// <summary><see langword="true"/> for a designation, <see langword="false"/> for a removal.</summary>
    public bool Assigned { get; private set; }

    /// <summary>The owner who made the change.</summary>
    public Guid ChangedByAccountId { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public static DatabaseDataManagerChange Create(
        Database database,
        Guid accountId,
        bool assigned,
        Guid changedByAccountId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(accountId));
        }

        if (changedByAccountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(changedByAccountId));
        }

        return new DatabaseDataManagerChange
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = database.OrganizationId,
            DatabaseId = database.Id,
            AccountId = accountId,
            Assigned = assigned,
            ChangedByAccountId = changedByAccountId,
            ChangedAt = now,
        };
    }
}
