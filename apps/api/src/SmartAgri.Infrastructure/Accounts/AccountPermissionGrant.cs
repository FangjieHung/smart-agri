using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Infrastructure.Accounts;

/// <summary>
/// One permission granted to one account (table <c>AccountPermissions</c>). Permissions are
/// read from the database on every request rather than carried in the access token (M1
/// plan §3), so changing them takes effect immediately.
/// </summary>
public class AccountPermissionGrant : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private AccountPermissionGrant()
    {
    }

    public AccountPermissionGrant(Account account, AccountPermission permission)
    {
        ArgumentNullException.ThrowIfNull(account);

        AccountId = account.Id;
        OrganizationId = account.OrganizationId;
        Permission = permission;
    }

    public Guid AccountId { get; private set; }

    public AccountPermission Permission { get; private set; }

    /// <summary>
    /// Always the account's organization: the database enforces it with a composite
    /// foreign key <c>(AccountId, OrganizationId) → Accounts(Id, OrganizationId)</c>.
    /// </summary>
    public Guid OrganizationId { get; private set; }
}
