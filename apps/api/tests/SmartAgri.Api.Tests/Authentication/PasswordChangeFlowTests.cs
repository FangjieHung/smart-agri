using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Authentication;

/// <summary>
/// The "must change password" gate and <c>POST /api/v1/auth/change-password</c> with real
/// tokens against real PostgreSQL (M1 plan, Slice 11 acceptance). Accounts are flagged the
/// way <c>setup</c> flags its administrator; <c>InitialSetupTests</c> covers the account
/// setup itself creates.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class PasswordChangeFlowTests : IClassFixture<AuthHostFixture>
{
    private const string OneTimePassword = "One-Time-Pass-7x";
    private const string NewPassword = "My-Own-Secret-42z";

    private readonly AuthHostFixture _host;

    public PasswordChangeFlowTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Flagged_account_is_gated_until_it_changes_its_password_and_the_old_password_stops_working()
    {
        var (organization, account) = await CreateFlaggedAdminAsync();
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "admin", OneTimePassword);

        (await spa.GetMeJsonAsync(token.AccessToken)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();

        var gated = await spa.GetAsync(ProtectedProbeEndpoint.Path, token.AccessToken);
        gated.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await gated.Content.ReadAsByteArrayAsync(CancellationToken))
            .ShouldBe((await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.PasswordChangeRequired))).Body);

        (await spa.ChangePasswordAsync(token.AccessToken, OneTimePassword, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Same token: flag read from the database per request, so the gate is lifted at once.
        (await spa.GetMeJsonAsync(token.AccessToken)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeFalse();
        var open = await spa.GetAsync(ProtectedProbeEndpoint.Path, token.AccessToken);
        open.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await spa.LoginAsync(organization.Code, "admin", OneTimePassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var fresh = _host.CreateSpaClient();
        var newToken = await fresh.SignInAsync(organization.Code, "admin", NewPassword);
        (await fresh.GetMeJsonAsync(newToken.AccessToken)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeFalse();

        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        (await dbContext.Accounts.SingleAsync(candidate => candidate.Id == account.Id, CancellationToken))
            .PasswordChangeRequired.ShouldBeFalse();
    }

    [Fact]
    public async Task Changing_the_password_invalidates_the_earlier_sign_in_cookie()
    {
        var (organization, _) = await CreateFlaggedAdminAsync();
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "admin", OneTimePassword);

        (await spa.ChangePasswordAsync(token.AccessToken, OneTimePassword, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The cookie from the one-time-password login can no longer mint codes (security stamp).
        var authorize = await spa.AuthorizeAsync(SpaClient.NewCodeVerifier());
        authorize.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        authorize.Headers.Location.ShouldNotBeNull().ToString().ShouldStartWith("/login?returnUrl=");
    }

    [Fact]
    public async Task Invalid_change_requests_are_422_with_field_errors_and_change_nothing()
    {
        var (organization, account) = await CreateFlaggedAdminAsync();
        using var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, "admin", OneTimePassword)).AccessToken;

        (await ErrorsAsync(await spa.ChangePasswordAsync(token, "wrong-current-1A!", NewPassword))).Keys.ShouldBe(["currentPassword"]);
        (await ErrorsAsync(await spa.ChangePasswordAsync(token, OneTimePassword, OneTimePassword))).Keys.ShouldBe(["newPassword"]);
        (await ErrorsAsync(await spa.ChangePasswordAsync(token, "", null))).Keys.ShouldBe(["currentPassword", "newPassword"], ignoreOrder: true);

        var weak = await ErrorsAsync(await spa.ChangePasswordAsync(token, OneTimePassword, "short"));
        weak.Keys.ShouldBe(["newPassword"]);
        weak["newPassword"].Length.ShouldBeGreaterThanOrEqualTo(3); // too short, no upper case, no digit, no symbol

        (await spa.GetMeJsonAsync(token)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
        (await spa.LoginAsync(organization.Code, "admin", OneTimePassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        (await dbContext.Accounts.SingleAsync(candidate => candidate.Id == account.Id, CancellationToken)).AccessFailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Change_password_needs_a_bearer_token()
    {
        using var spa = _host.CreateSpaClient();

        (await spa.ChangePasswordAsync(null, OneTimePassword, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_current_passwords_count_towards_lockout()
    {
        var (organization, _) = await CreateFlaggedAdminAsync();
        using var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, "admin", OneTimePassword)).AccessToken;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            (await spa.ChangePasswordAsync(token, $"wrong-{attempt}-Aa!", NewPassword)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        // The fifth failure locks the account: from then on it is treated as signed out.
        (await spa.ChangePasswordAsync(token, "wrong-5-Aa!", NewPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await spa.ChangePasswordAsync(token, OneTimePassword, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await spa.LoginAsync(organization.Code, "admin", OneTimePassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Accounts_without_the_flag_are_not_gated()
    {
        var organization = await _host.CreateOrganizationAsync();
        await _host.CreateAccountAsync(organization, "member", NewPassword, AccountRole.InternalEmployee);
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "member", NewPassword);

        (await spa.GetAsync(ProtectedProbeEndpoint.Path, token.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await spa.GetMeJsonAsync(token.AccessToken)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeFalse();
    }

    private async Task<(Organization Organization, Account Account)> CreateFlaggedAdminAsync()
    {
        var organization = await _host.CreateOrganizationAsync();
        var account = await _host.CreateAccountAsync(
            organization, "admin", OneTimePassword, AccountRole.SmbAdmin, permissions: Enum.GetValues<AccountPermission>());
        await _host.RequirePasswordChangeAsync(account);
        return (organization, account);
    }

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(ApiErrors.ProblemJsonContentType);

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("message").GetString().ShouldNotBeNullOrEmpty();
        return json.RootElement.GetProperty("errors").EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.EnumerateArray().Select(error => error.GetString()!).ToArray());
    }
}
