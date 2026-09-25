using OpenIddict.Abstractions;
using Shouldly;
using SmartAgri.Api.Authentication;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAgri.Api.Tests.Authentication;

/// <summary>
/// What goes into the tokens (M1 plan §3): the access token carries exactly <c>sub</c>,
/// <c>org_id</c> and <c>role</c>; permissions never. No database needed.
/// </summary>
public class TokenContentTests
{
    private static readonly Organization Organization = new(Guid.NewGuid(), "安心商行", "anxin");

    [Fact]
    public void Access_token_claims_are_exactly_sub_org_id_and_role()
    {
        var account = Account.Create(Organization, "admin", "安心商行管理者", AccountRole.SmbAdmin);

        var principal = ConnectEndpoints.CreatePrincipal(account, [Scopes.OpenId]);

        var accessTokenClaims = principal.Claims
            .Where(claim => claim.GetDestinations().Contains(Destinations.AccessToken))
            .ToDictionary(claim => claim.Type, claim => claim.Value);
        accessTokenClaims.Keys.ShouldBe(["sub", "org_id", "role"], ignoreOrder: true);
        accessTokenClaims["sub"].ShouldBe(account.Id.ToString("D"));
        accessTokenClaims["org_id"].ShouldBe(Organization.Id.ToString("D"));
        accessTokenClaims["role"].ShouldBe("smb-admin");
    }

    [Fact]
    public void Identity_token_carries_only_the_subject()
    {
        var account = Account.Create(Organization, "customer", "外部客戶", AccountRole.ExternalCustomer);

        var principal = ConnectEndpoints.CreatePrincipal(account, [Scopes.OpenId]);

        principal.Claims
            .Where(claim => claim.GetDestinations().Contains(Destinations.IdentityToken))
            .Select(claim => claim.Type)
            .ShouldBe(["sub"]);
    }

    [Fact]
    public void No_claim_mentions_permissions()
    {
        var account = Account.Create(Organization, "admin", "管理者", AccountRole.SmbAdmin);

        var principal = ConnectEndpoints.CreatePrincipal(account, [Scopes.OpenId]);

        principal.Claims.ShouldNotContain(claim => claim.Type.Contains("permission", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_the_openid_scope_is_granted_so_no_refresh_token_can_be_issued()
    {
        var account = Account.Create(Organization, "admin", "管理者", AccountRole.SmbAdmin);

        var principal = ConnectEndpoints.CreatePrincipal(account, [Scopes.OpenId, Scopes.OfflineAccess, "profile"]);

        principal.GetScopes().ShouldBe([Scopes.OpenId]);
    }

    [Fact]
    public void New_accounts_have_lockout_enabled()
    {
        // Accounts inserted directly (seeding, setup, tests) must still lock out.
        Account.Create(Organization, "admin", "管理者", AccountRole.SmbAdmin).LockoutEnabled.ShouldBeTrue();
    }
}
