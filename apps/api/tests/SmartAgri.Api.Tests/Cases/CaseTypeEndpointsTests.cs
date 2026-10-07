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
/// <c>/api/v1/case-types</c> (M7 plan §3 B, §5 Slice M7-2; issue #247) against real PostgreSQL: who
/// may read and change, the field rules (handling time 1–2,160 hours), the default group's archived
/// state on both sides, deactivation, and the activity log.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class CaseTypeEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Case-Type-Pass-1!";
    private const string Path = "/api/v1/case-types";
    private const string GroupsPath = "/api/v1/case-groups";

    private readonly AuthHostFixture _host;

    public CaseTypeEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Internal_accounts_read_the_active_types_and_only_the_manager_sees_inactive_ones()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var active = await CreateTypeAsync(admin, "設備故障報修", group, 72, description: "冷藏庫、溫控設備故障");
        await CreateTypeAsync(admin, "舊的類型", group, 24, isActive: false);

        var member = await SignInAsync(org, "internal");
        var response = await member.Spa.GetAsync(Path, member.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "CaseTypeListView");
        body.GetProperty("canManage").GetBoolean().ShouldBeFalse();
        var listed = body.GetProperty("types").EnumerateArray().ShouldHaveSingleItem();
        OpenApiContract.AssertKeysMatchSchema(listed, "CaseTypeView");
        OpenApiContract.AssertKeysMatchSchema(listed.GetProperty("defaultGroup"), "CaseTypeGroupView");
        listed.GetProperty("id").GetGuid().ShouldBe(active);
        (listed.GetProperty("name").GetString(), listed.GetProperty("description").GetString(),
                listed.GetProperty("defaultDueHours").GetInt32(), listed.GetProperty("isActive").GetBoolean())
            .ShouldBe(("設備故障報修", "冷藏庫、溫控設備故障", 72, true));
        (listed.GetProperty("defaultGroup").GetProperty("id").GetGuid(), listed.GetProperty("defaultGroup").GetProperty("name").GetString())
            .ShouldBe((group, "設備組"));

        (await ListedNamesAsync(member, $"{Path}?includeInactive=true")).ShouldBe(["設備故障報修"], "includeInactive is the manager's only");
        (await ListedNamesAsync(admin, Path)).ShouldBe(["設備故障報修"]);
        (await ListedNamesAsync(admin, $"{Path}?includeInactive=true")).ShouldBe(["設備故障報修", "舊的類型"]);
        (await BodyJsonAsync(await admin.Spa.GetAsync(Path, admin.Token))).GetProperty("canManage").GetBoolean().ShouldBeTrue();

        var external = await SignInAsync(org, "external");
        var denied = await external.Spa.GetAsync(Path, external.Token);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.CaseFeature));
        (await denied.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body);

        (await _host.CreateSpaClient().Http.GetAsync(Path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Every_write_by_a_non_manager_and_every_unknown_or_foreign_id_get_the_same_403()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var otherAdmin = await SignInAsync(other, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var type = await CreateTypeAsync(admin, "設備故障報修", group, 72);
        var foreignType = await CreateTypeAsync(otherAdmin, "外部類型", await CreateGroupAsync(otherAdmin, "外組"), 24);
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.OrganizationSettings));
        var body = Body("改名", group, 24);

        foreach (var login in new[] { "internal", "external" })
        {
            var caller = await SignInAsync(org, login);
            foreach (var response in new[]
                     {
                         await caller.Spa.PostAsync(Path, caller.Token, Body("新的類型", group, 24)),
                         await caller.Spa.PutAsync($"{Path}/{type}", caller.Token, body),
                     })
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, login);
                (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, login);
            }
        }

        foreach (var id in new[] { Guid.CreateVersion7(), foreignType })
        {
            var response = await admin.Spa.PutAsync($"{Path}/{id}", admin.Token, body);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body);
        }

        (await TypesAsync(org)).ShouldHaveSingleItem().Name.ShouldBe("設備故障報修");
        (await TypesAsync(other)).ShouldHaveSingleItem().Name.ShouldBe("外部類型");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2160, true)]
    [InlineData(2161, false)]
    public async Task The_handling_time_is_one_to_2160_hours_on_create_and_update(int hours, bool accepted)
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var existing = await CreateTypeAsync(admin, "既有類型", group, 24);

        var created = await admin.Spa.PostAsync(Path, admin.Token, Body("新類型", group, hours));
        var updated = await admin.Spa.PutAsync($"{Path}/{existing}", admin.Token, Body("既有類型", group, hours));

        foreach (var response in new[] { created, updated })
        {
            if (accepted)
            {
                response.IsSuccessStatusCode.ShouldBeTrue();
                (await BodyJsonAsync(response)).GetProperty("defaultDueHours").GetInt32().ShouldBe(hours);
            }
            else
            {
                response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await BodyJsonAsync(response)).GetProperty("errors").GetProperty("defaultDueHours")[0].GetString()
                    .ShouldBe("預設處理時限請在 1 到 2,160 小時（90 天）之間。");
            }
        }

        (await TypesAsync(org)).Single(type => type.Id == existing).DefaultDueHours.ShouldBe(accepted ? hours : 24);
        (await TypesAsync(org)).Count.ShouldBe(accepted ? 2 : 1);
    }

    [Fact]
    public async Task A_blank_too_long_or_taken_name_and_a_too_long_description_are_each_their_own_422()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");

        async Task<JsonElement> RefusedAsync(HttpResponseMessage response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            return await BodyJsonAsync(response);
        }

        (await RefusedAsync(await admin.Spa.PostAsync(Path, admin.Token, Body("   ", group, 24))))
            .GetProperty("errors").GetProperty("name")[0].GetString().ShouldBe("請輸入案件類型名稱。");
        (await RefusedAsync(await admin.Spa.PostAsync(Path, admin.Token, Body(new string('類', 41), group, 24))))
            .GetProperty("errors").GetProperty("name")[0].GetString().ShouldBe("案件類型名稱請在 40 個字以內。");
        var description = await RefusedAsync(await admin.Spa.PostAsync(Path, admin.Token, Body("設備", group, 24, new string('說', 501))));
        description.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe(["description"]);
        description.GetProperty("errors").GetProperty("description")[0].GetString().ShouldBe("說明請在 500 個字以內。");

        var longest = await admin.Spa.PostAsync(Path, admin.Token, Body(new string('類', 40), group, 24, new string('說', 500)));
        longest.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await admin.Spa.PostAsync(Path, admin.Token, Body("  設備故障報修 ", group, 24, "  說明  "));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdBody = await BodyJsonAsync(created);
        (createdBody.GetProperty("name").GetString(), createdBody.GetProperty("description").GetString()).ShouldBe(("設備故障報修", "說明"));
        created.Headers.Location!.ToString().ShouldBe($"{Path}/{createdBody.GetProperty("id").GetGuid()}");

        var taken = await RefusedAsync(await admin.Spa.PostAsync(Path, admin.Token, Body("設備故障報修", group, 24)));
        (taken.GetProperty("reason").GetString(), taken.GetProperty("errors").GetProperty("name")[0].GetString())
            .ShouldBe(("case-type-name-taken", "已經有同名的案件類型，請換一個名稱。"));

        // An inactive type keeps its name; renaming onto another type's name is refused too.
        var inactive = await CreateTypeAsync(admin, "停用的類型", group, 24, isActive: false);
        (await RefusedAsync(await admin.Spa.PostAsync(Path, admin.Token, Body("停用的類型", group, 24))))
            .GetProperty("reason").GetString().ShouldBe("case-type-name-taken");
        (await RefusedAsync(await admin.Spa.PutAsync($"{Path}/{inactive}", admin.Token, Body("設備故障報修", group, 24))))
            .GetProperty("reason").GetString().ShouldBe("case-type-name-taken");

        (await TypesAsync(org)).Count.ShouldBe(3);

        var otherAdmin = await SignInAsync(await CreateOrganizationAsync(), "admin");
        (await otherAdmin.Spa.PostAsync(Path, otherAdmin.Token, Body("設備故障報修", await CreateGroupAsync(otherAdmin, "設備組"), 24)))
            .StatusCode.ShouldBe(HttpStatusCode.Created, "unique within the organization only");
    }

    [Fact]
    public async Task The_default_group_must_be_one_of_the_organizations_groups_in_use()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var archived = await CreateGroupAsync(admin, "舊倉儲組");
        (await admin.Spa.PostAsync($"{GroupsPath}/{archived}:archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var otherAdmin = await SignInAsync(await CreateOrganizationAsync(), "admin");
        var foreignGroup = await CreateGroupAsync(otherAdmin, "外組");
        var type = await CreateTypeAsync(admin, "設備故障報修", group, 24);

        foreach (var response in new[]
                 {
                     await admin.Spa.PostAsync(Path, admin.Token, Body("新類型", archived, 24)),
                     await admin.Spa.PutAsync($"{Path}/{type}", admin.Token, Body("設備故障報修", archived, 24)),
                     await admin.Spa.PutAsync($"{Path}/{type}", admin.Token, Body("設備故障報修", archived, 24, isActive: false)),
                 })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var refused = await BodyJsonAsync(response);
            (refused.GetProperty("reason").GetString(), refused.GetProperty("errors").GetProperty("defaultGroupId")[0].GetString())
                .ShouldBe(("case-group-archived", "這個承辦組已封存，請選擇其他承辦組。"));
        }

        foreach (var unknown in new[] { foreignGroup, Guid.CreateVersion7() })
        {
            var response = await admin.Spa.PostAsync(Path, admin.Token, Body("新類型", unknown, 24));
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe("case-group-not-found");
        }

        var missing = await admin.Spa.PostAsync(Path, admin.Token, new { name = "新類型", defaultDueHours = 24 });
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(missing)).GetProperty("errors").GetProperty("defaultGroupId")[0].GetString().ShouldBe("請選擇預設承辦組。");

        (await TypesAsync(org)).ShouldHaveSingleItem().DefaultGroupId.ShouldBe(group);
    }

    [Fact]
    public async Task A_group_that_an_active_type_defaults_to_cannot_be_archived_until_the_type_is_deactivated()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var repair = await CreateTypeAsync(admin, "設備故障報修", group, 72);
        var cold = await CreateTypeAsync(admin, "冷藏庫異常", group, 24);
        await CreateTypeAsync(admin, "停用的類型", group, 24, isActive: false);

        var refused = await admin.Spa.PostAsync($"{GroupsPath}/{group}:archive", admin.Token, new { });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var refusedBody = await BodyJsonAsync(refused);
        refusedBody.GetProperty("reason").GetString().ShouldBe("case-group-in-use");
        refusedBody.GetProperty("message").GetString().ShouldBe(
            "「設備組」是啟用中的案件類型「設備故障報修」、「冷藏庫異常」的預設承辦組。請先替這些類型換一個承辦組，或停用它們，再封存。");
        refusedBody.GetProperty("errors").GetProperty("caseTypes").GetArrayLength().ShouldBe(1);
        (await GroupsAsync(org)).ShouldHaveSingleItem().IsArchived.ShouldBeFalse();

        // Move one type to another group, deactivate the other: now it can be archived.
        var purchasing = await CreateGroupAsync(admin, "採購組");
        (await admin.Spa.PutAsync($"{Path}/{repair}", admin.Token, Body("設備故障報修", purchasing, 72))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var stillRefused = await BodyJsonAsync(await admin.Spa.PostAsync($"{GroupsPath}/{group}:archive", admin.Token, new { }));
        stillRefused.GetProperty("message").GetString()!.ShouldStartWith("「設備組」是啟用中的案件類型「冷藏庫異常」的預設承辦組。");
        (await admin.Spa.PutAsync($"{Path}/{cold}", admin.Token, Body("冷藏庫異常", group, 24, isActive: false))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.Spa.PostAsync($"{GroupsPath}/{group}:archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The inactive type keeps its archived group (and says so), may be edited, but not reactivated with it.
        var listed = (await BodyJsonAsync(await admin.Spa.GetAsync($"{Path}?includeInactive=true", admin.Token))).GetProperty("types")
            .EnumerateArray().Single(type => type.GetProperty("id").GetGuid() == cold);
        listed.GetProperty("defaultGroup").GetProperty("archived").GetBoolean().ShouldBeTrue();
        (await admin.Spa.PutAsync($"{Path}/{cold}", admin.Token, Body("冷藏庫異常", group, 48, isActive: false))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var reactivate = await admin.Spa.PutAsync($"{Path}/{cold}", admin.Token, Body("冷藏庫異常", group, 48, isActive: true));
        reactivate.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(reactivate)).GetProperty("reason").GetString().ShouldBe("case-group-archived");
        (await admin.Spa.PutAsync($"{Path}/{cold}", admin.Token, Body("冷藏庫異常", purchasing, 48, isActive: true))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The database refuses to delete a group a type points at (Restrict), whatever the code does.
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        dbContext.CaseGroups.Remove(await dbContext.CaseGroups.SingleAsync(candidate => candidate.Id == purchasing, CancellationToken));
        await Should.ThrowAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(CancellationToken));
    }

    [Fact]
    public async Task Creating_and_each_real_change_write_one_activity_and_a_no_op_writes_nothing()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var group = await CreateGroupAsync(admin, "設備組");
        var type = await CreateTypeAsync(admin, "設備故障報修", group, 72, description: "冷藏庫故障");

        var same = await admin.Spa.PutAsync($"{Path}/{type}", admin.Token, Body("設備故障報修", group, 72, "冷藏庫故障"));
        same.StatusCode.ShouldBe(HttpStatusCode.OK);
        OpenApiContract.AssertKeysMatchSchema(await BodyJsonAsync(same), "CaseTypeView");

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        var renamed = await BodyJsonAsync(
            await admin.Spa.PutAsync($"{Path}/{type}", admin.Token, Body("設備報修", group, 48, "冷藏庫、溫控設備故障")));
        (renamed.GetProperty("name").GetString(), renamed.GetProperty("defaultDueHours").GetInt32()).ShouldBe(("設備報修", 48));

        // Omitting isActive keeps it; then deactivate.
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        var deactivated = await BodyJsonAsync(await admin.Spa.PutAsync(
            $"{Path}/{type}", admin.Token, Body("設備報修", group, 48, "冷藏庫、溫控設備故障", isActive: false)));
        deactivated.GetProperty("isActive").GetBoolean().ShouldBeFalse();

        var activities = (await ActivitiesAsync(org)).Where(activity => activity.Action is
            OrganizationActivityAction.CaseTypeCreated or OrganizationActivityAction.CaseTypeUpdated).ToList();
        activities.Select(activity => activity.Action).ShouldBe(
        [
            OrganizationActivityAction.CaseTypeCreated, OrganizationActivityAction.CaseTypeUpdated, OrganizationActivityAction.CaseTypeUpdated,
        ]);
        activities.ShouldAllBe(activity => activity.ActorAccountId == org.Admin.Id);
        using (var detail = JsonDocument.Parse(activities[0].Detail!))
        {
            (detail.RootElement.GetProperty("id").GetGuid(), detail.RootElement.GetProperty("name").GetString()).ShouldBe((type, "設備故障報修"));
        }

        using (var detail = JsonDocument.Parse(activities[1].Detail!))
        {
            detail.RootElement.GetProperty("name").GetString().ShouldBe("設備報修");
            detail.RootElement.GetProperty("changed").EnumerateArray().Select(field => field.GetString())
                .ShouldBe(["name", "description", "defaultDueHours"]);
            activities[1].Detail!.ShouldNotContain("溫控", Shouldly.Case.Sensitive, "the description's text is never recorded");
        }

        using (var detail = JsonDocument.Parse(activities[2].Detail!))
        {
            detail.RootElement.GetProperty("changed").EnumerateArray().Select(field => field.GetString()).ShouldBe(["isActive"]);
            detail.RootElement.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        }
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private static object Body(string name, Guid groupId, int hours, string? description = null, bool? isActive = null) =>
        new { name, description, defaultGroupId = groupId, defaultDueHours = hours, isActive };

    private async Task<TestOrganization> CreateOrganizationAsync()
    {
        var organization = await _host.CreateOrganizationAsync("類型商行");
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "類型商行管理者", AccountPermission.ManageAssistants);
        await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, "類型商行同仁", AccountPermission.ManageAssistants);
        await _host.CreateAccountAsync(
            organization, "external", Password, AccountRole.ExternalCustomer, "類型商行客戶", AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, admin);
    }

    private async Task<SignedIn> SignInAsync(TestOrganization org, string loginName)
    {
        var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(org.Organization.Code, loginName, Password);
        return new SignedIn(spa, token.AccessToken);
    }

    private static async Task<Guid> CreateGroupAsync(SignedIn admin, string name)
    {
        var response = await admin.Spa.PostAsync(GroupsPath, admin.Token, new { name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateTypeAsync(
        SignedIn admin, string name, Guid groupId, int hours, string? description = null, bool? isActive = null)
    {
        var response = await admin.Spa.PostAsync(Path, admin.Token, Body(name, groupId, hours, description, isActive));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "CaseTypeView");
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<List<string?>> ListedNamesAsync(SignedIn caller, string path) =>
        [.. (await BodyJsonAsync(await caller.Spa.GetAsync(path, caller.Token))).GetProperty("types").EnumerateArray()
            .Select(type => type.GetProperty("name").GetString())];

    private async Task<List<CaseType>> TypesAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.CaseTypes.AsNoTracking().ToListAsync(CancellationToken);
    }

    private async Task<List<CaseGroup>> GroupsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.CaseGroups.AsNoTracking().ToListAsync(CancellationToken);
    }

    private async Task<List<OrganizationActivity>> ActivitiesAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.OrganizationActivities.AsNoTracking()
            .OrderBy(activity => activity.At).ThenBy(activity => activity.Id)
            .ToListAsync(CancellationToken);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
