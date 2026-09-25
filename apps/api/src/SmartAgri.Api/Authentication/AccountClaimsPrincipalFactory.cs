using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Authentication;

/// <summary>
/// Builds the principal stored in the Identity sign-in cookie: Identity's defaults plus the
/// account's <c>org_id</c>. The cookie is only read by <c>/connect/authorize</c> and
/// <c>/connect/endsession</c>; it needs the organization so the account (an
/// organization-scoped row) can be loaded again.
/// </summary>
public sealed class AccountClaimsPrincipalFactory : UserClaimsPrincipalFactory<Account>
{
    public AccountClaimsPrincipalFactory(UserManager<Account> userManager, IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, optionsAccessor)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(Account user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AccountClaims.OrganizationId, user.OrganizationId.ToString("D")));
        return identity;
    }
}
