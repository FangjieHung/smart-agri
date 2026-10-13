using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using Shouldly;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Organizations;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAgri.Api.Tests.Authentication;

/// <summary>
/// Refresh tokens on the real OpenIddict pipeline (#354): the admin SPA's sign-in gets a refresh token,
/// every use rotates it, a rotated one presented again revokes the whole chain, 8 hours unused or 7 days
/// since sign-in end it, and so do logout (revocation), a password change and lockout. Its own host:
/// the tests move the host's clock (OpenIddict, Identity and the token endpoint all read the
/// <see cref="TimeProvider"/> from DI), which only ever goes forward, and every test signs in afresh.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class RefreshTokenTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Correct-Horse-9-battery";
    private const string NewPassword = "Another-Horse-8-staple";

    private readonly AuthHostFixture _host;

    public RefreshTokenTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_spa_sign_in_gets_a_refresh_token_that_renews_the_access_token()
    {
        var (_, spa, first) = await SignInAsync();
        using var signedIn = spa;

        var refreshToken = first.RefreshToken.ShouldNotBeNull();
        var renewed = await spa.RefreshOkAsync(refreshToken);

        renewed.AccessToken.ShouldNotBe(first.AccessToken);
        renewed.RefreshToken.ShouldNotBeNull().ShouldNotBe(refreshToken, "every use rotates the refresh token");
        renewed.ExpiresInSeconds.ShouldBeInRange(29 * 60, 30 * 60);
        (await spa.GetMeAsync(renewed.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Like the code exchange's, the refreshed access token carries only sub, org_id and role.
        SpaClient.DecodePayload(renewed.AccessToken).EnumerateObject().Select(claim => claim.Name)
            .ShouldNotContain(name => name == SignInSession.SignedInAtClaim || name == SignInSession.SecurityStampClaim);
    }

    [Fact]
    public async Task A_rotated_refresh_token_presented_again_is_rejected_and_revokes_the_whole_chain()
    {
        var (organization, spa, first) = await SignInAsync();
        using var signedIn = spa;
        var original = first.RefreshToken.ShouldNotBeNull();
        var rotated = (await spa.RefreshOkAsync(original)).RefreshToken.ShouldNotBeNull();

        // Reuse right away: no leeway, treated as theft.
        await ShouldBeRejectedAsync(await spa.RefreshAsync(original));

        // The legitimate holder's newer token went down with it: the chain is revoked.
        await ShouldBeRejectedAsync(await spa.RefreshAsync(rotated));

        // Signing in again starts a new chain.
        using var again = _host.CreateSpaClient();
        var fresh = await again.SignInAsync(organization.Code, "admin", Password, SpaClient.SpaScope);
        await again.RefreshOkAsync(fresh.RefreshToken.ShouldNotBeNull());
    }

    [Fact]
    public async Task A_refresh_token_unused_for_8_hours_is_rejected_but_each_use_slides_the_window()
    {
        var (_, spa, first) = await SignInAsync();
        using var signedIn = spa;

        _host.Clock.Advance(TimeSpan.FromHours(7.9));
        var second = (await spa.RefreshOkAsync(first.RefreshToken!)).RefreshToken!;

        // 15.8 hours after sign-in, but only 7.9 since the last use.
        _host.Clock.Advance(TimeSpan.FromHours(7.9));
        var third = (await spa.RefreshOkAsync(second)).RefreshToken!;

        _host.Clock.Advance(AuthenticationServiceCollectionExtensions.RefreshTokenLifetime + TimeSpan.FromMinutes(1));
        await ShouldBeRejectedAsync(await spa.RefreshAsync(third));
    }

    [Fact]
    public async Task A_sign_in_ends_7_days_after_it_started_however_often_it_is_refreshed()
    {
        var (_, spa, first) = await SignInAsync();
        using var signedIn = spa;
        var refreshToken = first.RefreshToken!;

        // Every 7 hours, well inside the idle limit: 23 refreshes take it to 161 hours.
        for (var refresh = 0; refresh < 23; refresh++)
        {
            _host.Clock.Advance(TimeSpan.FromHours(7));
            refreshToken = (await spa.RefreshOkAsync(refreshToken)).RefreshToken!;
        }

        // 20 minutes before the 7 days are up: still renewed, but the access token ends with the session.
        _host.Clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(40));
        var last = await spa.RefreshOkAsync(refreshToken);
        last.ExpiresInSeconds.ShouldBeLessThanOrEqualTo(20 * 60);

        // Past the 7 days, though only 25 minutes after the last refresh.
        _host.Clock.Advance(TimeSpan.FromMinutes(25));
        await ShouldBeRejectedAsync(await spa.RefreshAsync(last.RefreshToken!));
    }

    [Fact]
    public async Task Logging_out_revokes_the_refresh_token()
    {
        var (_, spa, first) = await SignInAsync();
        using var signedIn = spa;

        var revoke = await spa.RevokeAsync(first.RefreshToken!);

        revoke.StatusCode.ShouldBe(HttpStatusCode.OK, await revoke.Content.ReadAsStringAsync(CancellationToken));
        await ShouldBeRejectedAsync(await spa.RefreshAsync(first.RefreshToken!));
    }

    [Fact]
    public async Task Changing_the_password_ends_every_refresh_token_of_the_account()
    {
        var (organization, spa, first) = await SignInAsync();
        using var signedIn = spa;
        using var otherDevice = _host.CreateSpaClient();
        var other = await otherDevice.SignInAsync(organization.Code, "admin", Password, SpaClient.SpaScope);

        (await spa.ChangePasswordAsync(first.AccessToken, Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldBeRejectedAsync(await spa.RefreshAsync(first.RefreshToken!));
        await ShouldBeRejectedAsync(await otherDevice.RefreshAsync(other.RefreshToken!));
        (await RefreshTokenStatusesAsync(AccountIdOf(first))).ShouldAllBe(status => status == Statuses.Revoked);
    }

    [Fact]
    public async Task A_locked_out_account_cannot_refresh()
    {
        var (organization, spa, first) = await SignInAsync();
        using var signedIn = spa;
        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            var account = await dbContext.Accounts.SingleAsync(candidate => candidate.LoginName == "admin", CancellationToken);
            account.LockoutEnd = DateTimeOffset.MaxValue;
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        await ShouldBeRejectedAsync(await spa.RefreshAsync(first.RefreshToken!));
    }

    [Fact]
    public async Task An_account_that_must_change_its_password_still_refreshes_like_it_signs_in()
    {
        var (_, spa, first) = await SignInAsync(requirePasswordChange: true);
        using var signedIn = spa;

        var renewed = await spa.RefreshOkAsync(first.RefreshToken!);

        // The gate is read per request, not from the token: still only /me and change-password.
        (await spa.GetMeJsonAsync(renewed.AccessToken)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
        (await spa.GetAsync(ProtectedProbeEndpoint.Path, renewed.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>A new organization (the only "admin" in it) signed in the way the admin SPA does.</summary>
    private async Task<(Organization Organization, SpaClient Spa, TokenResponse Token)> SignInAsync(bool requirePasswordChange = false)
    {
        var organization = await _host.CreateOrganizationAsync();
        var account = await _host.CreateAccountAsync(organization, "admin", Password);
        if (requirePasswordChange)
        {
            await _host.RequirePasswordChangeAsync(account);
        }

        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "admin", Password, SpaClient.SpaScope);
        token.RefreshToken.ShouldNotBeNull("the admin SPA asks for offline_access");
        return (organization, spa, token);
    }

    private static async Task ShouldBeRejectedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString().ShouldBe(OpenIddict.Abstractions.OpenIddictConstants.Errors.InvalidGrant, body);
    }

    private async Task<List<string?>> RefreshTokenStatusesAsync(Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        var statuses = await dbContext.Set<OpenIddictEntityFrameworkCoreToken>()
            .Where(token => token.Subject == accountId.ToString("D")
                && (token.Type == TokenTypeIdentifiers.RefreshToken || token.Type == TokenTypeHints.RefreshToken))
            .Select(token => token.Status)
            .ToListAsync(CancellationToken);
        statuses.ShouldNotBeEmpty();
        return statuses;
    }

    private static Guid AccountIdOf(TokenResponse token) =>
        SpaClient.DecodePayload(token.AccessToken).GetProperty("sub").GetGuid();
}
