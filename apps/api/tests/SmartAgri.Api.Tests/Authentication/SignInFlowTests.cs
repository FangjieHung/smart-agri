using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Authentication;

/// <summary>
/// Sign-in end to end against real PostgreSQL (M1 plan, Slice 5 acceptance) in a
/// deployment with several organizations. Every test creates its own organizations with
/// unique codes, so tests do not interfere through failed-attempt counts or lockouts.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class SignInFlowTests : IClassFixture<AuthHostFixture>, IAsyncLifetime
{
    private const string Password = "Correct-Horse-9-battery";

    private readonly AuthHostFixture _host;

    public SignInFlowTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // Guarantees "several organizations" whatever order tests run in.
        await _host.CreateOrganizationAsync();
        await _host.CreateOrganizationAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Full_pkce_flow_returns_me_with_the_accounts_role_and_permissions()
    {
        var organization = await _host.CreateOrganizationAsync("安心商行");
        var account = await _host.CreateAccountAsync(
            organization,
            "internal",
            Password,
            AccountRole.InternalEmployee,
            "內部員工",
            AccountPermission.ReadOwnTracking,
            AccountPermission.UseSharedAssistants,
            AccountPermission.ManageDataSources);
        using var spa = _host.CreateSpaClient();

        var token = await spa.SignInAsync(organization.Code, "internal", Password);
        var me = await spa.GetMeJsonAsync(token.AccessToken);

        token.HasRefreshToken.ShouldBeFalse();
        token.ExpiresInSeconds.ShouldBeInRange(29 * 60, 30 * 60);

        me.GetProperty("id").GetGuid().ShouldBe(account.Id);
        me.GetProperty("displayName").GetString().ShouldBe("內部員工");
        me.GetProperty("role").GetString().ShouldBe("internal-employee");
        me.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString())
            .ShouldBe(["manage-data-sources", "use-shared-assistants", "read-own-tracking"]);
        me.GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(organization.Id);
        me.GetProperty("organization").GetProperty("name").GetString().ShouldBe("安心商行");
        me.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["id", "displayName", "role", "permissions", "organization", "passwordChangeRequired"], ignoreOrder: true);
        me.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Decoded_access_token_has_sub_org_id_and_role_but_no_permissions()
    {
        var organization = await _host.CreateOrganizationAsync();
        var allPermissions = Enum.GetValues<AccountPermission>();
        var account = await _host.CreateAccountAsync(organization, "admin", Password, AccountRole.SmbAdmin, permissions: allPermissions);
        using var spa = _host.CreateSpaClient();

        var token = await spa.SignInAsync(organization.Code, "admin", Password);
        var payload = SpaClient.DecodePayload(token.AccessToken);

        payload.GetProperty("sub").GetString().ShouldBe(account.Id.ToString("D"));
        payload.GetProperty("org_id").GetString().ShouldBe(organization.Id.ToString("D"));
        payload.GetProperty("role").GetString().ShouldBe("smb-admin");

        var claimNames = payload.EnumerateObject().Select(property => property.Name).ToList();
        claimNames.ShouldNotContain(name => name.Contains("permission", StringComparison.OrdinalIgnoreCase));

        // Not smuggled under another name either: no claim value is a permission name.
        var permissionNames = WireNames<AccountPermission>.All.ToHashSet(StringComparer.Ordinal);
        payload.EnumerateObject()
            .SelectMany(property => property.Value.ValueKind == JsonValueKind.Array
                ? property.Value.EnumerateArray().Select(item => item.ToString())
                : [property.Value.ToString()])
            .ShouldNotContain(value => permissionNames.Contains(value));
    }

    [Fact]
    public async Task Wrong_password_unknown_account_unknown_organization_code_and_missing_code_are_indistinguishable()
    {
        var organization = await _host.CreateOrganizationAsync();
        await _host.CreateAccountAsync(organization, "admin", Password);
        using var spa = _host.CreateSpaClient();

        var wrongPassword = await ResponseFingerprint.FromAsync(await spa.LoginAsync(organization.Code, "admin", "Wrong-Password-1"));
        var unknownAccount = await ResponseFingerprint.FromAsync(await spa.LoginAsync(organization.Code, "nobody", Password));
        var unknownOrganization = await ResponseFingerprint.FromAsync(await spa.LoginAsync("no-such-org", "admin", Password));
        var malformedOrganization = await ResponseFingerprint.FromAsync(await spa.LoginAsync("NOT/A CODE", "admin", Password));
        var missingCode = await ResponseFingerprint.FromAsync(await spa.LoginAsync(null, "admin", Password));

        wrongPassword.Status.ShouldBe(HttpStatusCode.Unauthorized);
        wrongPassword.SetsCookie.ShouldBeFalse();
        foreach (var other in new[] { unknownAccount, unknownOrganization, malformedOrganization, missingCode })
        {
            other.Status.ShouldBe(wrongPassword.Status);
            other.ContentType.ShouldBe(wrongPassword.ContentType);
            other.Body.ShouldBe(wrongPassword.Body);
            other.SetsCookie.ShouldBe(wrongPassword.SetsCookie);
        }
    }

    [Fact]
    public async Task Two_organizations_each_with_an_admin_sign_in_with_their_own_code_and_get_their_own_me()
    {
        var organizationA = await _host.CreateOrganizationAsync("組織甲");
        var organizationB = await _host.CreateOrganizationAsync("組織乙");
        var adminA = await _host.CreateAccountAsync(organizationA, "admin", "Password-for-A-1", displayName: "甲的管理者", permissions: AccountPermission.ManageAssistants);
        var adminB = await _host.CreateAccountAsync(organizationB, "admin", "Password-for-B-2", displayName: "乙的管理者", permissions: AccountPermission.ManagePublishing);
        using var spaA = _host.CreateSpaClient();
        using var spaB = _host.CreateSpaClient();

        var meA = await spaA.GetMeJsonAsync((await spaA.SignInAsync(organizationA.Code, "admin", "Password-for-A-1")).AccessToken);
        var meB = await spaB.GetMeJsonAsync((await spaB.SignInAsync(organizationB.Code, "admin", "Password-for-B-2")).AccessToken);

        meA.GetProperty("id").GetGuid().ShouldBe(adminA.Id);
        meA.GetProperty("displayName").GetString().ShouldBe("甲的管理者");
        meA.GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(organizationA.Id);
        meA.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()).ShouldBe(["manage-assistants"]);

        meB.GetProperty("id").GetGuid().ShouldBe(adminB.Id);
        meB.GetProperty("displayName").GetString().ShouldBe("乙的管理者");
        meB.GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(organizationB.Id);
        meB.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()).ShouldBe(["manage-publishing"]);

        // B's password does not open A's admin, even though the login names match.
        using var crossed = _host.CreateSpaClient();
        (await crossed.LoginAsync(organizationA.Code, "admin", "Password-for-B-2")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_consecutive_failures_lock_the_account()
    {
        var organization = await _host.CreateOrganizationAsync();
        var account = await _host.CreateAccountAsync(organization, "admin", Password);
        using var spa = _host.CreateSpaClient();

        // Recorded on the account row, which only works because sign-in establishes the
        // account's organization before Identity writes (otherwise the write guard throws → 500).
        var wrongPassword = await ResponseFingerprint.FromAsync(await spa.LoginAsync(organization.Code, "admin", "Wrong-Password-1"));
        for (var attempt = 2; attempt <= 5; attempt++)
        {
            (await spa.LoginAsync(organization.Code, "admin", "Wrong-Password-1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var lockedWithCorrectPassword = await ResponseFingerprint.FromAsync(await spa.LoginAsync(organization.Code, "admin", Password));

        lockedWithCorrectPassword.Status.ShouldBe(HttpStatusCode.Unauthorized);
        lockedWithCorrectPassword.Body.ShouldBe(wrongPassword.Body);
        lockedWithCorrectPassword.ContentType.ShouldBe(wrongPassword.ContentType);
        lockedWithCorrectPassword.SetsCookie.ShouldBeFalse();

        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var stored = await dbContext.Accounts.AsNoTracking().SingleAsync(candidate => candidate.Id == account.Id, CancellationToken);
        stored.LockoutEnd.ShouldNotBeNull();
        stored.LockoutEnd.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Fewer_than_five_failures_then_the_right_password_signs_in_and_resets_the_count()
    {
        var organization = await _host.CreateOrganizationAsync();
        var account = await _host.CreateAccountAsync(organization, "admin", Password);
        using var spa = _host.CreateSpaClient();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            (await spa.LoginAsync(organization.Code, "admin", "Wrong-Password-1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            (await dbContext.Accounts.AsNoTracking().SingleAsync(candidate => candidate.Id == account.Id, CancellationToken))
                .AccessFailedCount.ShouldBe(4);
        }

        var success = await spa.LoginAsync(organization.Code, "admin", Password);
        success.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        success.Headers.Contains("Set-Cookie").ShouldBeTrue();

        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            var stored = await dbContext.Accounts.AsNoTracking().SingleAsync(candidate => candidate.Id == account.Id, CancellationToken);
            stored.AccessFailedCount.ShouldBe(0);
            stored.LockoutEnd.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Organization_code_and_login_name_are_case_insensitive()
    {
        var organization = await _host.CreateOrganizationAsync();
        await _host.CreateAccountAsync(organization, "Operator", Password);
        using var spa = _host.CreateSpaClient();

        var response = await spa.LoginAsync(organization.Code.ToUpperInvariant(), "operator", Password);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Login_options_require_an_organization_code_when_several_organizations_exist()
    {
        using var spa = _host.CreateSpaClient();

        var response = await spa.Http.GetAsync("/api/v1/auth/login-options", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;
        body.GetProperty("organizationCodeRequired").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Authorize_when_signed_out_redirects_to_the_login_page_with_a_return_url()
    {
        using var spa = _host.CreateSpaClient();

        var response = await spa.AuthorizeAsync(SpaClient.NewCodeVerifier());

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = response.Headers.Location.ShouldNotBeNull().OriginalString;
        location.ShouldStartWith("/login?returnUrl=");

        var returnUrl = QueryHelpers.ParseQuery(location[location.IndexOf('?')..])["returnUrl"].ToString();
        returnUrl.ShouldStartWith("/connect/authorize?");
        var parameters = QueryHelpers.ParseQuery(returnUrl[returnUrl.IndexOf('?')..]);
        parameters["client_id"].ToString().ShouldBe("admin-spa");
        parameters["code_challenge_method"].ToString().ShouldBe("S256");
        parameters["redirect_uri"].ToString().ShouldBe(AuthHostFixture.RedirectUri);
    }

    [Fact]
    public async Task Authorize_without_pkce_is_rejected()
    {
        var organization = await _host.CreateOrganizationAsync();
        await _host.CreateAccountAsync(organization, "admin", Password);
        using var spa = _host.CreateSpaClient();
        (await spa.LoginAsync(organization.Code, "admin", Password)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var url = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "admin-spa",
            ["redirect_uri"] = AuthHostFixture.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["state"] = "s",
        });
        var response = await spa.Http.GetAsync(url, CancellationToken);

        // Either an error page or an error redirect — never a code.
        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        if (response.Headers.Location is { } location)
        {
            var parameters = QueryHelpers.ParseQuery(location.Query);
            parameters.ContainsKey("code").ShouldBeFalse(location.ToString());
            parameters["error"].ToString().ShouldBe("invalid_request");
        }
    }

    [Fact]
    public async Task Logout_ends_the_sign_in_session()
    {
        var organization = await _host.CreateOrganizationAsync();
        await _host.CreateAccountAsync(organization, "admin", Password);
        using var spa = _host.CreateSpaClient();
        (await spa.LoginAsync(organization.Code, "admin", Password)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await spa.Http.PostAsync("/api/v1/auth/logout", content: null, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var authorize = await spa.AuthorizeAsync(SpaClient.NewCodeVerifier());

        authorize.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        authorize.Headers.Location.ShouldNotBeNull().OriginalString.ShouldStartWith("/login?returnUrl=");
    }

    [Fact]
    public async Task A_permission_change_applies_to_the_same_token_immediately()
    {
        var organization = await _host.CreateOrganizationAsync();
        var account = await _host.CreateAccountAsync(
            organization,
            "internal",
            Password,
            AccountRole.InternalEmployee,
            permissions: [AccountPermission.ReadConsentedSubmissions, AccountPermission.ReadOwnTracking]);
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "internal", Password);

        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            var grant = await dbContext.AccountPermissions.SingleAsync(
                candidate => candidate.AccountId == account.Id && candidate.Permission == AccountPermission.ReadConsentedSubmissions,
                CancellationToken);
            dbContext.AccountPermissions.Remove(grant);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var me = await spa.GetMeJsonAsync(token.AccessToken);
        me.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()).ShouldBe(["read-own-tracking"]);
    }

    [Fact]
    public async Task Me_without_a_token_is_401_with_no_body()
    {
        using var spa = _host.CreateSpaClient();

        var response = await spa.Http.GetAsync("/api/v1/me", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_token_for_an_account_that_no_longer_exists_gets_401()
    {
        var organization = await _host.CreateOrganizationAsync();
        var account = await _host.CreateAccountAsync(organization, "leaver", Password);
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "leaver", Password);

        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            dbContext.Accounts.Remove(await dbContext.Accounts.SingleAsync(candidate => candidate.Id == account.Id, CancellationToken));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var response = await spa.GetMeAsync(token.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
    }
}
