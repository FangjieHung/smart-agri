using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using Shouldly;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAgri.Api.Tests.Authentication;

/// <summary>
/// How sign-in and token issuance are configured, and the <c>401</c> rule for API calls
/// without a valid token. No database needed: these requests are rejected before any
/// query, and options are read from the container.
/// </summary>
public class AuthenticationConfigurationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthenticationConfigurationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString));
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Me_without_a_token_is_401_with_no_body()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/me", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl")]
    public async Task Me_with_an_invalid_token_is_401_with_no_body(string token)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Me_does_not_accept_the_sign_in_cookie_in_place_of_a_token()
    {
        // Even a request carrying some sign-in cookie is judged by bearer token only.
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Add("Cookie", "smartagri.signin=forged");

        var response = await client.SendAsync(request, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_is_anonymous_and_idempotent()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/v1/auth/logout", content: null, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public void Identity_allows_slash_in_user_names_and_locks_out_after_five_failures()
    {
        var identity = _factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;

        identity.User.AllowedUserNameCharacters.ShouldContain('/');
        identity.Lockout.AllowedForNewUsers.ShouldBeTrue();
        identity.Lockout.MaxFailedAccessAttempts.ShouldBe(5);
        identity.Lockout.DefaultLockoutTimeSpan.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void The_sign_in_cookie_is_http_only_same_site_strict_and_not_sliding()
    {
        var cookie = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        cookie.Cookie.HttpOnly.ShouldBeTrue();
        cookie.Cookie.SameSite.ShouldBe(SameSiteMode.Strict);
        cookie.SlidingExpiration.ShouldBeFalse();
        cookie.ExpireTimeSpan.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Only_the_authorization_code_flow_with_pkce_is_enabled()
    {
        var server = _factory.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;

        server.GrantTypes.ShouldBe([GrantTypes.AuthorizationCode]);
        server.ResponseTypes.ShouldBe([ResponseTypes.Code]);
        server.RequireProofKeyForCodeExchange.ShouldBeTrue();
        server.AccessTokenLifetime.ShouldBe(TimeSpan.FromMinutes(30));
        server.DisableAccessTokenEncryption.ShouldBeTrue();

        server.AuthorizationEndpointUris.ShouldHaveSingleItem().OriginalString.ShouldBe("connect/authorize");
        server.TokenEndpointUris.ShouldHaveSingleItem().OriginalString.ShouldBe("connect/token");
        server.EndSessionEndpointUris.ShouldHaveSingleItem().OriginalString.ShouldBe("connect/endsession");
        server.UserInfoEndpointUris.ShouldHaveSingleItem().OriginalString.ShouldBe("connect/userinfo");
    }
}
