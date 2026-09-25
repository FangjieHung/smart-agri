using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Shouldly;
using SmartAgri.Api.Tenancy;

namespace SmartAgri.Api.Tests.Tenancy;

public class ClaimsOrganizationContextTests
{
    private static readonly Guid OrganizationId = Guid.NewGuid();

    [Fact]
    public void Authenticated_org_id_claim_is_the_current_organization()
    {
        Resolve(Authenticated(new Claim(OrganizationClaimTypes.OrganizationId, OrganizationId.ToString())))
            .ShouldBe(OrganizationId);
    }

    [Fact]
    public void No_request_means_no_organization()
    {
        new ClaimsOrganizationContext(new HttpContextAccessor()).OrganizationId.ShouldBeNull();
    }

    [Fact]
    public void Unauthenticated_principal_is_ignored_even_with_an_org_id_claim()
    {
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(OrganizationClaimTypes.OrganizationId, OrganizationId.ToString())]));

        Resolve(unauthenticated).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Missing_or_invalid_org_id_means_no_organization(string? value)
    {
        var claims = value is null ? Array.Empty<Claim>() : [new Claim(OrganizationClaimTypes.OrganizationId, value)];

        Resolve(Authenticated(claims)).ShouldBeNull();
    }

    [Fact]
    public void Conflicting_org_id_claims_mean_no_organization()
    {
        Resolve(Authenticated(
                new Claim(OrganizationClaimTypes.OrganizationId, OrganizationId.ToString()),
                new Claim(OrganizationClaimTypes.OrganizationId, Guid.NewGuid().ToString())))
            .ShouldBeNull();
    }

    private static Guid? Resolve(ClaimsPrincipal principal)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        return new ClaimsOrganizationContext(accessor).OrganizationId;
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));
}
