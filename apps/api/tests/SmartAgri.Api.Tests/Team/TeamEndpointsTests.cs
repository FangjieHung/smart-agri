using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Team;

/// <summary>
/// <c>GET /api/v1/team</c> and <c>PUT /api/v1/team/members/{id}/permissions</c> against real
/// PostgreSQL (M1 plan, Slice 8 acceptance, and the Slice 5 403-consistency follow-up).
/// Organizations and accounts are created directly through <see cref="AuthHostFixture"/>'s
/// helpers rather than <c>DevelopmentSeeder</c> (which only runs in <c>Development</c> and
/// is exercised by its own tests) — the seed's three "安心商行" accounts and their initial
/// permissions are reproduced here (<c>demo-seed.ts</c> / <c>DevelopmentSeedData</c>) so the
/// scenarios match the mock exactly.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class TeamEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Team-Endpoint-Pass-1!";

    private readonly AuthHostFixture _host;

    public TeamEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_gets_the_teams_three_members_not_the_other_organizations_account()
    {
        var (anxin, admin, internalEmployee, customer) = await CreateAnxinOrganizationAsync();
        var control = await _host.CreateOrganizationAsync("對照組織");
        await _host.CreateAccountAsync(
            control, "admin", Password, AccountRole.SmbAdmin, "對照組織管理者", AccountPermission.ManageAssistants);

        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(anxin.Code, "admin", Password);

        var response = await spa.GetAsync("/api/v1/team", token.AccessToken);
        var body = await BodyJsonAsync(response);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var ids = body.GetProperty("members").EnumerateArray().Select(member => member.GetProperty("id").GetGuid()).ToList();
        ids.ShouldBe([admin.Id, internalEmployee.Id, customer.Id], ignoreOrder: true);
        body.GetProperty("savedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Caller_without_manage_assistants_gets_the_same_403_as_a_put_to_a_nonexistent_member()
    {
        var (anxin, _, _, _) = await CreateAnxinOrganizationAsync();

        using var internalSpa = _host.CreateSpaClient();
        var internalToken = await internalSpa.SignInAsync(anxin.Code, "internal", Password);
        var getTeam = await internalSpa.GetAsync("/api/v1/team", internalToken.AccessToken);
        getTeam.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var adminSpa = _host.CreateSpaClient();
        var adminToken = await adminSpa.SignInAsync(anxin.Code, "admin", Password);
        var putNonExistent = await adminSpa.PutAsync(
            $"/api/v1/team/members/{Guid.NewGuid()}/permissions", adminToken.AccessToken, EmptyPermissions);
        putNonExistent.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await AssertIdenticalAsync(getTeam, putNonExistent);
    }

    [Fact]
    public async Task A_organizations_token_gets_an_identical_403_for_b_orgs_member_and_a_nonexistent_id()
    {
        var (anxin, _, _, _) = await CreateAnxinOrganizationAsync();
        var control = await _host.CreateOrganizationAsync("對照組織");
        var controlAdmin = await _host.CreateAccountAsync(
            control, "admin", Password, AccountRole.SmbAdmin, "對照組織管理者", AccountPermission.ManageAssistants);

        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(anxin.Code, "admin", Password);

        var toOtherOrganization = await spa.PutAsync(
            $"/api/v1/team/members/{controlAdmin.Id}/permissions", token.AccessToken, EmptyPermissions);
        var toNonExistent = await spa.PutAsync(
            $"/api/v1/team/members/{Guid.NewGuid()}/permissions", token.AccessToken, EmptyPermissions);

        toOtherOrganization.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        toNonExistent.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(toOtherOrganization, toNonExistent);
    }

    [Fact]
    public async Task Admin_removing_own_manage_assistants_is_422_and_the_database_is_unchanged()
    {
        var (anxin, admin, _, _) = await CreateAnxinOrganizationAsync();
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(anxin.Code, "admin", Password);

        var response = await spa.PutAsync(
            $"/api/v1/team/members/{admin.Id}/permissions",
            token.AccessToken,
            new { permissions = new[] { "manage-data-sources", "manage-publishing", "read-consented-submissions" } });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(response);
        body.GetProperty("message").GetString().ShouldNotBeNullOrEmpty();

        (await CurrentPermissionsAsync(anxin.Id, admin.Id)).ShouldBe(
        [
            AccountPermission.ManageAssistants,
            AccountPermission.ManageDataSources,
            AccountPermission.ManagePublishing,
            AccountPermission.ReadConsentedSubmissions,
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task Unknown_permission_value_is_422_and_nothing_is_written()
    {
        var (anxin, _, internalEmployee, _) = await CreateAnxinOrganizationAsync();
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(anxin.Code, "admin", Password);

        var response = await spa.PutAsync(
            $"/api/v1/team/members/{internalEmployee.Id}/permissions",
            token.AccessToken,
            new { permissions = new[] { "use-shared-assistants", "not-a-real-permission" } });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await CurrentPermissionsAsync(anxin.Id, internalEmployee.Id)).ShouldBe(
            [AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions], ignoreOrder: true);
    }

    [Fact]
    public async Task Removing_a_members_permission_takes_effect_immediately_on_their_existing_token()
    {
        var (anxin, _, internalEmployee, _) = await CreateAnxinOrganizationAsync();

        using var internalSpa = _host.CreateSpaClient();
        var internalToken = await internalSpa.SignInAsync(anxin.Code, "internal", Password);
        (await PermissionStringsAsync(internalSpa, internalToken.AccessToken)).ShouldContain("read-consented-submissions");

        using var adminSpa = _host.CreateSpaClient();
        var adminToken = await adminSpa.SignInAsync(anxin.Code, "admin", Password);
        var response = await adminSpa.PutAsync(
            $"/api/v1/team/members/{internalEmployee.Id}/permissions",
            adminToken.AccessToken,
            new { permissions = new[] { "use-shared-assistants" } });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Same token as before, no new sign-in: permissions are read from the database on
        // every request (M1 plan §3), so the change is visible at once.
        var afterPermissions = await PermissionStringsAsync(internalSpa, internalToken.AccessToken);
        afterPermissions.ShouldNotContain("read-consented-submissions");
        afterPermissions.ShouldContain("use-shared-assistants");
    }

    [Fact]
    public async Task Successful_update_normalizes_order_sets_saved_at_and_reports_the_viewers_own_locked_permission()
    {
        var (anxin, admin, internalEmployee, _) = await CreateAnxinOrganizationAsync();
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(anxin.Code, "admin", Password);
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        // Out of order with a duplicate; the server must normalize to ACCOUNT_PERMISSIONS order,
        // which puts submit-authorized-forms before use-shared-assistants (unlike the enum).
        var response = await spa.PutAsync(
            $"/api/v1/team/members/{internalEmployee.Id}/permissions",
            token.AccessToken,
            new { permissions = new[] { "use-shared-assistants", "submit-authorized-forms", "use-shared-assistants", "read-consented-submissions" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyJsonAsync(response);
        var savedAt = body.GetProperty("savedAt").GetDateTimeOffset();
        savedAt.ShouldBeGreaterThan(before);
        savedAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(1));

        var members = body.GetProperty("members").EnumerateArray().ToList();
        var internalRow = members.Single(member => member.GetProperty("id").GetGuid() == internalEmployee.Id);
        internalRow.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString())
            .ShouldBe(["read-consented-submissions", "submit-authorized-forms", "use-shared-assistants"]); // ACCOUNT_PERMISSIONS order
        internalRow.GetProperty("lockedPermissions").EnumerateArray().ShouldBeEmpty();

        var adminRow = members.Single(member => member.GetProperty("id").GetGuid() == admin.Id);
        adminRow.GetProperty("lockedPermissions").EnumerateArray().Select(permission => permission.GetString())
            .ShouldBe(["manage-assistants"]);

        // A later GET (not just the PUT's own response) sees the same whole-team timestamp.
        var getResponse = await spa.GetAsync("/api/v1/team", token.AccessToken);
        (await BodyJsonAsync(getResponse)).GetProperty("savedAt").GetDateTimeOffset().ShouldBe(savedAt);
    }

    [Fact]
    public async Task Password_change_gate_still_blocks_the_team_endpoints()
    {
        var organization = await _host.CreateOrganizationAsync();
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, permissions: [AccountPermission.ManageAssistants]);
        await _host.RequirePasswordChangeAsync(admin);

        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "admin", Password);

        var response = await spa.GetAsync("/api/v1/team", token.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe("password-change-required");
    }

    private async Task<(Organization Organization, Account Admin, Account Internal, Account Customer)> CreateAnxinOrganizationAsync()
    {
        var organization = await _host.CreateOrganizationAsync("安心商行");
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "安心商行管理者",
            AccountPermission.ManageAssistants,
            AccountPermission.ManageDataSources,
            AccountPermission.ManagePublishing,
            AccountPermission.ReadConsentedSubmissions);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, "安心商行客服同仁",
            AccountPermission.UseSharedAssistants,
            AccountPermission.ReadConsentedSubmissions);
        var customer = await _host.CreateAccountAsync(
            organization, "customer", Password, AccountRole.ExternalCustomer, "外部客戶",
            AccountPermission.SubmitAuthorizedForms,
            AccountPermission.ReadOwnTracking);

        return (organization, admin, internalEmployee, customer);
    }

    private async Task<List<AccountPermission>> CurrentPermissionsAsync(Guid organizationId, Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);
        return await dbContext.AccountPermissions
            .Where(grant => grant.AccountId == accountId)
            .Select(grant => grant.Permission)
            .ToListAsync(CancellationToken);
    }

    private static async Task<List<string?>> PermissionStringsAsync(SpaClient spa, string accessToken) =>
        [.. (await spa.GetMeJsonAsync(accessToken)).GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString())];

    private static readonly object EmptyPermissions = new { permissions = Array.Empty<string>() };

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Byte-for-byte identical, like <c>SignInFlowTests</c>'s indistinguishable-401
    /// assertions: status, content type and body (never just status).</summary>
    private static async Task AssertIdenticalAsync(HttpResponseMessage first, HttpResponseMessage second)
    {
        var a = await ResponseFingerprint.FromAsync(first);
        var b = await ResponseFingerprint.FromAsync(second);

        b.Status.ShouldBe(a.Status);
        b.ContentType.ShouldBe(a.ContentType);
        b.Body.ShouldBe(a.Body);
        b.SetsCookie.ShouldBe(a.SetsCookie);
    }
}
