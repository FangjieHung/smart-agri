using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Shouldly;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Tenancy;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAgri.Api.Tests.Authentication;

/// <summary>The admin-spa client descriptor and organization pinning during sign-in. No
/// database needed.</summary>
public class AdminSpaClientTests
{
    [Fact]
    public void Admin_spa_is_a_public_pkce_client_limited_to_the_code_flow()
    {
        var descriptor = AdminSpaClientRegistrar.Describe(["http://localhost:4200"]);

        descriptor.ClientId.ShouldBe("admin-spa");
        descriptor.ClientType.ShouldBe(ClientTypes.Public);
        descriptor.ClientSecret.ShouldBeNull();
        descriptor.Requirements.ShouldBe([Requirements.Features.ProofKeyForCodeExchange]);
        descriptor.Permissions.ShouldBe(
            [
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
            ],
            ignoreOrder: true);
        descriptor.RedirectUris.ShouldBe([new Uri("http://localhost:4200/auth/callback")]);
        descriptor.PostLogoutRedirectUris.ShouldBe([new Uri("http://localhost:4200/login")]);
    }

    [Theory]
    [InlineData("localhost:4200")]
    [InlineData("ftp://example.org")]
    [InlineData("https://example.org/admin")]
    [InlineData("https://example.org/?x=1")]
    public void Origins_must_be_bare_http_origins(string origin)
    {
        Should.Throw<InvalidOperationException>(() => AdminSpaClientRegistrar.Describe([origin]));
    }

    [Fact]
    public void Pinning_sets_the_organization_for_an_anonymous_request()
    {
        var organizationId = Guid.NewGuid();
        var context = new ClaimsOrganizationContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        context.OrganizationId.ShouldBeNull();
        context.Pin(organizationId);
        context.OrganizationId.ShouldBe(organizationId);

        // Pinning the same organization again is harmless.
        context.Pin(organizationId);
        context.OrganizationId.ShouldBe(organizationId);
    }

    [Fact]
    public void Pinning_refuses_to_switch_to_another_organization()
    {
        var context = new ClaimsOrganizationContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        context.Pin(Guid.NewGuid());

        Should.Throw<InvalidOperationException>(() => context.Pin(Guid.NewGuid()));
    }

    [Fact]
    public void Pinning_refuses_to_contradict_the_authenticated_principal()
    {
        var tokenOrganization = Guid.NewGuid();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(OrganizationClaimTypes.OrganizationId, tokenOrganization.ToString("D"))],
                authenticationType: "test")),
        };
        var context = new ClaimsOrganizationContext(new HttpContextAccessor { HttpContext = httpContext });

        Should.Throw<InvalidOperationException>(() => context.Pin(Guid.NewGuid()));
        context.OrganizationId.ShouldBe(tokenOrganization);
    }
}
