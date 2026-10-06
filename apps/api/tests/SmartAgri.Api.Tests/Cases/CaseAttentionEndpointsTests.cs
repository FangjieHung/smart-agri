using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Cases;

/// <summary>
/// 逾期提示 over HTTP against real PostgreSQL (M7 plan §3 E, §5 Slice M7-5; issue #250):
/// <c>GET /api/v1/cases/attention</c> and the list's <c>overdue=true</c> with the host's injectable clock
/// (<see cref="TestClock"/>) — the boundary, 待補件 still counting, closed cases never counting, who an overdue
/// case counts for (the case owner, every member of a 待受理 case's current group, the new group after a
/// transfer, nothing extra for the manager), and the side navigation's number equalling the two list
/// filters. The pure rule is <c>CaseAttentionTests</c> (Application).
/// </summary>
/// <remarks>
/// Accounts (<see cref="CreateOrganizationAsync"/>): <c>admin</c> the manager (in no group),
/// <c>internal</c> who creates the cases (in no group), <c>member</c> 阿明 and <c>colleague</c> in 設備組,
/// <c>other</c> in 採購組, <c>external</c> a customer. The API never accepts a due time in the past
/// (decision H), so <see cref="MakeOverdueAsync"/> moves it straight in the database, as M7-11's
/// <c>case-set-due</c> operations command will.
/// </remarks>
[Trait("Category", TestCategories.Docker)]
public class CaseAttentionEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Case-Attention-Pass-1!";
    private const string Path = "/api/v1/cases";
    private const string AttentionPath = "/api/v1/cases/attention";
    private const string GroupsPath = "/api/v1/case-groups";
    private const string TypesPath = "/api/v1/case-types";

    private readonly AuthHostFixture _host;

    public CaseAttentionEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_case_becomes_overdue_only_once_the_clock_passes_its_due_time()
    {
        var (_, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup, _host.Clock.GetUtcNow().AddMinutes(1));

        (await AttentionAsync(who["member"])).ShouldBe(new Attention(0, 0, 0, 1), "not yet due: only 待我受理");
        (await ListedIdsAsync(who["member"], $"{Path}?overdue=true")).ShouldBeEmpty();
        (await ListedIdsAsync(who["member"], $"{Path}?overdue=false")).ShouldBe([caseId], "false filters nothing");

        _host.Clock.Advance(TimeSpan.FromMinutes(2));

        (await AttentionAsync(who["member"])).ShouldBe(new Attention(1, 0, 1, 1));
        (await ListedIdsAsync(who["member"], $"{Path}?overdue=true")).ShouldBe([caseId]);
        (await ListedIdsAsync(who["member"], $"{Path}?scope=my-groups&status=pending&overdue=true")).ShouldBe([caseId]);
    }

    [Fact]
    public async Task Awaiting_info_keeps_counting_and_completed_or_cancelled_cases_never_count()
    {
        var (org, setup, who) = await ArrangeAsync();
        var inProgress = await CreateCaseAsync(who["internal"], setup, title: "處理中");
        var awaiting = await CreateCaseAsync(who["internal"], setup, title: "待補件");
        var completed = await CreateCaseAsync(who["internal"], setup, title: "已完成");
        var cancelled = await CreateCaseAsync(who["internal"], setup, title: "已取消");
        foreach (var id in new[] { inProgress, awaiting, completed, cancelled })
        {
            await ActAsync(org, who["member"], id, "accept");
        }

        await ActAsync(org, who["member"], awaiting, "request-info", new { note = "請補上溫度紀錄的照片" });
        await ActAsync(org, who["member"], completed, "complete", new { resolution = "已更換壓縮機" });
        await ActAsync(org, who["member"], cancelled, "cancel", new { reason = "重複的案件" });
        foreach (var id in new[] { inProgress, awaiting, completed, cancelled })
        {
            await MakeOverdueAsync(org, id);
        }

        (await AttentionAsync(who["member"])).ShouldBe(new Attention(2, 2, 0, 0));
        (await ListedIdsAsync(who["member"], $"{Path}?scope=owned&overdue=true")).ShouldBe([awaiting, inProgress], ignoreOrder: true);
        (await ListedIdsAsync(who["member"], $"{Path}?status=all&overdue=true")).ShouldBe([awaiting, inProgress], ignoreOrder: true);
        (await ListedIdsAsync(who["member"], $"{Path}?status=closed&overdue=true")).ShouldBeEmpty("a closed case is never overdue");
        (await ListedIdsAsync(who["member"], $"{Path}?status=awaiting-info&overdue=true")).ShouldBe([awaiting]);
    }

    [Fact]
    public async Task A_pending_case_counts_for_its_groups_members_then_for_its_owner_then_for_the_new_group_and_never_extra_for_the_manager()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);
        await MakeOverdueAsync(org, caseId);

        await ShouldCountAsync(who, "待受理 in 設備組", new()
        {
            ["member"] = new Attention(1, 0, 1, 1),
            ["colleague"] = new Attention(1, 0, 1, 1),
            ["other"] = new Attention(0, 0, 0, 0),
            ["internal"] = new Attention(0, 0, 0, 0),
            ["admin"] = new Attention(0, 0, 0, 0),
        });
        (await ListedIdsAsync(who["admin"], $"{Path}?overdue=true")).ShouldBe([caseId], "the manager sees it in the list, but it is not counted for them");

        await ActAsync(org, who["member"], caseId, "accept");
        await ShouldCountAsync(who, "accepted by 阿明", new()
        {
            ["member"] = new Attention(1, 1, 0, 0),
            ["colleague"] = new Attention(0, 0, 0, 0),
            ["other"] = new Attention(0, 0, 0, 0),
            ["internal"] = new Attention(0, 0, 0, 0),
            ["admin"] = new Attention(0, 0, 0, 0),
        });

        await ActAsync(org, who["member"], caseId, "transfer", new { groupId = setup.Purchasing, note = "請採購組詢價" });
        await ShouldCountAsync(who, "transferred to 採購組", new()
        {
            ["member"] = new Attention(0, 0, 0, 0),
            ["colleague"] = new Attention(0, 0, 0, 0),
            ["other"] = new Attention(1, 0, 1, 1),
            ["internal"] = new Attention(0, 0, 0, 0),
            ["admin"] = new Attention(0, 0, 0, 0),
        });
        (await ListedIdsAsync(who["member"], $"{Path}?overdue=true")).ShouldBe([caseId], "阿明 still sees it, it is just not his any more");
    }

    [Fact]
    public async Task The_side_navigations_number_equals_the_owned_and_my_groups_pending_filters_with_overdue()
    {
        var (org, setup, who) = await ArrangeAsync();
        var ownedOverdue = await CreateCaseAsync(who["internal"], setup, title: "我負責、逾期");
        var ownedOverdueAwaiting = await CreateCaseAsync(who["internal"], setup, title: "我負責、待補件、逾期");
        var ownedOnTime = await CreateCaseAsync(who["internal"], setup, title: "我負責、未逾期");
        var colleaguesOverdue = await CreateCaseAsync(who["internal"], setup, title: "同事負責、逾期");
        var pendingOverdue = await CreateCaseAsync(who["internal"], setup, title: "待受理、逾期");
        var pendingOnTime = await CreateCaseAsync(who["internal"], setup, title: "待受理、未逾期");
        foreach (var id in new[] { ownedOverdue, ownedOverdueAwaiting, ownedOnTime })
        {
            await ActAsync(org, who["member"], id, "accept");
        }

        await ActAsync(org, who["member"], ownedOverdueAwaiting, "request-info", new { note = "請補照片" });
        await ActAsync(org, who["colleague"], colleaguesOverdue, "accept");
        foreach (var id in new[] { ownedOverdue, ownedOverdueAwaiting, colleaguesOverdue, pendingOverdue })
        {
            await MakeOverdueAsync(org, id);
        }

        var attention = await AttentionAsync(who["member"]);
        attention.ShouldBe(new Attention(3, 2, 1, 2));

        var owned = await ListedIdsAsync(who["member"], $"{Path}?scope=owned&overdue=true");
        var groupPending = await ListedIdsAsync(who["member"], $"{Path}?scope=my-groups&status=pending&overdue=true");
        owned.ShouldBe([ownedOverdueAwaiting, ownedOverdue], ignoreOrder: true);
        groupPending.ShouldBe([pendingOverdue]);
        (owned.Count + groupPending.Count).ShouldBe(attention.OverdueCount);
        (await ListedIdsAsync(who["member"], $"{Path}?scope=my-groups&status=pending")).ShouldBe([pendingOnTime, pendingOverdue], ignoreOrder: true);
        attention.PendingForMeCount.ShouldBe(2);

        // The colleague's own overdue case and the same group's 待受理 one.
        (await AttentionAsync(who["colleague"])).ShouldBe(new Attention(2, 1, 1, 2));
    }

    [Fact]
    public async Task External_customers_get_the_one_403_case_and_other_organizations_cases_never_count()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);
        await MakeOverdueAsync(org, caseId);

        var (otherOrg, otherSetup, otherWho) = await ArrangeAsync("別家商行");
        var otherCase = await CreateCaseAsync(otherWho["internal"], otherSetup);
        await MakeOverdueAsync(otherOrg, otherCase);

        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.CaseFeature));
        var external = await who["external"].Spa.GetAsync(AttentionPath, who["external"].Token);
        external.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await external.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body);
        (await _host.CreateSpaClient().Http.GetAsync(AttentionPath, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await AttentionAsync(who["member"])).ShouldBe(new Attention(1, 0, 1, 1), "only this organization's case");
        (await AttentionAsync(otherWho["member"])).ShouldBe(new Attention(1, 0, 1, 1), "only the other organization's case");
        (await ListedIdsAsync(otherWho["member"], $"{Path}?overdue=true")).ShouldBe([otherCase]);
    }

    // --- Helpers ---------------------------------------------------------------------------------

    /// <summary>One caller's <c>CaseAttentionView</c>.</summary>
    private sealed record Attention(int OverdueCount, int OwnedOverdueCount, int GroupPendingOverdueCount, int PendingForMeCount);

    private sealed record TestOrganization(Organization Organization, Account Member, Account Colleague, Account Other);

    private sealed record Setup(Guid Equipment, Guid Purchasing, Guid Type);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private async Task<(TestOrganization Org, Setup Setup, Dictionary<string, SignedIn> Who)> ArrangeAsync(string name = "逾期商行")
    {
        var org = await CreateOrganizationAsync(name);
        var setup = await SetUpAsync(org);
        var who = new Dictionary<string, SignedIn>();
        foreach (var login in new[] { "admin", "internal", "member", "colleague", "other", "external" })
        {
            who[login] = await SignInAsync(org, login);
        }

        return (org, setup, who);
    }

    private async Task<TestOrganization> CreateOrganizationAsync(string name)
    {
        var organization = await _host.CreateOrganizationAsync(name);
        await _host.CreateAccountAsync(organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AccountPermission.ManageAssistants);
        await _host.CreateAccountAsync(organization, "internal", Password, AccountRole.InternalEmployee, $"{name}同仁", AccountPermission.UseSharedAssistants);
        var member = await _host.CreateAccountAsync(
            organization, "member", Password, AccountRole.InternalEmployee, $"{name}阿明", AccountPermission.UseSharedAssistants);
        var colleague = await _host.CreateAccountAsync(
            organization, "colleague", Password, AccountRole.InternalEmployee, $"{name}設備組同事", AccountPermission.UseSharedAssistants);
        var other = await _host.CreateAccountAsync(
            organization, "other", Password, AccountRole.InternalEmployee, $"{name}採購組成員", AccountPermission.UseSharedAssistants);
        await _host.CreateAccountAsync(organization, "external", Password, AccountRole.ExternalCustomer, $"{name}客戶", AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, member, colleague, other);
    }

    /// <summary>設備組 (阿明, colleague), 採購組 (other), and the type 設備故障報修 defaulting to 設備組, 72 hours.</summary>
    private async Task<Setup> SetUpAsync(TestOrganization org)
    {
        var admin = await SignInAsync(org, "admin");
        var equipment = await CreateGroupAsync(admin, "設備組");
        var purchasing = await CreateGroupAsync(admin, "採購組");
        (await admin.Spa.PutAsync($"{GroupsPath}/{equipment}/members", admin.Token, new { accountIds = new[] { org.Member.Id, org.Colleague.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.Spa.PutAsync($"{GroupsPath}/{purchasing}/members", admin.Token, new { accountIds = new[] { org.Other.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var type = await admin.Spa.PostAsync(TypesPath, admin.Token, new
        {
            name = "設備故障報修", description = "", defaultGroupId = equipment, defaultDueHours = 72, isActive = true,
        });
        type.StatusCode.ShouldBe(HttpStatusCode.Created, await type.Content.ReadAsStringAsync(CancellationToken));
        return new Setup(equipment, purchasing, (await BodyJsonAsync(type)).GetProperty("id").GetGuid());
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

    /// <summary>A case in 設備組, due in 72 hours unless <paramref name="dueAt"/> is given.</summary>
    private async Task<Guid> CreateCaseAsync(SignedIn caller, Setup setup, DateTimeOffset? dueAt = null, string title = "冷藏庫溫度降不下來")
    {
        var response = await caller.Spa.PostAsync(Path, caller.Token, new
        {
            typeId = setup.Type,
            groupId = setup.Equipment,
            dueAt = dueAt ?? _host.Clock.GetUtcNow().AddHours(72),
            title,
            description = "說明",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("case").GetProperty("id").GetGuid();
    }

    /// <summary>Moves the due time an hour into the past, straight in the database (the API refuses a
    /// due time in the past, decision H; M7-11's <c>case-set-due</c> does the same for the E2E).</summary>
    private async Task MakeOverdueAsync(TestOrganization org, Guid caseId)
    {
        var past = _host.Clock.GetUtcNow().AddHours(-1);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.Cases.Where(item => item.Id == caseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DueAt, past), CancellationToken))
            .ShouldBe(1);
    }

    /// <summary>Posts <paramref name="action"/> with the case's current <c>eventCount</c>; it must succeed.</summary>
    private async Task ActAsync(TestOrganization org, SignedIn caller, Guid caseId, string action, object? fields = null)
    {
        int eventCount;
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            eventCount = (await dbContext.Cases.AsNoTracking().SingleAsync(item => item.Id == caseId, CancellationToken)).EventCount;
        }

        var body = new Dictionary<string, object?> { ["eventCount"] = eventCount };
        if (fields is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(fields).EnumerateObject())
            {
                body[property.Name] = property.Value.Clone();
            }
        }

        var response = await caller.Spa.PostAsync($"{Path}/{caseId}:{action}", caller.Token, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private async Task ShouldCountAsync(Dictionary<string, SignedIn> who, string because, Dictionary<string, Attention> expected)
    {
        foreach (var (login, attention) in expected)
        {
            (await AttentionAsync(who[login])).ShouldBe(attention, $"{because}: {login}");
        }
    }

    private static async Task<Attention> AttentionAsync(SignedIn caller)
    {
        var response = await caller.Spa.GetAsync(AttentionPath, caller.Token);
        var raw = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        var body = JsonDocument.Parse(raw).RootElement;
        OpenApiContract.AssertKeysMatchSchema(body, "CaseAttentionView");
        return new Attention(
            body.GetProperty("overdueCount").GetInt32(),
            body.GetProperty("ownedOverdueCount").GetInt32(),
            body.GetProperty("groupPendingOverdueCount").GetInt32(),
            body.GetProperty("pendingForMeCount").GetInt32());
    }

    private static async Task<List<Guid>> ListedIdsAsync(SignedIn caller, string path)
    {
        var response = await caller.Spa.GetAsync(path, caller.Token);
        var raw = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        return [.. JsonDocument.Parse(raw).RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetGuid())];
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
