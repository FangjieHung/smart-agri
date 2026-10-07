using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Cases;

/// <summary>
/// <c>/api/v1/case-groups</c> (M7 plan §3 A, §5 Slice M7-1; issue #246) against real PostgreSQL:
/// who may read and change, the whole-list member replacement with its history, the name rules,
/// archiving, and the shared 「已停用的帳號」 name.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class CaseGroupEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Case-Group-Pass-1!";
    private const string Path = "/api/v1/case-groups";

    private readonly AuthHostFixture _host;

    public CaseGroupEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Internal_accounts_read_the_list_with_member_names_and_external_customers_get_403_case()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        (await admin.Spa.PutAsync($"{Path}/{group}/members", admin.Token, new { accountIds = new[] { org.Internal.Id, org.Admin.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var member = await SignInAsync(org, "internal");
        var response = await member.Spa.GetAsync(Path, member.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "CaseGroupListView");
        body.GetProperty("canManage").GetBoolean().ShouldBeFalse();
        body.GetProperty("candidates").GetArrayLength().ShouldBe(0);
        var listed = body.GetProperty("groups").EnumerateArray().ShouldHaveSingleItem();
        OpenApiContract.AssertKeysMatchSchema(listed, "CaseGroupView");
        (listed.GetProperty("name").GetString(), listed.GetProperty("archived").GetBoolean()).ShouldBe(("設備組", false));
        MemberNames(listed).ShouldBe(["承辦商行同仁", "承辦商行管理者"], ignoreOrder: true);

        var external = await SignInAsync(org, "external");
        var denied = await external.Spa.GetAsync(Path, external.Token);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.CaseFeature));
        (await denied.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body);
        (await BodyJsonAsync(denied)).GetProperty("reason").GetString().ShouldBe("case");

        (await _host.CreateSpaClient().Http.GetAsync(Path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_managers_list_offers_only_internal_accounts_as_candidates()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var body = await BodyJsonAsync(await admin.Spa.GetAsync(Path, admin.Token));

        body.GetProperty("canManage").GetBoolean().ShouldBeTrue();
        body.GetProperty("candidates").EnumerateArray()
            .Select(candidate => (candidate.GetProperty("displayName").GetString(), candidate.GetProperty("role").GetString()))
            .ShouldBe([("承辦商行管理者", "smb-admin"), ("承辦商行同仁", "internal-employee"), ("承辦商行同仁二", "internal-employee")]);
    }

    [Fact]
    public async Task Every_write_by_a_non_manager_and_every_unknown_or_foreign_id_get_the_same_403()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var otherAdmin = await SignInAsync(other, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var foreignGroup = await CreateGroupAsync(otherAdmin, "外組");
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.OrganizationSettings));

        // A colleague with manage-assistants, and an external customer: never managers (decision A).
        foreach (var login in new[] { "internal", "external" })
        {
            var caller = await SignInAsync(org, login);
            foreach (var response in await WritesAsync(caller, group))
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, login);
                (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, login);
            }
        }

        // The manager, with an id that does not exist or belongs to another organization: the same bytes.
        foreach (var id in new[] { Guid.CreateVersion7(), foreignGroup })
        {
            foreach (var response in await WritesAsync(admin, id, includeCreate: false))
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body);
            }
        }

        var groups = await GroupsAsync(org);
        (groups.ShouldHaveSingleItem().Name, groups[0].IsArchived).ShouldBe(("設備組", false));
        (await MembersAsync(org)).ShouldBeEmpty();
        (await ActivitiesAsync(org)).Select(activity => activity.Action).ShouldBe([OrganizationActivityAction.CaseGroupCreated]);
        (await GroupsAsync(other)).ShouldHaveSingleItem().Name.ShouldBe("外組");
    }

    [Fact]
    public async Task A_blank_too_long_or_taken_name_is_each_its_own_422()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");

        var blank = await admin.Spa.PostAsync(Path, admin.Token, new { name = "   " });
        blank.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(blank)).GetProperty("errors").GetProperty("name")[0].GetString().ShouldBe("請輸入承辦組名稱。");

        var tooLong = await admin.Spa.PostAsync(Path, admin.Token, new { name = new string('組', 41) });
        tooLong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(tooLong)).GetProperty("errors").GetProperty("name")[0].GetString().ShouldBe("承辦組名稱請在 40 個字以內。");

        var longest = await admin.Spa.PostAsync(Path, admin.Token, new { name = new string('組', 40) });
        longest.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await admin.Spa.PostAsync(Path, admin.Token, new { name = "  設備組 " });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdBody = await BodyJsonAsync(created);
        OpenApiContract.AssertKeysMatchSchema(createdBody, "CaseGroupView");
        createdBody.GetProperty("name").GetString().ShouldBe("設備組");
        created.Headers.Location!.ToString().ShouldBe($"{Path}/{createdBody.GetProperty("id").GetGuid()}");

        var taken = await admin.Spa.PostAsync(Path, admin.Token, new { name = "設備組" });
        taken.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var takenBody = await BodyJsonAsync(taken);
        (takenBody.GetProperty("reason").GetString(), takenBody.GetProperty("errors").GetProperty("name")[0].GetString())
            .ShouldBe(("case-group-name-taken", "已經有同名的承辦組，請換一個名稱。"));

        // Renaming onto another group's name, too; renaming to its own name is a no-op.
        var purchasing = await CreateGroupAsync(admin, "採購組");
        var renameTaken = await admin.Spa.PutAsync($"{Path}/{purchasing}", admin.Token, new { name = "設備組" });
        renameTaken.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(renameTaken)).GetProperty("reason").GetString().ShouldBe("case-group-name-taken");
        var renameBlank = await admin.Spa.PutAsync($"{Path}/{purchasing}", admin.Token, new { name = "" });
        renameBlank.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await admin.Spa.PutAsync($"{Path}/{purchasing}", admin.Token, new { name = "採購組" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await GroupsAsync(org)).Select(group => group.Name).ShouldBe([new string('組', 40), "設備組", "採購組"], ignoreOrder: true);
        (await ActivitiesAsync(org)).Count.ShouldBe(3, "three creations, no rename");

        // Unique within the organization only.
        var other = await SignInAsync(await CreateOrganizationAsync(), "admin");
        (await other.Spa.PostAsync(Path, other.Token, new { name = "設備組" })).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_rename_archive_and_unarchive_go_back_and_forth_and_each_writes_one_activity()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");

        var renamed = await BodyJsonAsync(await admin.Spa.PutAsync($"{Path}/{group}", admin.Token, new { name = "設備維修組" }));
        renamed.GetProperty("name").GetString().ShouldBe("設備維修組");

        for (var round = 0; round < 2; round++)
        {
            var archived = await admin.Spa.PostAsync($"{Path}/{group}:archive", admin.Token, new { });
            archived.StatusCode.ShouldBe(HttpStatusCode.OK);
            var archivedBody = await BodyJsonAsync(archived);
            OpenApiContract.AssertKeysMatchSchema(archivedBody, "CaseGroupView");
            (archivedBody.GetProperty("archived").GetBoolean(), archivedBody.GetProperty("archivedAt").ValueKind)
                .ShouldBe((true, JsonValueKind.String));

            // Archiving again changes nothing and writes nothing.
            (await admin.Spa.PostAsync($"{Path}/{group}:archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

            var unarchived = await BodyJsonAsync(await admin.Spa.PostAsync($"{Path}/{group}:unarchive", admin.Token, new { }));
            (unarchived.GetProperty("archived").GetBoolean(), unarchived.GetProperty("archivedAt").ValueKind)
                .ShouldBe((false, JsonValueKind.Null));
            (await admin.Spa.PostAsync($"{Path}/{group}:unarchive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var activities = await ActivitiesAsync(org);
        activities.Select(activity => activity.Action).ShouldBe(
        [
            OrganizationActivityAction.CaseGroupCreated, OrganizationActivityAction.CaseGroupRenamed,
            OrganizationActivityAction.CaseGroupArchived, OrganizationActivityAction.CaseGroupUnarchived,
            OrganizationActivityAction.CaseGroupArchived, OrganizationActivityAction.CaseGroupUnarchived,
        ]);
        activities.ShouldAllBe(activity => activity.ActorAccountId == org.Admin.Id);
        using (var detail = JsonDocument.Parse(activities[1].Detail!))
        {
            detail.RootElement.GetProperty("id").GetGuid().ShouldBe(group);
            (detail.RootElement.GetProperty("name").GetString(), detail.RootElement.GetProperty("previousName").GetString())
                .ShouldBe(("設備維修組", "設備組"));
        }

        using (var detail = JsonDocument.Parse(activities[2].Detail!))
        {
            detail.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["id", "name"], ignoreOrder: true);
            detail.RootElement.GetProperty("name").GetString().ShouldBe("設備維修組");
        }
    }

    [Fact]
    public async Task An_archived_group_is_not_in_the_default_list_only_in_the_managers_full_list()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "internal");
        await CreateGroupAsync(admin, "設備組");
        var archived = await CreateGroupAsync(admin, "舊的組");
        (await admin.Spa.PostAsync($"{Path}/{archived}:archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ListedNamesAsync(admin, Path)).ShouldBe(["設備組"]);
        (await ListedNamesAsync(admin, $"{Path}?includeArchived=true")).ShouldBe(["設備組", "舊的組"]);
        (await ListedNamesAsync(member, Path)).ShouldBe(["設備組"]);
        (await ListedNamesAsync(member, $"{Path}?includeArchived=true")).ShouldBe(["設備組"], "only the manager sees archived groups");

        (await admin.Spa.PostAsync($"{Path}/{archived}:unarchive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListedNamesAsync(member, Path)).ShouldBe(["設備組", "舊的組"]);
    }

    [Fact]
    public async Task Members_are_replaced_as_a_whole_and_every_addition_and_removal_is_one_history_row()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var membersPath = $"{Path}/{group}/members";

        var first = await admin.Spa.PutAsync(membersPath, admin.Token, new { accountIds = new[] { org.Admin.Id, org.Internal.Id, org.Internal.Id } });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = await BodyJsonAsync(first);
        OpenApiContract.AssertKeysMatchSchema(firstBody, "CaseGroupView");
        MemberNames(firstBody).Count.ShouldBe(2);

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await BodyJsonAsync(
            await admin.Spa.PutAsync(membersPath, admin.Token, new { accountIds = new[] { org.Internal.Id, org.SecondInternal.Id } }));
        MemberNames(second).ShouldBe(["承辦商行同仁", "承辦商行同仁二"]);

        // The same list again: nothing written.
        (await admin.Spa.PutAsync(membersPath, admin.Token, new { accountIds = new[] { org.SecondInternal.Id, org.Internal.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var changes = await admin.Spa.GetAsync($"{Path}/{group}/member-changes", admin.Token);
        changes.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = (await BodyJsonAsync(changes)).EnumerateArray().ToList();
        OpenApiContract.AssertKeysMatchSchema(rows[0], "CaseGroupMemberChangeView");
        rows.Count.ShouldBe(4);
        rows.Take(2).Select(Change).ShouldBe([("承辦商行管理者", false), ("承辦商行同仁二", true)], ignoreOrder: true, "newest first");
        rows.Skip(2).Select(Change).ShouldBe([("承辦商行管理者", true), ("承辦商行同仁", true)], ignoreOrder: true);
        rows.ShouldAllBe(row => row.GetProperty("changedBy").GetProperty("displayName").GetString() == "承辦商行管理者");

        (await MembersAsync(org)).Select(member => member.AccountId).ShouldBe([org.Internal.Id, org.SecondInternal.Id], ignoreOrder: true);
        (await ActivitiesAsync(org)).Count.ShouldBe(1, "member changes have their own history");

        // An empty list removes everyone.
        MemberNames(await BodyJsonAsync(await admin.Spa.PutAsync(membersPath, admin.Token, new { accountIds = Array.Empty<Guid>() }))).ShouldBeEmpty();
        (await ChangeRowsAsync(org)).Count.ShouldBe(6);
    }

    [Fact]
    public async Task A_list_with_an_external_customer_or_another_organizations_account_is_422_and_nothing_is_saved()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var membersPath = $"{Path}/{group}/members";
        (await admin.Spa.PutAsync(membersPath, admin.Token, new { accountIds = new[] { org.Internal.Id } })).StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var outsider in new[] { org.External.Id, other.Internal.Id, Guid.CreateVersion7() })
        {
            var response = await admin.Spa.PutAsync(membersPath, admin.Token, new { accountIds = new[] { org.SecondInternal.Id, outsider } });

            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var body = await BodyJsonAsync(response);
            (body.GetProperty("reason").GetString(), body.GetProperty("errors").GetProperty("accountIds").GetArrayLength())
                .ShouldBe(("member-not-eligible", 1));
        }

        var missing = await admin.Spa.PutAsync(membersPath, admin.Token, new { accountIds = (Guid[]?)null });
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await MembersAsync(org)).ShouldHaveSingleItem().AccountId.ShouldBe(org.Internal.Id);
        (await ChangeRowsAsync(org)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_history_row_whose_account_can_no_longer_be_found_shows_the_deactivated_name()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var membersPath = $"{Path}/{group}/members";
        (await admin.Spa.PutAsync(membersPath, admin.Token, new { accountIds = new[] { org.SecondInternal.Id } })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A current member's account cannot be deleted (Restrict, decision C).
        await Should.ThrowAsync<DbUpdateException>(() => DeleteAccountAsync(org, org.SecondInternal.Id));

        (await admin.Spa.PutAsync(membersPath, admin.Token, new { accountIds = Array.Empty<Guid>() })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeleteAccountAsync(org, org.SecondInternal.Id);

        var rows = (await BodyJsonAsync(await admin.Spa.GetAsync($"{Path}/{group}/member-changes", admin.Token))).EnumerateArray().ToList();
        rows.Select(Change).ShouldBe([("已停用的帳號", false), ("已停用的帳號", true)]);
        rows[0].GetProperty("account").GetProperty("id").GetGuid().ShouldBe(org.SecondInternal.Id);
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private sealed record TestOrganization(
        Organization Organization, Account Admin, Account Internal, Account SecondInternal, Account External);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private async Task<TestOrganization> CreateOrganizationAsync()
    {
        var organization = await _host.CreateOrganizationAsync("承辦商行");
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "承辦商行管理者", AccountPermission.ManageAssistants);
        var member = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, "承辦商行同仁", AccountPermission.ManageAssistants);
        var second = await _host.CreateAccountAsync(
            organization, "internal2", Password, AccountRole.InternalEmployee, "承辦商行同仁二");
        var external = await _host.CreateAccountAsync(
            organization, "external", Password, AccountRole.ExternalCustomer, "承辦商行客戶", AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, admin, member, second, external);
    }

    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateGroupAsync(SignedIn admin, string name)
    {
        var response = await admin.Spa.PostAsync(Path, admin.Token, new { name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<List<HttpResponseMessage>> WritesAsync(SignedIn caller, Guid id, bool includeCreate = true) =>
    [
        .. includeCreate ? [await caller.Spa.PostAsync(Path, caller.Token, new { name = "新的組" })] : Array.Empty<HttpResponseMessage>(),
        await caller.Spa.PutAsync($"{Path}/{id}", caller.Token, new { name = "改名" }),
        await caller.Spa.PostAsync($"{Path}/{id}:archive", caller.Token, new { }),
        await caller.Spa.PostAsync($"{Path}/{id}:unarchive", caller.Token, new { }),
        await caller.Spa.PutAsync($"{Path}/{id}/members", caller.Token, new { accountIds = Array.Empty<Guid>() }),
        await caller.Spa.GetAsync($"{Path}/{id}/member-changes", caller.Token),
    ];

    private static async Task<List<string?>> ListedNamesAsync(SignedIn caller, string path) =>
        [.. (await BodyJsonAsync(await caller.Spa.GetAsync(path, caller.Token))).GetProperty("groups").EnumerateArray()
            .Select(group => group.GetProperty("name").GetString())];

    private static List<string?> MemberNames(JsonElement group) =>
        [.. group.GetProperty("members").EnumerateArray().Select(member => member.GetProperty("displayName").GetString())];

    private static (string?, bool) Change(JsonElement row) =>
        (row.GetProperty("account").GetProperty("displayName").GetString(), row.GetProperty("added").GetBoolean());

    private async Task<List<CaseGroup>> GroupsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.CaseGroups.AsNoTracking().ToListAsync(CancellationToken);
    }

    private async Task<List<CaseGroupMember>> MembersAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.CaseGroupMembers.AsNoTracking().ToListAsync(CancellationToken);
    }

    private async Task<List<CaseGroupMemberChange>> ChangeRowsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.CaseGroupMemberChanges.AsNoTracking().ToListAsync(CancellationToken);
    }

    private async Task<List<OrganizationActivity>> ActivitiesAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.OrganizationActivities.AsNoTracking()
            .OrderBy(activity => activity.At).ThenBy(activity => activity.Id)
            .ToListAsync(CancellationToken);
    }

    private async Task DeleteAccountAsync(TestOrganization org, Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var account = await dbContext.Accounts.SingleAsync(candidate => candidate.Id == accountId, CancellationToken);
        dbContext.Accounts.Remove(account);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
