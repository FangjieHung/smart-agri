using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Setup;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Setup;
using SmartAgri.Domain.Accounts;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Team;

/// <summary>
/// <c>POST /api/v1/team/members</c> against real PostgreSQL (issue #52, M2 plan Slice 18
/// acceptance): authorization, cross-organization isolation, several same-role members,
/// duplicate login names, the "must change password" handoff to M1 Slice 11's gate, and that
/// the one-time password never reaches logs or traces.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class CreateMemberEndpointTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Create-Member-Pass-1!";

    private readonly AuthHostFixture _host;

    public CreateMemberEndpointTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_creates_a_member_who_must_change_password_before_anything_else()
    {
        var organization = await _host.CreateOrganizationAsync("安心商行");
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "安心商行管理者", AccountPermission.ManageAssistants);

        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "admin", Password);

        var response = await spa.PostAsync(
            "/api/v1/team/members",
            token.AccessToken,
            new
            {
                loginName = "new-hire",
                displayName = "新進同仁",
                role = "internal-employee",
                permissions = new[] { "use-shared-assistants" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await BodyJsonAsync(response);
        var member = body.GetProperty("member");
        member.GetProperty("displayName").GetString().ShouldBe("新進同仁");
        member.GetProperty("role").GetString().ShouldBe("internal-employee");
        member.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldBe(["use-shared-assistants"]);
        member.GetProperty("lockedPermissions").EnumerateArray().ShouldBeEmpty();
        var memberId = member.GetProperty("id").GetGuid();

        var oneTimePassword = body.GetProperty("oneTimePassword").GetString();
        oneTimePassword.ShouldNotBeNullOrEmpty();
        oneTimePassword.Length.ShouldBe(OneTimePasswordGenerator.Length);

        // The account exists, must change its password, and its hash verifies the printed
        // one-time password (never stored or comparable in plain text elsewhere).
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var created = await dbContext.Accounts.SingleAsync(a => a.Id == memberId, CancellationToken);
        created.LoginName.ShouldBe("new-hire");
        created.PasswordChangeRequired.ShouldBeTrue();
        created.LockoutEnabled.ShouldBeTrue();
        new PasswordHasher<Account>().VerifyHashedPassword(created, created.PasswordHash!, oneTimePassword)
            .ShouldNotBe(PasswordVerificationResult.Failed);

        // Signing in as the new member and reading /me is allowed (M1 Slice 11's exemption),
        // but every other endpoint is blocked until the password is changed.
        using var newMemberSpa = _host.CreateSpaClient();
        var newMemberToken = await newMemberSpa.SignInAsync(organization.Code, "new-hire", oneTimePassword);
        var me = await newMemberSpa.GetMeJsonAsync(newMemberToken.AccessToken);
        me.GetProperty("id").GetGuid().ShouldBe(memberId);

        var blocked = await newMemberSpa.GetAsync("/api/v1/team", newMemberToken.AccessToken);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(blocked)).GetProperty("reason").GetString().ShouldBe("password-change-required");

        var changePassword = await newMemberSpa.ChangePasswordAsync(newMemberToken.AccessToken, oneTimePassword, "Brand-New-Pass-1!");
        changePassword.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // After changing the password, the gate is gone (though this account still has no
        // manage-assistants, so it gets the ordinary team 403, not password-change-required).
        var afterChange = await newMemberSpa.GetAsync("/api/v1/team", newMemberToken.AccessToken);
        afterChange.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(afterChange)).GetProperty("reason").GetString().ShouldBe("team");
    }

    [Fact]
    public async Task Caller_without_manage_assistants_gets_the_same_403_as_a_get_to_team()
    {
        var organization = await _host.CreateOrganizationAsync("安心商行");
        await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, "安心商行客服同仁",
            AccountPermission.UseSharedAssistants);

        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "internal", Password);

        var getTeam = await spa.GetAsync("/api/v1/team", token.AccessToken);
        var createMember = await spa.PostAsync("/api/v1/team/members", token.AccessToken, ValidCreateBody("blocked"));

        getTeam.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        createMember.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(getTeam, createMember);
    }

    [Fact]
    public async Task Duplicate_login_name_in_the_same_organization_is_422_but_a_different_organization_may_reuse_it()
    {
        var anxin = await _host.CreateOrganizationAsync("安心商行");
        var anxinAdmin = await _host.CreateAccountAsync(
            anxin, "admin", Password, AccountRole.SmbAdmin, "安心商行管理者", AccountPermission.ManageAssistants);
        await _host.CreateAccountAsync(
            anxin, "duplicate-name", Password, AccountRole.InternalEmployee, "已存在的同仁", AccountPermission.UseSharedAssistants);

        var control = await _host.CreateOrganizationAsync("對照組織");
        var controlAdmin = await _host.CreateAccountAsync(
            control, "admin", Password, AccountRole.SmbAdmin, "對照組織管理者", AccountPermission.ManageAssistants);

        using var anxinSpa = _host.CreateSpaClient();
        var anxinToken = await anxinSpa.SignInAsync(anxin.Code, "admin", Password);
        var rejected = await anxinSpa.PostAsync("/api/v1/team/members", anxinToken.AccessToken, ValidCreateBody("duplicate-name"));
        rejected.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var rejectedBody = await BodyJsonAsync(rejected);
        rejectedBody.GetProperty("message").GetString().ShouldNotBeNullOrEmpty();

        // Nothing extra was written: still exactly the two accounts (admin + the existing one).
        (await MemberCountAsync(anxin.Id)).ShouldBe(2);

        // A different organization may use the exact same login name.
        using var controlSpa = _host.CreateSpaClient();
        var controlToken = await controlSpa.SignInAsync(control.Code, "admin", Password);
        var accepted = await controlSpa.PostAsync("/api/v1/team/members", controlToken.AccessToken, ValidCreateBody("duplicate-name"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created);

        (await MemberCountAsync(control.Id)).ShouldBe(2);
    }

    /// <summary>
    /// Review finding / M2 plan Slice 18 acceptance: several members with the same role,
    /// created one after another, must keep separate ids — editing one must never touch the
    /// other (the same invariant <c>TeamEndpointsTests</c> proves for pre-existing accounts).
    /// </summary>
    [Fact]
    public async Task Two_newly_created_same_role_members_keep_separate_ids_and_editing_one_leaves_the_other_unchanged()
    {
        var organization = await _host.CreateOrganizationAsync("安心商行");
        await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "安心商行管理者", AccountPermission.ManageAssistants);

        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "admin", Password);

        var firstResponse = await spa.PostAsync(
            "/api/v1/team/members",
            token.AccessToken,
            new { loginName = "internal-a", displayName = "客服同仁 A", role = "internal-employee", permissions = new[] { "use-shared-assistants" } });
        var secondResponse = await spa.PostAsync(
            "/api/v1/team/members",
            token.AccessToken,
            new { loginName = "internal-b", displayName = "客服同仁 B", role = "internal-employee", permissions = new[] { "read-consented-submissions" } });

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var firstId = (await BodyJsonAsync(firstResponse)).GetProperty("member").GetProperty("id").GetGuid();
        var secondId = (await BodyJsonAsync(secondResponse)).GetProperty("member").GetProperty("id").GetGuid();
        firstId.ShouldNotBe(secondId);

        var getTeam = await spa.GetAsync("/api/v1/team", token.AccessToken);
        var members = (await BodyJsonAsync(getTeam)).GetProperty("members").EnumerateArray().ToList();
        members.Select(member => member.GetProperty("id").GetGuid()).ShouldContain(firstId);
        members.Select(member => member.GetProperty("id").GetGuid()).ShouldContain(secondId);

        var update = await spa.PutAsync(
            $"/api/v1/team/members/{firstId}/permissions",
            token.AccessToken,
            new { permissions = new[] { "manage-data-sources" } });
        update.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        (await dbContext.AccountPermissions.Where(g => g.AccountId == firstId).Select(g => g.Permission).ToListAsync(CancellationToken))
            .ShouldBe([AccountPermission.ManageDataSources]);
        (await dbContext.AccountPermissions.Where(g => g.AccountId == secondId).Select(g => g.Permission).ToListAsync(CancellationToken))
            .ShouldBe([AccountPermission.ReadConsentedSubmissions]);
    }

    [Theory]
    [InlineData("", "顯示名稱", "internal-employee", "loginName")]
    [InlineData("login", "", "internal-employee", "displayName")]
    [InlineData("login", "顯示名稱", "not-a-real-role", "role")]
    public async Task Invalid_input_is_422_and_nothing_is_written(string loginName, string displayName, string role, string expectedField)
    {
        var organization = await _host.CreateOrganizationAsync("安心商行");
        await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "安心商行管理者", AccountPermission.ManageAssistants);

        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "admin", Password);

        var response = await spa.PostAsync(
            "/api/v1/team/members",
            token.AccessToken,
            new { loginName, displayName, role, permissions = Array.Empty<string>() });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(response);
        body.GetProperty("errors").TryGetProperty(expectedField, out _).ShouldBeTrue();
        (await MemberCountAsync(organization.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_permission_value_is_422_and_nothing_is_written()
    {
        var organization = await _host.CreateOrganizationAsync("安心商行");
        await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "安心商行管理者", AccountPermission.ManageAssistants);

        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, "admin", Password);

        var response = await spa.PostAsync(
            "/api/v1/team/members",
            token.AccessToken,
            new { loginName = "someone", displayName = "某人", role = "internal-employee", permissions = new[] { "not-a-real-permission" } });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await MemberCountAsync(organization.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task The_one_time_password_never_reaches_logs_or_traces()
    {
        var telemetry = new TelemetryCapture();
        using var factory = _host.Factory.WithWebHostBuilder(telemetry.Attach);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        var spa = new SpaClient(client);

        var organization = await _host.CreateOrganizationAsync("安心商行");
        await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "安心商行管理者", AccountPermission.ManageAssistants);

        var token = await spa.SignInAsync(organization.Code, "admin", Password);
        var response = await spa.PostAsync("/api/v1/team/members", token.AccessToken, ValidCreateBody("telemetry-check"));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var oneTimePassword = (await BodyJsonAsync(response)).GetProperty("oneTimePassword").GetString();
        oneTimePassword.ShouldNotBeNullOrEmpty();

        telemetry.All.ShouldNotContain(entry => entry.Contains(oneTimePassword, StringComparison.Ordinal));
    }

    private static object ValidCreateBody(string loginName) =>
        new { loginName, displayName = "測試成員", role = "internal-employee", permissions = new[] { "use-shared-assistants" } };

    private async Task<int> MemberCountAsync(Guid organizationId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);
        return await dbContext.Accounts.CountAsync(CancellationToken);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

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
