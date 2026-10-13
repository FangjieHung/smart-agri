using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Shouldly;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Setup;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Setup;

/// <summary>
/// <c>reset-password</c> against real PostgreSQL and real sign-ins (#340): the old password stops
/// working, the one-time password signs in gated by "must change password", lockout is cleared, the
/// account's earlier sign-ins end as far as this Api's tokens allow (see <see cref="EfPasswordResetStore"/>),
/// and nothing else — in particular not the same login name in another organization — changes.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class ResetPasswordTests : IClassFixture<AuthHostFixture>
{
    private const string OldPassword = "Old-Shop-Password-1!";
    private const string ChosenPassword = "Chosen-After-Reset-2!";

    private readonly AuthHostFixture _host;

    public ResetPasswordTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_old_password_stops_working_and_the_one_time_password_signs_in_and_must_be_changed()
    {
        var (organization, account) = await CreateAdminAsync();
        using (var before = _host.CreateSpaClient())
        {
            (await before.LoginAsync(organization.Code, "admin", OldPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var telemetry = new TelemetryCapture();
        using var factory = _host.Factory.WithWebHostBuilder(telemetry.Attach);
        var console = new FakeSetupConsole(interactive: false);
        var exitCode = await ResetPasswordCommand.RunAsync(
            factory.Services, ["--organization", organization.Code.ToUpperInvariant(), "--login", "ADMIN"], console, CancellationToken);
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

        exitCode.ShouldBe(ResetPasswordCommand.ExitSuccess, console.ErrorWriter.ToString());
        var password = console.PrintedPassword.ShouldNotBeNull();
        password.Length.ShouldBe(OneTimePasswordGenerator.Length);
        console.OutWriter.ToString().ShouldContain($"組織代碼 {organization.Code}");
        console.ErrorWriter.ToString().ShouldBeEmpty();

        // Logged (who, which organization) — but the password reaches no log, OpenTelemetry log or span.
        telemetry.Logs.ShouldContain(entry =>
            entry.Contains($"reset-password gave account {account.Id} of organization {organization.Code}", StringComparison.Ordinal));
        telemetry.Spans.ShouldContain(span => span.StartsWith("Npgsql", StringComparison.Ordinal));
        telemetry.All.ShouldNotContain(entry => entry.Contains(password, StringComparison.Ordinal));

        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            var stored = await dbContext.Accounts.SingleAsync(candidate => candidate.Id == account.Id, CancellationToken);
            stored.PasswordChangeRequired.ShouldBeTrue();
            stored.PasswordHash.ShouldNotBeNull().ShouldNotContain(password);
            stored.SecurityStamp.ShouldNotBe(account.SecurityStamp);
        }

        using var spa = _host.CreateSpaClient();
        (await spa.LoginAsync(organization.Code, "admin", OldPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var token = await spa.SignInAsync(organization.Code, "admin", password);
        (await spa.GetMeJsonAsync(token.AccessToken)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
        (await spa.GetAsync(ProtectedProbeEndpoint.Path, token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await spa.ChangePasswordAsync(token.AccessToken, password, ChosenPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await spa.GetAsync(ProtectedProbeEndpoint.Path, token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var fresh = _host.CreateSpaClient();
        (await fresh.LoginAsync(organization.Code, "admin", password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await fresh.SignInAsync(organization.Code, "admin", ChosenPassword);
    }

    [Fact]
    public async Task A_locked_out_account_is_unlocked()
    {
        var (organization, account) = await CreateAdminAsync();
        using var spa = _host.CreateSpaClient();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            (await spa.LoginAsync(organization.Code, "admin", $"Wrong-Password-{attempt}!")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Locked: even the right password is refused.
        (await spa.LoginAsync(organization.Code, "admin", OldPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using (var locked = _host.Postgres.CreateDbContext(organization.Id))
        {
            (await locked.Accounts.SingleAsync(candidate => candidate.Id == account.Id, CancellationToken))
                .LockoutEnd.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        }

        var password = await ResetAsync(organization.Code, "admin");

        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            var stored = await dbContext.Accounts.SingleAsync(candidate => candidate.Id == account.Id, CancellationToken);
            stored.LockoutEnd.ShouldBeNull();
            stored.AccessFailedCount.ShouldBe(0);
            stored.LockoutEnabled.ShouldBeTrue();
        }

        using var after = _host.CreateSpaClient();
        var token = await after.SignInAsync(organization.Code, "admin", password);
        (await after.GetMeJsonAsync(token.AccessToken)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task The_accounts_earlier_sign_ins_end_as_far_as_its_tokens_allow()
    {
        var (organization, account) = await CreateAdminAsync();

        // A finished sign-in (cookie + access token + refresh token, as the admin SPA asks) and one stopped
        // between authorize and redeem.
        using var signedIn = _host.CreateSpaClient();
        var token = await signedIn.SignInAsync(organization.Code, "admin", OldPassword, SpaClient.SpaScope);
        var refreshToken = token.RefreshToken.ShouldNotBeNull();
        (await signedIn.GetAsync(ProtectedProbeEndpoint.Path, token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var midway = _host.CreateSpaClient();
        (await midway.LoginAsync(organization.Code, "admin", OldPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var verifier = SpaClient.NewCodeVerifier();
        var authorize = await midway.AuthorizeAsync(verifier);
        authorize.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location.ShouldNotBeNull().Query)["code"].ToString();
        code.ShouldNotBeNullOrEmpty();

        await ResetAsync(organization.Code, "admin");

        // The sign-in cookie can no longer mint codes (security stamp, checked on every /connect/* read).
        var again = await signedIn.AuthorizeAsync(SpaClient.NewCodeVerifier());
        again.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        again.Headers.Location.ShouldNotBeNull().ToString().ShouldStartWith("/login?returnUrl=");

        // A code issued before the reset is no longer redeemable (its token entry is revoked).
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = AuthHostFixture.RedirectUri,
            ["client_id"] = AdminSpaClientOptions.ClientId,
            ["code_verifier"] = verifier,
        });
        var redeem = await midway.Http.PostAsync("/connect/token", form, CancellationToken);
        redeem.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await redeem.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("invalid_grant");

        // The refresh token is revoked too (RevokeBySubjectAsync covers refresh token entries, #354), so the
        // sign-in cannot outlive its access token.
        await using (var dbContext = _host.Postgres.CreateDbContext())
        {
            var subject = account.Id.ToString("D");
            (await dbContext.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>()
                    .Where(entry => entry.Subject == subject
                        && (entry.Type == OpenIddict.Abstractions.OpenIddictConstants.TokenTypeIdentifiers.RefreshToken
                            || entry.Type == OpenIddict.Abstractions.OpenIddictConstants.TokenTypeHints.RefreshToken))
                    .Select(entry => entry.Status)
                    .ToListAsync(CancellationToken))
                .ShouldHaveSingleItem().ShouldBe(OpenIddict.Abstractions.OpenIddictConstants.Statuses.Revoked);
        }

        var refresh = await signedIn.RefreshAsync(refreshToken);
        refresh.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refresh.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("invalid_grant");

        // The access token in hand is a self-contained JWT, so it still authenticates until it expires
        // (at most AccessTokenLifetime) — but the account must change its password now, which is read
        // per request: everything but /me and change-password (which needs the new password) is refused.
        (await signedIn.GetAsync(ProtectedProbeEndpoint.Path, token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await signedIn.GetMeJsonAsync(token.AccessToken)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
        (await signedIn.ChangePasswordAsync(token.AccessToken, OldPassword, ChosenPassword))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        SmartAgri.Api.Authentication.AuthenticationServiceCollectionExtensions.AccessTokenLifetime.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task The_same_login_name_in_another_organization_is_untouched()
    {
        var (target, _) = await CreateAdminAsync("要重設的店");
        var (other, otherAccount) = await CreateAdminAsync("隔壁的店");
        using var otherSpa = _host.CreateSpaClient();
        var otherToken = await otherSpa.SignInAsync(other.Code, "admin", OldPassword);
        var before = await SnapshotAsync(other.Id, otherAccount.Id);

        await ResetAsync(target.Code, "admin");

        (await SnapshotAsync(other.Id, otherAccount.Id)).ShouldBe(before);
        (await otherSpa.GetAsync(ProtectedProbeEndpoint.Path, otherToken.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await otherSpa.AuthorizeAsync(SpaClient.NewCodeVerifier())).Headers.Location.ShouldNotBeNull()
            .GetLeftPart(UriPartial.Path).ShouldBe(AuthHostFixture.RedirectUri);
        using var fresh = _host.CreateSpaClient();
        var token = await fresh.SignInAsync(other.Code, "admin", OldPassword);
        (await fresh.GetMeJsonAsync(token.AccessToken)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeFalse();

        // Whereas the target organization's admin was reset.
        (await fresh.LoginAsync(target.Code, "admin", OldPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Other_accounts_of_the_same_organization_are_untouched()
    {
        var (organization, _) = await CreateAdminAsync();
        var clerk = await _host.CreateAccountAsync(organization, "clerk", OldPassword, AccountRole.InternalEmployee);
        var before = await SnapshotAsync(organization.Id, clerk.Id);

        await ResetAsync(organization.Code, "admin");

        (await SnapshotAsync(organization.Id, clerk.Id)).ShouldBe(before);
        using var spa = _host.CreateSpaClient();
        (await spa.LoginAsync(organization.Code, "clerk", OldPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("no-such-shop", "admin", "找不到組織代碼「no-such-shop」")]
    [InlineData("Bad_Code!", "admin", "找不到組織代碼「Bad_Code!」")]
    [InlineData(null, "ghost", "裡沒有登入名稱為「ghost」的帳號")]
    public async Task An_unknown_organization_or_login_exits_1_and_changes_nothing(string? organizationCode, string login, string message)
    {
        var (organization, account) = await CreateAdminAsync();
        var before = await SnapshotAsync(organization.Id, account.Id);
        var console = new FakeSetupConsole(interactive: false);

        var exitCode = await ResetPasswordCommand.RunAsync(
            _host.Factory.Services, ["--organization", organizationCode ?? organization.Code, "--login", login], console, CancellationToken);

        exitCode.ShouldBe(ResetPasswordCommand.ExitRefused);
        console.ErrorWriter.ToString().ShouldContain(message);
        console.ErrorWriter.ToString().ShouldContain("沒有做任何變更");
        console.PrintedPassword.ShouldBeNull();
        (await SnapshotAsync(organization.Id, account.Id)).ShouldBe(before);
        using var spa = _host.CreateSpaClient();
        (await spa.LoginAsync(organization.Code, "admin", OldPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_login_name_that_exists_only_in_another_organization_is_not_found()
    {
        var (target, _) = await CreateAdminAsync();
        var (other, otherAccount) = await CreateAdminAsync();
        await _host.CreateAccountAsync(other, "only-there", OldPassword, AccountRole.InternalEmployee);
        var console = new FakeSetupConsole(interactive: false);

        (await ResetPasswordCommand.RunAsync(
            _host.Factory.Services, ["--organization", target.Code, "--login", "only-there"], console, CancellationToken))
            .ShouldBe(ResetPasswordCommand.ExitRefused);

        console.ErrorWriter.ToString().ShouldContain("「only-there」");
        using var spa = _host.CreateSpaClient();
        (await spa.LoginAsync(other.Code, "only-there", OldPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SnapshotAsync(other.Id, otherAccount.Id)).PasswordChangeRequired.ShouldBeFalse();
    }

    private async Task<string> ResetAsync(string organizationCode, string login)
    {
        var console = new FakeSetupConsole(interactive: false);
        var exitCode = await ResetPasswordCommand.RunAsync(
            _host.Factory.Services, ["--organization", organizationCode, "--login", login], console, CancellationToken);
        exitCode.ShouldBe(ResetPasswordCommand.ExitSuccess, console.ErrorWriter.ToString());
        return console.PrintedPassword.ShouldNotBeNull();
    }

    private async Task<(Organization Organization, Account Account)> CreateAdminAsync(string name = "測試組織")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var account = await _host.CreateAccountAsync(
            organization, "admin", OldPassword, AccountRole.SmbAdmin, permissions: Enum.GetValues<AccountPermission>());
        return (organization, account);
    }

    private async Task<AccountSnapshot> SnapshotAsync(Guid organizationId, Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);
        var account = await dbContext.Accounts.AsNoTracking().SingleAsync(candidate => candidate.Id == accountId, CancellationToken);
        return new AccountSnapshot(
            account.PasswordHash, account.SecurityStamp, account.ConcurrencyStamp, account.PasswordChangeRequired,
            account.LockoutEnd, account.AccessFailedCount);
    }

    private sealed record AccountSnapshot(
        string? PasswordHash,
        string? SecurityStamp,
        string? ConcurrencyStamp,
        bool PasswordChangeRequired,
        DateTimeOffset? LockoutEnd,
        int AccessFailedCount);
}
