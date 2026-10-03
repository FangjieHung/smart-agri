using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Databases;

/// <summary>
/// Data managers of a database (M4, issue #144) against real PostgreSQL: designating accounts,
/// who can then see the database, and that revoking either half (the designation or the
/// <c>read-consented-submissions</c> permission) takes effect on the very next request with the
/// same token.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DatabaseDataManagerEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Database-Manager-Pass-1!";
    private const string BasePath = "/api/v1/databases";
    private const string ForbiddenMessage = "你沒有這個資料庫的存取權限，或它已不存在。";

    private static readonly AccountPermission[] AllAdminPermissions =
    [
        AccountPermission.ManageAssistants,
        AccountPermission.ManageDataSources,
        AccountPermission.ManagePublishing,
        AccountPermission.ReadConsentedSubmissions,
    ];

    private readonly AuthHostFixture _host;

    public DatabaseDataManagerEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Creating ---------------------------------------------------------------------------

    [Fact]
    public async Task A_new_database_starts_with_its_creator_as_the_one_data_manager_and_no_last_change()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);

        var access = (await GetDetailAsync(admin, databaseId)).GetProperty("access");

        OpenApiContract.AssertKeysMatchSchema(access, "DatabaseAccessView");
        access.GetProperty("owner").GetProperty("id").GetGuid().ShouldBe(org.Admin.Id);
        DataManagerIds(access).ShouldBe([org.Admin.Id]);
        access.GetProperty("dataManagers")[0].GetProperty("assignedBy").GetProperty("id").GetGuid().ShouldBe(org.Admin.Id);
        access.GetProperty("effectiveReaders").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ShouldBe([org.Admin.Id]);
        access.GetProperty("viewerIsDataManager").GetBoolean().ShouldBeTrue();
        access.GetProperty("viewerCanReadRecords").GetBoolean().ShouldBeTrue();
        access.GetProperty("viewerCanManageAccess").GetBoolean().ShouldBeTrue();
        access.GetProperty("lastChange").ValueKind.ShouldBe(JsonValueKind.Null);
        // Every account of the organization, in role order, each with its permission now.
        access.GetProperty("candidates").EnumerateArray()
            .Select(c => (c.GetProperty("id").GetGuid(), c.GetProperty("role").GetString(), c.GetProperty("hasReadPermission").GetBoolean()))
            .ShouldBe(
            [
                (org.Admin.Id, "smb-admin", true),
                (org.Internal.Id, "internal-employee", true),
                (org.Customer.Id, "external-customer", false),
            ]);
    }

    // --- Who may be designated --------------------------------------------------------------

    [Fact]
    public async Task Only_accounts_of_the_same_organization_can_be_designated_and_a_refusal_writes_nothing()
    {
        var org = await CreateOrganizationAsync();
        var otherOrg = await CreateOrganizationAsync("其他組織");
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);

        foreach (var (who, ids) in new[]
        {
            ("another organization's account", new[] { org.Internal.Id, otherOrg.Admin.Id }),
            ("an unknown id", new[] { Guid.NewGuid() }),
            ("an empty id", new[] { Guid.Empty }),
        })
        {
            var response = await PutAccessAsync(admin, databaseId, ids);

            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, who);
            var body = await BodyJsonAsync(response);
            body.GetProperty("message").GetString().ShouldBe("有不認得的帳號，這次指定沒有儲存。", who);
            body.GetProperty("errors").GetProperty("dataManagerAccountIds").GetArrayLength().ShouldBe(1);
        }

        var missing = await admin.Spa.PutAsync($"{BasePath}/{databaseId}/access", admin.Token, new { });
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(missing)).GetProperty("message").GetString().ShouldBe("請提供資料管理者清單。");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseDataManagers.Select(m => m.AccountId).ToListAsync(CancellationToken)).ShouldBe([org.Admin.Id]);
        (await dbContext.DatabaseDataManagerChanges.CountAsync(CancellationToken)).ShouldBe(0);
        await using var otherContext = _host.Postgres.CreateDbContext(otherOrg.Organization.Id);
        (await otherContext.DatabaseDataManagers.CountAsync(CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Designating_replaces_the_list_and_the_owner_can_remove_themselves()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);

        var response = await PutAccessAsync(admin, databaseId, [org.Internal.Id, org.Internal.Id]);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var access = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(access, "DatabaseAccessView");
        DataManagerIds(access).ShouldBe([org.Internal.Id], "the list is the complete set after the change; duplicates collapse");
        access.GetProperty("viewerIsDataManager").GetBoolean().ShouldBeFalse();
        access.GetProperty("viewerCanReadRecords").GetBoolean().ShouldBeFalse("owning it, or even having the permission, is not designation");
        access.GetProperty("viewerCanManageAccess").GetBoolean().ShouldBeTrue();

        (await PutAccessAsync(admin, databaseId, [])).StatusCode.ShouldBe(HttpStatusCode.OK);
        DataManagerIds((await GetDetailAsync(admin, databaseId)).GetProperty("access")).ShouldBeEmpty();
    }

    // --- Visibility: designation AND account permission -------------------------------------

    [Fact]
    public async Task Designated_without_the_permission_and_permitted_without_designation_both_see_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var internalEmployee = await SignInAsync(org, "internal");
        var customer = await SignInAsync(org, "customer");

        // internal has the permission but is not designated; customer will be designated but lacks it.
        (await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Customer.Id])).StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var (who, caller) in new[] { ("permitted, not designated", internalEmployee), ("designated, not permitted", customer) })
        {
            await AssertInvisibleAsync(caller, databaseId, who);
        }

        // The owner's preview tells the two halves apart.
        var access = (await GetDetailAsync(admin, databaseId)).GetProperty("access");
        access.GetProperty("dataManagers").EnumerateArray()
            .Select(m => (m.GetProperty("account").GetProperty("id").GetGuid(), m.GetProperty("hasReadPermission").GetBoolean()))
            .ShouldBe([(org.Admin.Id, true), (org.Customer.Id, false)], ignoreOrder: true);
        access.GetProperty("effectiveReaders").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ShouldBe([org.Admin.Id]);
    }

    [Fact]
    public async Task Designated_and_permitted_sees_it_read_only_and_cannot_change_the_managers()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin, "客戶資料");
        var manager = await SignInAsync(org, "internal");
        await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id]);

        var list = await ListAsync(manager);
        list.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ShouldBe([databaseId]);
        list[0].GetProperty("viewerCanManage").GetBoolean().ShouldBeFalse();
        list[0].GetProperty("owner").GetProperty("id").GetGuid().ShouldBe(org.Admin.Id);
        list[0].TryGetProperty("recordCount", out _).ShouldBeFalse("no response carries a record count before #146");

        var detail = await GetDetailAsync(manager, databaseId);
        detail.GetProperty("summary").GetProperty("viewerCanManage").GetBoolean().ShouldBeFalse();
        detail.GetProperty("form").GetProperty("fields").GetArrayLength().ShouldBeGreaterThan(0);
        var access = detail.GetProperty("access");
        access.GetProperty("viewerIsDataManager").GetBoolean().ShouldBeTrue();
        access.GetProperty("viewerCanReadRecords").GetBoolean().ShouldBeTrue();
        access.GetProperty("viewerCanManageAccess").GetBoolean().ShouldBeFalse();
        access.GetProperty("candidates").GetArrayLength().ShouldBe(0, "only the owner is told who could be designated");

        // Not the owner: cannot change the designations, and the refusal equals a missing id's.
        var refused = await PutAccessAsync(manager, databaseId, [org.Internal.Id, org.Customer.Id]);
        var missing = await PutAccessAsync(manager, Guid.NewGuid(), [org.Internal.Id, org.Customer.Id]);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(refused, missing);

        // Nor can a data manager edit the form or trial-fill it (#143): the same 403 as a missing id.
        var formPath = $"{BasePath}/{databaseId}/form";
        var missingFormPath = $"{BasePath}/{Guid.NewGuid()}/form";
        var saveForm = await manager.Spa.PutAsync(formPath, manager.Token, new
        {
            baseVersionNumber = 1,
            fields = new object[] { new { id = "field-x", label = "被改掉的欄位", type = "text", required = false } },
        });
        var saveMissing = await manager.Spa.PutAsync(missingFormPath, manager.Token, new
        {
            baseVersionNumber = 1,
            fields = new object[] { new { id = "field-x", label = "被改掉的欄位", type = "text", required = false } },
        });
        saveForm.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(saveForm, saveMissing);
        var preview = await manager.Spa.PostAsync($"{formPath}/preview", manager.Token, new { answers = new { } });
        var previewMissing = await manager.Spa.PostAsync($"{missingFormPath}/preview", manager.Token, new { answers = new { } });
        preview.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(preview, previewMissing);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.DatabaseDataManagers.Select(m => m.AccountId).ToListAsync(CancellationToken))
            .ShouldBe([org.Admin.Id, org.Internal.Id], ignoreOrder: true);
        (await dbContext.DatabaseFormVersions.CountAsync(v => v.DatabaseId == databaseId, CancellationToken))
            .ShouldBe(1, "a refused form save writes no version");
    }

    [Fact]
    public async Task Revoking_the_designation_or_the_permission_each_takes_effect_on_the_next_request_with_the_same_token()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var manager = await SignInAsync(org, "internal");
        await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id]);
        (await ListAsync(manager)).GetArrayLength().ShouldBe(1);

        // 1. Revoke the designation.
        await PutAccessAsync(admin, databaseId, [org.Admin.Id]);
        await AssertInvisibleAsync(manager, databaseId, "designation revoked");

        // 2. Designate again: it is back, nothing was lost.
        await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id]);
        (await GetDetailAsync(manager, databaseId)).GetProperty("summary").GetProperty("name").GetString().ShouldBe("客戶資料庫");

        // 3. Revoke the account permission (through the team API, as an admin would).
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.UseSharedAssistants]);
        await AssertInvisibleAsync(manager, databaseId, "permission revoked");
        // The designation is still there, only inert; the owner sees exactly that.
        var access = (await GetDetailAsync(admin, databaseId)).GetProperty("access");
        DataManagerIds(access).ShouldContain(org.Internal.Id);
        access.GetProperty("effectiveReaders").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ShouldBe([org.Admin.Id]);

        // 4. Grant it again.
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions]);
        (await ListAsync(manager)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Another_organizations_or_a_missing_database_is_the_same_403_for_detail_and_for_changing_managers()
    {
        var orgA = await CreateOrganizationAsync("組織 A");
        var adminA = await SignInAsync(orgA, "admin");
        var databaseId = await CreateDatabaseAsync(adminA, "A 的客戶名單");
        var orgB = await CreateOrganizationAsync("組織 B");
        var adminB = await SignInAsync(orgB, "admin");

        var toForeign = await PutAccessAsync(adminB, databaseId, [orgB.Admin.Id]);
        var toMissing = await PutAccessAsync(adminB, Guid.NewGuid(), [orgB.Admin.Id]);

        toForeign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(toForeign, toMissing);
        var raw = await toForeign.Content.ReadAsStringAsync(CancellationToken);
        JsonDocument.Parse(raw).RootElement.GetProperty("message").GetString().ShouldBe(ForbiddenMessage);
        raw.ShouldNotContain("A 的客戶名單");
        await AssertInvisibleAsync(adminB, databaseId, "another organization's owner");

        (await PutAccessAsync(await SignInAsync(orgA, "internal"), databaseId, [orgA.Internal.Id])).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await adminA.Spa.PutAsync($"{BasePath}/{databaseId}/access", null, new { dataManagerAccountIds = Array.Empty<Guid>() }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- Audit ------------------------------------------------------------------------------

    [Fact]
    public async Task Every_change_records_who_and_when_and_a_request_that_changes_nothing_records_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var added = await BodyJsonAsync(await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id, org.Customer.Id]));
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var internalEntry = added.GetProperty("dataManagers").EnumerateArray()
            .Single(m => m.GetProperty("account").GetProperty("id").GetGuid() == org.Internal.Id);
        internalEntry.GetProperty("assignedBy").GetProperty("id").GetGuid().ShouldBe(org.Admin.Id);
        internalEntry.GetProperty("assignedBy").GetProperty("displayName").GetString().ShouldBe("安心商行管理者");
        internalEntry.GetProperty("assignedAt").GetDateTimeOffset().ShouldBeInRange(before, after);
        var lastChange = added.GetProperty("lastChange");
        lastChange.GetProperty("changedBy").GetProperty("id").GetGuid().ShouldBe(org.Admin.Id);
        lastChange.GetProperty("changedAt").GetDateTimeOffset().ShouldBeInRange(before, after);

        // Saving the same set again writes nothing; removing one logs the removal.
        await PutAccessAsync(admin, databaseId, [org.Customer.Id, org.Admin.Id, org.Internal.Id]);
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.DatabaseDataManagerChanges.CountAsync(CancellationToken)).ShouldBe(2, "only the two additions so far");
        }

        await PutAccessAsync(admin, databaseId, [org.Admin.Id, org.Internal.Id]);

        await using var context = _host.Postgres.CreateDbContext(org.Organization.Id);
        var changes = await context.DatabaseDataManagerChanges.OrderBy(c => c.ChangedAt).ToListAsync(CancellationToken);
        changes.Count.ShouldBe(3);
        changes.ShouldAllBe(c => c.ChangedByAccountId == org.Admin.Id && c.DatabaseId == databaseId);
        changes.Where(c => c.Assigned).Select(c => c.AccountId).ShouldBe([org.Internal.Id, org.Customer.Id], ignoreOrder: true);
        var removal = changes.Single(c => !c.Assigned);
        removal.AccountId.ShouldBe(org.Customer.Id);
        removal.ChangedAt.ShouldBeInRange(before, DateTimeOffset.UtcNow.AddSeconds(1));
        (await context.DatabaseDataManagers.CountAsync(CancellationToken)).ShouldBe(2);
    }

    // --- The shared "who may read records" condition (used by #146/#147) ---------------------

    [Fact]
    public async Task DatabaseRecordReaders_requires_designation_and_permission_read_fresh_each_time()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        var otherDatabaseId = await CreateDatabaseAsync(admin, "另一個");
        await PutAccessAsync(admin, databaseId, [org.Internal.Id, org.Customer.Id]);

        async Task<bool> CanReadAsync(Guid accountId, Guid database)
        {
            // A new context and per-request permission cache each time, like a new request.
            await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
            var permissions = new RequestAccountPermissions(new DatabaseAccountPermissionSource(dbContext));
            return await DatabaseRecordReaders.CanReadAsync(dbContext, permissions, accountId, database, CancellationToken);
        }

        (await CanReadAsync(org.Internal.Id, databaseId)).ShouldBeTrue("designated and permitted");
        (await CanReadAsync(org.Customer.Id, databaseId)).ShouldBeFalse("designated, not permitted");
        (await CanReadAsync(org.Internal.Id, otherDatabaseId)).ShouldBeFalse("permitted, not designated on that database");
        (await CanReadAsync(org.Admin.Id, databaseId)).ShouldBeFalse("the owner who removed themselves reads nothing");
        (await CanReadAsync(org.Internal.Id, Guid.NewGuid())).ShouldBeFalse("no such database");

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var permissions = new RequestAccountPermissions(new DatabaseAccountPermissionSource(dbContext));
            (await DatabaseRecordReaders.ReadableDatabaseIdsAsync(dbContext, permissions, org.Internal.Id, CancellationToken))
                .ShouldBe([databaseId]);
            (await DatabaseRecordReaders.EffectiveReaderIdsAsync(dbContext, databaseId, CancellationToken))
                .ShouldBe([org.Internal.Id]);
        }

        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.UseSharedAssistants]);
        (await CanReadAsync(org.Internal.Id, databaseId)).ShouldBeFalse("permission revoked");

        await PutAccessAsync(admin, databaseId, [org.Admin.Id]);
        await SetPermissionsAsync(admin, org.Internal.Id, [AccountPermission.ReadConsentedSubmissions]);
        (await CanReadAsync(org.Internal.Id, databaseId)).ShouldBeFalse("designation revoked");
    }

    // --- Helpers ---------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Customer);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "安心商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AllAdminPermissions);
        var internalEmployee = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}客服同仁",
            AccountPermission.UseSharedAssistants,
            AccountPermission.ReadConsentedSubmissions);
        var customer = await _host.CreateAccountAsync(
            organization, "customer", Password, AccountRole.ExternalCustomer, $"{name}外部客戶",
            AccountPermission.SubmitAuthorizedForms,
            AccountPermission.ReadOwnTracking);

        return new TestOrganization(organization, admin, internalEmployee, customer);
    }

    /// <remarks>The client is never disposed: it only wraps the test host's in-memory handler.</remarks>
    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string name = "客戶資料庫")
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { templateId = "template-customer-profile", name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PutAccessAsync(SignedIn caller, Guid databaseId, Guid[] accountIds) =>
        caller.Spa.PutAsync($"{BasePath}/{databaseId}/access", caller.Token, new { dataManagerAccountIds = accountIds });

    private static async Task SetPermissionsAsync(SignedIn admin, Guid accountId, AccountPermission[] permissions)
    {
        var wire = permissions.Select(SmartAgri.Domain.WireNames<AccountPermission>.ToWire).ToArray();
        var response = await admin.Spa.PutAsync($"/api/v1/team/members/{accountId}/permissions", admin.Token, new { permissions = wire });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<JsonElement> GetDetailAsync(SignedIn viewer, Guid databaseId)
    {
        var response = await viewer.Spa.GetAsync($"{BasePath}/{databaseId}", viewer.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyJsonAsync(response);
    }

    private static async Task<JsonElement> ListAsync(SignedIn viewer)
    {
        var response = await viewer.Spa.GetAsync(BasePath, viewer.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyJsonAsync(response);
    }

    private static IEnumerable<Guid> DataManagerIds(JsonElement access) =>
        access.GetProperty("dataManagers").EnumerateArray().Select(m => m.GetProperty("account").GetProperty("id").GetGuid());

    /// <summary>Not in the list, and the detail is the same bytes as for an id that does not exist.</summary>
    private static async Task AssertInvisibleAsync(SignedIn caller, Guid databaseId, string who)
    {
        (await ListAsync(caller)).EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ShouldNotContain(databaseId, who);
        var detail = await caller.Spa.GetAsync($"{BasePath}/{databaseId}", caller.Token);
        detail.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
        await AssertIdenticalAsync(detail, await caller.Spa.GetAsync($"{BasePath}/{Guid.NewGuid()}", caller.Token));
        var raw = await detail.Content.ReadAsStringAsync(CancellationToken);
        JsonDocument.Parse(raw).RootElement.GetProperty("message").GetString().ShouldBe(ForbiddenMessage, who);
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
