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
/// every use rotates it, a rotated one presented again after 30 seconds revokes the whole chain (#360), 8
/// hours unused or 7 days since sign-in end it, and so do logout (revocation) and lockout; a password
/// change ends every other sign-in of the account but keeps the one that made it (#360). Its own host:
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
    public async Task A_rotated_refresh_token_may_be_used_again_within_30_seconds()
    {
        var (_, spa, first) = await SignInAsync();
        using var signedIn = spa;
        var original = first.RefreshToken.ShouldNotBeNull();
        var rotated = (await spa.RefreshOkAsync(original)).RefreshToken.ShouldNotBeNull();

        // A retry after a lost response, or a duplicated tab sharing sessionStorage (#360).
        _host.Clock.Advance(AuthenticationServiceCollectionExtensions.RefreshTokenReuseLeeway - TimeSpan.FromSeconds(5));
        var retried = await spa.RefreshOkAsync(original);

        (await spa.GetMeAsync(retried.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await spa.RefreshOkAsync(rotated);
        await spa.RefreshOkAsync(retried.RefreshToken.ShouldNotBeNull());
    }

    [Fact]
    public async Task A_rotated_refresh_token_presented_again_after_30_seconds_is_rejected_and_revokes_the_whole_chain()
    {
        var (organization, spa, first) = await SignInAsync();
        using var signedIn = spa;
        var original = first.RefreshToken.ShouldNotBeNull();
        var rotated = (await spa.RefreshOkAsync(original)).RefreshToken.ShouldNotBeNull();

        // Past the leeway: treated as theft.
        _host.Clock.Advance(AuthenticationServiceCollectionExtensions.RefreshTokenReuseLeeway + TimeSpan.FromSeconds(1));
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
    public async Task Changing_the_password_keeps_this_sign_in_and_ends_every_other_one()
    {
        var (organization, spa, first) = await SignInAsync();
        using var signedIn = spa;
        using var otherDevice = _host.CreateSpaClient();
        var other = await otherDevice.SignInAsync(organization.Code, "admin", Password, SpaClient.SpaScope);
        (await otherDevice.GetMeAsync(other.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await spa.ChangePasswordAsync(first.AccessToken, Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // This device: the access token in hand still works, and the refresh token issued before the change
        // (it carries the old security stamp) still renews it — and so does every one after it.
        (await spa.GetMeAsync(first.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var renewed = await spa.RefreshOkAsync(first.RefreshToken!);
        (await spa.GetAsync(ProtectedProbeEndpoint.Path, renewed.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = await spa.RefreshOkAsync(renewed.RefreshToken!);
        (await spa.GetMeAsync(again.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The other device: refused on its next request and at the token endpoint.
        var otherMe = await otherDevice.GetMeAsync(other.AccessToken);
        otherMe.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await otherMe.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
        await ShouldBeRejectedAsync(await otherDevice.RefreshAsync(other.RefreshToken!));

        var accountId = AccountIdOf(first);
        var authorizations = await AuthorizationStatusesAsync(accountId);
        authorizations.Count.ShouldBe(2);
        authorizations[AuthorizationIdOf(first)].ShouldBe(Statuses.Valid);
        authorizations[AuthorizationIdOf(other)].ShouldBe(Statuses.Revoked);
        (await RefreshTokenStatusesAsync(accountId, AuthorizationIdOf(other))).ShouldAllBe(status => status == Statuses.Revoked);

        // A second change from the same device keeps it signed in again.
        (await spa.ChangePasswordAsync(again.AccessToken, NewPassword, Password)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await spa.RefreshOkAsync(again.RefreshToken!);
    }

    [Fact]
    public async Task The_sign_in_that_changed_the_password_still_ends_7_days_after_it_started()
    {
        var (_, spa, first) = await SignInAsync();
        using var signedIn = spa;

        // Inside the access token's 30 minutes.
        _host.Clock.Advance(TimeSpan.FromMinutes(20));
        (await spa.ChangePasswordAsync(first.AccessToken, Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var refreshToken = (await spa.RefreshOkAsync(first.RefreshToken!)).RefreshToken!;

        // Refreshed every 7 hours: 161 hours 20 minutes after the sign-in.
        for (var refresh = 0; refresh < 23; refresh++)
        {
            _host.Clock.Advance(TimeSpan.FromHours(7));
            refreshToken = (await spa.RefreshOkAsync(refreshToken)).RefreshToken!;
        }

        // 20 minutes before 7 days since the sign-in (not since the change): the access token ends with it.
        _host.Clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(20));
        var last = await spa.RefreshOkAsync(refreshToken);
        last.ExpiresInSeconds.ShouldBeLessThanOrEqualTo(20 * 60);

        _host.Clock.Advance(TimeSpan.FromMinutes(25));
        await ShouldBeRejectedAsync(await spa.RefreshAsync(last.RefreshToken!));
    }

    [Fact]
    public async Task Another_password_change_or_reset_still_ends_the_sign_in_that_changed_it_first()
    {
        var (organization, spa, first) = await SignInAsync();
        using var signedIn = spa;
        (await spa.ChangePasswordAsync(first.AccessToken, Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A later sign-in changes the password again: the first device's re-stamped sign-in ends.
        using var otherDevice = _host.CreateSpaClient();
        var other = await otherDevice.SignInAsync(organization.Code, "admin", NewPassword, SpaClient.SpaScope);
        (await otherDevice.ChangePasswordAsync(other.AccessToken, NewPassword, Password)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldBeRejectedAsync(await spa.RefreshAsync(first.RefreshToken!));
        (await spa.GetMeAsync(first.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await otherDevice.RefreshOkAsync(other.RefreshToken!);
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

    [Fact]
    public async Task The_first_sign_in_password_change_ends_still_signed_in()
    {
        var (_, spa, first) = await SignInAsync(requirePasswordChange: true);
        using var signedIn = spa;

        (await spa.ChangePasswordAsync(first.AccessToken, Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // No new sign-in: the same tokens go on, now ungated.
        (await spa.GetMeJsonAsync(first.AccessToken)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeFalse();
        var renewed = await spa.RefreshOkAsync(first.RefreshToken!);
        (await spa.GetAsync(ProtectedProbeEndpoint.Path, renewed.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
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

    private async Task<List<string?>> RefreshTokenStatusesAsync(Guid accountId, string authorizationId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        var statuses = await dbContext.Set<OpenIddictEntityFrameworkCoreToken>()
            .Where(token => token.Subject == accountId.ToString("D")
                && token.Authorization!.Id == authorizationId
                && (token.Type == TokenTypeIdentifiers.RefreshToken || token.Type == TokenTypeHints.RefreshToken))
            .Select(token => token.Status)
            .ToListAsync(CancellationToken);
        statuses.ShouldNotBeEmpty();
        return statuses;
    }

    private async Task<Dictionary<string, string?>> AuthorizationStatusesAsync(Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        return await dbContext.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .Where(authorization => authorization.Subject == accountId.ToString("D"))
            .ToDictionaryAsync(authorization => authorization.Id!, authorization => authorization.Status, CancellationToken);
    }

    /// <summary>The sign-in (OpenIddict authorization) the access token names.</summary>
    private static string AuthorizationIdOf(TokenResponse token) =>
        SpaClient.DecodePayload(token.AccessToken).GetProperty(Claims.Private.AuthorizationId).GetString().ShouldNotBeNull();

    private static Guid AccountIdOf(TokenResponse token) =>
        SpaClient.DecodePayload(token.AccessToken).GetProperty("sub").GetGuid();
}
