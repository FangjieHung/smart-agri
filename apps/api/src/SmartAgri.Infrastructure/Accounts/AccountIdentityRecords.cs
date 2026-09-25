using Microsoft.AspNetCore.Identity;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Infrastructure.Accounts;

// Identity's per-user tables. They hold data about one account, so they belong to that
// account's organization and get the same query filter and write guard as every other
// organization-scoped row. Identity's stores create these with object initializers that
// never set OrganizationId; the save interceptor fills it in from the current
// organization.

/// <summary>A claim attached to an <see cref="Account"/> (<c>AspNetUserClaims</c>).</summary>
public class AccountClaim : IdentityUserClaim<Guid>, IOrganizationScoped
{
    public Guid OrganizationId { get; private set; }
}

/// <summary>An external login linked to an <see cref="Account"/> (<c>AspNetUserLogins</c>).</summary>
public class AccountLogin : IdentityUserLogin<Guid>, IOrganizationScoped
{
    public Guid OrganizationId { get; private set; }
}

/// <summary>An authentication token stored for an <see cref="Account"/> (<c>AspNetUserTokens</c>).</summary>
public class AccountToken : IdentityUserToken<Guid>, IOrganizationScoped
{
    public Guid OrganizationId { get; private set; }
}
