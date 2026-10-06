using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Cases;

/// <summary>
/// <c>/api/v1/cases</c> (M7 plan §3 C, §5 Slice M7-3; issue #248) against real PostgreSQL: who sees a
/// case (<c>CaseVisibility</c> through EF Core) and the one <c>403 case</c> for everyone else, every
/// <c>422</c> of creating, the list's default and filters, the links' states (never any conversation
/// text), and archiving a group that still has open cases.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class CaseEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Case-Endpoint-Pass-1!";
    private const string Path = "/api/v1/cases";
    private const string GroupsPath = "/api/v1/case-groups";
    private const string TypesPath = "/api/v1/case-types";

    private readonly AuthHostFixture _host;

    public CaseEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_creator_current_group_members_and_the_manager_see_a_case_and_everyone_else_gets_the_same_403()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync("別家商行");
        var setup = await SetUpAsync(org);
        var internalCaller = await SignInAsync(org, "internal");
        var caseId = await CreateCaseAsync(internalCaller, setup.Type, setup.Equipment, "冷藏庫溫度降不下來");

        foreach (var login in new[] { "internal", "member", "admin" })
        {
            var caller = await SignInAsync(org, login);
            var response = await caller.Spa.GetAsync($"{Path}/{caseId}", caller.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, login);
            var body = await BodyJsonAsync(response);
            OpenApiContract.AssertKeysMatchSchema(body, "CaseDetailView");
            OpenApiContract.AssertKeysMatchSchema(body.GetProperty("case"), "CaseView");
            OpenApiContract.AssertKeysMatchSchema(body.GetProperty("links"), "CaseLinksView");
            body.GetProperty("case").GetProperty("id").GetGuid().ShouldBe(caseId, login);
            (await ListedIdsAsync(caller, Path)).ShouldBe([caseId], login);
        }

        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.CaseFeature));
        var outsider = await SignInAsync(org, "other");
        var external = await SignInAsync(org, "external");
        var otherAdmin = await SignInAsync(other, "admin");
        foreach (var (who, caller, id) in new[]
                 {
                     ("internal account outside the group", outsider, caseId),
                     ("external customer", external, caseId),
                     ("another organization's manager", otherAdmin, caseId),
                     ("an id that does not exist", internalCaller, Guid.CreateVersion7()),
                 })
        {
            var response = await caller.Spa.GetAsync($"{Path}/{id}", caller.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, who);
        }

        (await ListedIdsAsync(outsider, Path)).ShouldBeEmpty();
        (await ListedIdsAsync(otherAdmin, Path)).ShouldBeEmpty();
        var externalList = await external.Spa.GetAsync(Path, external.Token);
        externalList.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await externalList.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body);
        (await _host.CreateSpaClient().Http.GetAsync(Path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_external_customer_cannot_create_a_case()
    {
        var org = await CreateOrganizationAsync();
        var setup = await SetUpAsync(org);
        var external = await SignInAsync(org, "external");

        var response = await external.Spa.PostAsync(Path, external.Token, Body(setup.Type, setup.Equipment, "外部客戶的案件"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.CaseFeature));
        (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body);
        (await CaseCountAsync(org)).ShouldBe(0);
    }

    [Fact]
    public async Task A_new_case_is_pending_with_one_created_event_and_names()
    {
        var org = await CreateOrganizationAsync();
        var setup = await SetUpAsync(org);
        var caller = await SignInAsync(org, "internal");
        var due = _host.Clock.GetUtcNow().AddHours(48);

        var response = await caller.Spa.PostAsync(Path, caller.Token, new
        {
            typeId = setup.Type,
            groupId = setup.Purchasing,
            dueAt = due,
            title = "  需要更換壓縮機  ",
            description = "冷藏庫的壓縮機異音，請評估是否更換。",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        var item = body.GetProperty("case");
        response.Headers.Location!.ToString().ShouldBe($"{Path}/{item.GetProperty("id").GetGuid()}");
        (item.GetProperty("title").GetString(), item.GetProperty("status").GetString(), item.GetProperty("origin").GetString(),
                item.GetProperty("eventCount").GetInt32())
            .ShouldBe(("需要更換壓縮機", "pending", "manual", 1));
        (item.GetProperty("type").GetProperty("name").GetString(), item.GetProperty("group").GetProperty("name").GetString())
            .ShouldBe(("設備故障報修", "採購組"), "the group the type filled in may be changed");
        item.GetProperty("createdBy").GetProperty("displayName").GetString().ShouldBe("案件商行同仁");
        item.GetProperty("owner").ValueKind.ShouldBe(JsonValueKind.Null);
        item.GetProperty("dueAt").GetDateTimeOffset().ShouldBe(due, TimeSpan.FromMilliseconds(1));

        var created = body.GetProperty("events").EnumerateArray().ShouldHaveSingleItem();
        OpenApiContract.AssertKeysMatchSchema(created, "CaseEventView");
        (created.GetProperty("action").GetString(), created.GetProperty("ordinal").GetInt32(), created.GetProperty("status").GetString(),
                created.GetProperty("actor").GetProperty("displayName").GetString(), created.GetProperty("toGroup").GetProperty("name").GetString())
            .ShouldBe(("created", 1, "pending", "案件商行同仁", "採購組"));

        var links = body.GetProperty("links");
        foreach (var link in new[] { "record", "thread", "assistantIssue", "previousCase" })
        {
            links.GetProperty(link).ValueKind.ShouldBe(JsonValueKind.Null, link);
        }
    }

    [Fact]
    public async Task Each_refusal_of_creating_has_its_own_422()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync("別家商行");
        var setup = await SetUpAsync(org);
        var otherSetup = await SetUpAsync(other);
        var admin = await SignInAsync(org, "admin");
        var caller = await SignInAsync(org, "internal");
        var inactive = await CreateTypeAsync(admin, "停用的類型", setup.Equipment, 24, isActive: false);
        var archived = await CreateGroupAsync(admin, "舊倉儲組");
        (await admin.Spa.PostAsync($"{GroupsPath}/{archived}:archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var now = _host.Clock.GetUtcNow();

        var cases = new (string Who, object Body, string? Reason, string Field, string Message)[]
        {
            ("inactive type", Body(inactive, setup.Equipment, "標題"), "case-type-inactive", "typeId", "這個案件類型已停用或不存在，請選擇其他類型。"),
            ("unknown type", Body(Guid.CreateVersion7(), setup.Equipment, "標題"), "case-type-inactive", "typeId", "這個案件類型已停用或不存在，請選擇其他類型。"),
            ("another organization's type", Body(otherSetup.Type, setup.Equipment, "標題"), "case-type-inactive", "typeId", "這個案件類型已停用或不存在，請選擇其他類型。"),
            ("archived group", Body(setup.Type, archived, "標題"), "case-group-archived", "groupId", "這個承辦組已封存，請選擇其他承辦組。"),
            ("another organization's group", Body(setup.Type, otherSetup.Equipment, "標題"), "case-group-not-found", "groupId", "找不到這個承辦組，請重新選擇。"),
            ("due in the past", Body(setup.Type, setup.Equipment, "標題", now.AddMinutes(-1)), "due-in-past", "dueAt", "時限不能早於現在。"),
            ("blank title", Body(setup.Type, setup.Equipment, "   "), null, "title", "請輸入案件標題。"),
            ("title too long", Body(setup.Type, setup.Equipment, new string('案', 121)), null, "title", "案件標題請在 120 個字以內。"),
        };

        foreach (var (who, body, reason, field, message) in cases)
        {
            var response = await caller.Spa.PostAsync(Path, caller.Token, body);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, who);
            var json = await BodyJsonAsync(response);
            if (reason is null)
            {
                json.TryGetProperty("reason", out _).ShouldBeFalse(who);
            }
            else
            {
                json.GetProperty("reason").GetString().ShouldBe(reason, who);
            }

            json.GetProperty("errors").GetProperty(field)[0].GetString().ShouldBe(message, who);
        }

        (await CaseCountAsync(org)).ShouldBe(0);
    }

    [Fact]
    public async Task A_linked_record_must_be_readable_now_and_shows_its_state_and_whether_you_may_read_it()
    {
        var org = await CreateOrganizationAsync();
        var setup = await SetUpAsync(org);
        var admin = await SignInAsync(org, "admin");
        var member = await SignInAsync(org, "member");
        var internalCaller = await SignInAsync(org, "internal");
        var external = await SignInAsync(org, "external");
        var databaseId = await CreateDatabaseAsync(admin);
        var otherDatabaseId = await CreateDatabaseAsync(admin, "另一個資料庫");
        var record = await SubmitAsync(external, databaseId);

        // Not a designated data manager: the record cannot be linked.
        var notReadable = await internalCaller.Spa.PostAsync(Path, internalCaller.Token,
            Body(setup.Type, setup.Equipment, "看不到的紀錄", databaseId: databaseId, submissionId: record));
        await ShouldBeLinkRefusalAsync(notReadable, "submissionId", "這筆紀錄目前無法連結：你沒有讀取它的權限，或它已撤回、不存在。");
        var mismatched = await admin.Spa.PostAsync(Path, admin.Token,
            Body(setup.Type, setup.Equipment, "對不上的紀錄", databaseId: otherDatabaseId, submissionId: record));
        await ShouldBeLinkRefusalAsync(mismatched, "submissionId", "這筆紀錄目前無法連結：你沒有讀取它的權限，或它已撤回、不存在。");

        var caseId = await CreateCaseAsync(admin, setup.Type, setup.Equipment, "客戶來電詢問退貨", databaseId: databaseId, submissionId: record);
        (await RecordLinkAsync(admin, caseId)).ShouldBe((databaseId, record, "available", true));
        (await RecordLinkAsync(member, caseId)).ShouldBe((databaseId, record, "available", false), "seeing the case never widens the record's access");

        var withdrawal = await external.Spa.PostAsync($"/api/v1/submissions/{record}/withdrawal", external.Token, new { });
        withdrawal.StatusCode.ShouldBe(HttpStatusCode.OK, await withdrawal.Content.ReadAsStringAsync(CancellationToken));
        (await RecordLinkAsync(admin, caseId)).ShouldBe((databaseId, record, "withdrawn", false));
        var withdrawn = await admin.Spa.PostAsync(Path, admin.Token,
            Body(setup.Type, setup.Equipment, "撤回的紀錄", databaseId: databaseId, submissionId: record));
        await ShouldBeLinkRefusalAsync(withdrawn, "submissionId", "這筆紀錄目前無法連結：你沒有讀取它的權限，或它已撤回、不存在。");
    }

    [Fact]
    public async Task A_linked_thread_must_be_your_own_and_the_case_never_carries_any_conversation_text()
    {
        var org = await CreateOrganizationAsync();
        var setup = await SetUpAsync(org);
        var internalCaller = await SignInAsync(org, "internal");
        var member = await SignInAsync(org, "member");
        var assistantId = await CreateAssistantAsync(org);
        var threadId = await SeedThreadAsync(org, assistantId, org.Internal.Id);

        var someoneElses = await member.Spa.PostAsync(Path, member.Token,
            Body(setup.Type, setup.Equipment, "別人的對話", assistantId: assistantId, threadId: threadId));
        await ShouldBeLinkRefusalAsync(someoneElses, "threadId", "只能連結你自己的對話，而且對話必須還在。");

        var caseId = await CreateCaseAsync(internalCaller, setup.Type, setup.Equipment, "冷藏庫溫度異常", assistantId: assistantId, threadId: threadId);
        var ownView = await DetailRawAsync(internalCaller, caseId);
        var memberView = await DetailRawAsync(member, caseId);
        foreach (var (who, raw) in new[] { ("creator", ownView), ("group member", memberView) })
        {
            foreach (var conversationText in new[] { ThreadTitle, ThreadQuestion, ThreadAnswer })
            {
                raw.ShouldNotContain(conversationText, Shouldly.Case.Sensitive, who);
            }
        }

        ThreadLink(ownView).ShouldBe((assistantId, threadId, true));
        ThreadLink(memberView).ShouldBe((assistantId, threadId, false), "someone else's conversation cannot be opened");

        // Retention (or the member) deletes the thread: the link stays but cannot be opened.
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            await dbContext.ChatMessages.Where(message => message.ThreadId == threadId).ExecuteDeleteAsync(CancellationToken);
            await dbContext.ChatThreads.Where(thread => thread.Id == threadId).ExecuteDeleteAsync(CancellationToken);
        }

        ThreadLink(await DetailRawAsync(internalCaller, caseId)).ShouldBe((assistantId, threadId, false));
        var gone = await internalCaller.Spa.PostAsync(Path, internalCaller.Token,
            Body(setup.Type, setup.Equipment, "已刪除的對話", assistantId: assistantId, threadId: threadId));
        await ShouldBeLinkRefusalAsync(gone, "threadId", "只能連結你自己的對話，而且對話必須還在。");
    }

    [Fact]
    public async Task A_previous_case_must_be_visible_and_closed()
    {
        var org = await CreateOrganizationAsync();
        var setup = await SetUpAsync(org);
        var internalCaller = await SignInAsync(org, "internal");
        var outsider = await SignInAsync(org, "other");
        var open = await CreateCaseAsync(internalCaller, setup.Type, setup.Equipment, "還在處理的案件");
        var closed = await CreateCaseAsync(internalCaller, setup.Type, setup.Equipment, "已完成的案件");
        var member = await SignInAsync(org, "member");
        await ActAsync(org, member, closed, "accept");
        await ActAsync(org, member, closed, "complete", new { resolution = "已處理" });

        await ShouldBeLinkRefusalAsync(
            await internalCaller.Spa.PostAsync(Path, internalCaller.Token, Body(setup.Type, setup.Equipment, "另開新案", previousCaseId: open)),
            "previousCaseId", "只能連結你看得到、而且已結案的案件。");
        await ShouldBeLinkRefusalAsync(
            await outsider.Spa.PostAsync(Path, outsider.Token, Body(setup.Type, setup.Equipment, "另開新案", previousCaseId: closed)),
            "previousCaseId", "只能連結你看得到、而且已結案的案件。");

        var next = await CreateCaseAsync(internalCaller, setup.Type, setup.Equipment, "另開新案", previousCaseId: closed);
        var links = JsonDocument.Parse(await DetailRawAsync(internalCaller, next)).RootElement.GetProperty("links");
        (links.GetProperty("previousCase").GetProperty("caseId").GetGuid(), links.GetProperty("previousCase").GetProperty("canOpen").GetBoolean())
            .ShouldBe((closed, true));
    }

    [Fact]
    public async Task The_list_shows_open_cases_by_default_and_each_filter_narrows_it()
    {
        var org = await CreateOrganizationAsync();
        var setup = await SetUpAsync(org);
        var admin = await SignInAsync(org, "admin");
        var internalCaller = await SignInAsync(org, "internal");
        var member = await SignInAsync(org, "member");
        var purchaser = await SignInAsync(org, "other");
        var otherType = await CreateTypeAsync(admin, "採購申請", setup.Purchasing, 120);

        var inEquipment = await CreateCaseAsync(internalCaller, setup.Type, setup.Equipment, "設備組待受理");
        var completed = await CreateCaseAsync(internalCaller, otherType, setup.Purchasing, "已完成的採購");
        var byAdmin = await CreateCaseAsync(admin, setup.Type, setup.Purchasing, "管理者建立的");
        var ownedByMember = await CreateCaseAsync(internalCaller, setup.Type, setup.Equipment, "成員受理中");
        var byMember = await CreateCaseAsync(member, setup.Type, setup.Purchasing, "成員自己建立的");
        await ActAsync(org, purchaser, completed, "accept");
        await ActAsync(org, purchaser, completed, "complete", new { resolution = "已採購" });
        await ActAsync(org, member, ownedByMember, "accept");

        (await ListedIdsAsync(admin, Path)).ShouldBe([byMember, ownedByMember, byAdmin, inEquipment], "open cases only, newest first");
        (await ListedIdsAsync(admin, $"{Path}?status=open")).ShouldBe([byMember, ownedByMember, byAdmin, inEquipment]);
        (await ListedIdsAsync(admin, $"{Path}?status=closed")).ShouldBe([completed]);
        (await ListedIdsAsync(admin, $"{Path}?status=completed")).ShouldBe([completed]);
        (await ListedIdsAsync(admin, $"{Path}?status=cancelled")).ShouldBeEmpty();
        (await ListedIdsAsync(admin, $"{Path}?status=in-progress")).ShouldBe([ownedByMember]);
        (await ListedIdsAsync(admin, $"{Path}?status=pending")).ShouldBe([byMember, byAdmin, inEquipment]);
        (await ListedIdsAsync(admin, $"{Path}?status=all")).ShouldBe([byMember, ownedByMember, byAdmin, completed, inEquipment]);
        (await ListedIdsAsync(admin, $"{Path}?typeId={otherType}")).ShouldBeEmpty();
        (await ListedIdsAsync(admin, $"{Path}?typeId={otherType}&status=all")).ShouldBe([completed]);
        (await ListedIdsAsync(admin, $"{Path}?groupId={setup.Purchasing}")).ShouldBe([byMember, byAdmin]);
        (await ListedIdsAsync(admin, $"{Path}?groupId={setup.Equipment}&typeId={setup.Type}")).ShouldBe([ownedByMember, inEquipment]);

        // The member is in 設備組: sees its cases and their own, never the admin's case in 採購組.
        (await ListedIdsAsync(member, Path)).ShouldBe([byMember, ownedByMember, inEquipment]);
        (await ListedIdsAsync(member, $"{Path}?scope=all")).ShouldBe([byMember, ownedByMember, inEquipment]);
        (await ListedIdsAsync(member, $"{Path}?scope=created")).ShouldBe([byMember]);
        (await ListedIdsAsync(member, $"{Path}?scope=owned")).ShouldBe([ownedByMember]);
        (await ListedIdsAsync(member, $"{Path}?scope=my-groups")).ShouldBe([ownedByMember, inEquipment]);
        (await ListedIdsAsync(internalCaller, $"{Path}?scope=created&status=all")).ShouldBe([ownedByMember, completed, inEquipment]);

        var row = (await BodyJsonAsync(await member.Spa.GetAsync($"{Path}?scope=owned", member.Token))).EnumerateArray().ShouldHaveSingleItem();
        OpenApiContract.AssertKeysMatchSchema(row, "CaseSummaryView");
        (row.GetProperty("status").GetString(), row.GetProperty("owner").GetProperty("displayName").GetString())
            .ShouldBe(("in-progress", "案件商行設備組成員"));

        foreach (var query in new[] { "scope=mine", "status=open,closed", "status=resolved" })
        {
            var response = await admin.Spa.GetAsync($"{Path}?{query}", admin.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, query);
            (await BodyJsonAsync(response)).GetProperty("errors").EnumerateObject().ShouldHaveSingleItem().Name
                .ShouldBe(query.Split('=')[0], query);
        }
    }

    [Fact]
    public async Task Someone_who_accepted_a_case_keeps_seeing_it_after_it_moves_to_another_group()
    {
        var org = await CreateOrganizationAsync();
        var setup = await SetUpAsync(org);
        var internalCaller = await SignInAsync(org, "internal");
        var member = await SignInAsync(org, "member");
        var colleague = await SignInAsync(org, "colleague");
        var outsider = await SignInAsync(org, "other");
        var caseId = await CreateCaseAsync(internalCaller, setup.Type, setup.Equipment, "冷藏庫溫度異常");

        await ActAsync(org, member, caseId, "accept");
        await ActAsync(org, member, caseId, "transfer", new { groupId = setup.Purchasing });

        (await DetailStatusAsync(member, caseId)).ShouldBe(HttpStatusCode.OK, "a former case owner");
        (await DetailStatusAsync(outsider, caseId)).ShouldBe(HttpStatusCode.OK, "a member of the new group");
        (await DetailStatusAsync(colleague, caseId)).ShouldBe(HttpStatusCode.Forbidden, "the old group's member who never accepted it");
        (await ListedIdsAsync(colleague, $"{Path}?status=all")).ShouldBeEmpty();
        (await ListedIdsAsync(member, $"{Path}?status=all")).ShouldBe([caseId]);
    }

    [Fact]
    public async Task A_group_with_open_cases_cannot_be_archived()
    {
        var org = await CreateOrganizationAsync();
        var setup = await SetUpAsync(org);
        var admin = await SignInAsync(org, "admin");
        var temporary = await CreateGroupAsync(admin, "臨時組");
        var caseId = await CreateCaseAsync(admin, setup.Type, temporary, "放在臨時組的案件");

        var refused = await admin.Spa.PostAsync($"{GroupsPath}/{temporary}:archive", admin.Token, new { });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await BodyJsonAsync(refused);
        body.GetProperty("reason").GetString().ShouldBe("case-group-in-use");
        body.GetProperty("message").GetString().ShouldBe("「臨時組」還有 1 件未結案的案件。請先將它們結案或轉給其他承辦組，再封存。");
        body.GetProperty("errors").EnumerateObject().Select(field => field.Name).ShouldBe(["cases"]);

        // Both reasons at once: each under its own field.
        await CreateTypeAsync(admin, "臨時類型", temporary, 24);
        var both = await BodyJsonAsync(await admin.Spa.PostAsync($"{GroupsPath}/{temporary}:archive", admin.Token, new { }));
        both.GetProperty("errors").EnumerateObject().Select(field => field.Name).ShouldBe(["caseTypes", "cases"]);

        // Closed cases do not count.
        var typeId = (await BodyJsonAsync(await admin.Spa.GetAsync(TypesPath, admin.Token))).GetProperty("types").EnumerateArray()
            .Single(type => type.GetProperty("name").GetString() == "臨時類型").GetProperty("id").GetGuid();
        (await admin.Spa.PutAsync($"{TypesPath}/{typeId}", admin.Token, new
        {
            name = "臨時類型", description = "", defaultGroupId = temporary, defaultDueHours = 24, isActive = false,
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ActAsync(org, admin, caseId, "cancel");
        (await admin.Spa.PostAsync($"{GroupsPath}/{temporary}:archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private const string ThreadTitle = "冷藏庫溫度的私人對話標題";
    private const string ThreadQuestion = "冷藏庫溫度一直降不下來，我的電話是 0912-000-111";
    private const string ThreadAnswer = "建議先檢查門封條是否老化。";

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Member, Account Colleague, Account Other);

    private sealed record Setup(Guid Equipment, Guid Purchasing, Guid Type);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private object Body(
        Guid typeId,
        Guid groupId,
        string title,
        DateTimeOffset? dueAt = null,
        Guid? databaseId = null,
        Guid? submissionId = null,
        Guid? assistantId = null,
        Guid? threadId = null,
        Guid? previousCaseId = null) =>
        new
        {
            typeId,
            groupId,
            dueAt = dueAt ?? _host.Clock.GetUtcNow().AddHours(72),
            title,
            description = "說明",
            databaseId,
            submissionId,
            assistantId,
            threadId,
            previousCaseId,
        };

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "案件商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources, AccountPermission.ReadConsentedSubmissions);
        var internalAccount = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}同仁",
            AccountPermission.ManageAssistants, AccountPermission.UseSharedAssistants, AccountPermission.ReadConsentedSubmissions);
        var member = await _host.CreateAccountAsync(
            organization, "member", Password, AccountRole.InternalEmployee, $"{name}設備組成員", AccountPermission.UseSharedAssistants);
        var colleague = await _host.CreateAccountAsync(
            organization, "colleague", Password, AccountRole.InternalEmployee, $"{name}設備組同事", AccountPermission.UseSharedAssistants);
        var other = await _host.CreateAccountAsync(
            organization, "other", Password, AccountRole.InternalEmployee, $"{name}採購組成員", AccountPermission.UseSharedAssistants);
        await _host.CreateAccountAsync(
            organization, "external", Password, AccountRole.ExternalCustomer, $"{name}客戶",
            AccountPermission.SubmitAuthorizedForms, AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, admin, internalAccount, member, colleague, other);
    }

    /// <summary>設備組 (member, colleague), 採購組 (other), and the type 設備故障報修 defaulting to 設備組, 72 hours.</summary>
    private async Task<Setup> SetUpAsync(TestOrganization org)
    {
        var admin = await SignInAsync(org, "admin");
        var equipment = await CreateGroupAsync(admin, "設備組");
        var purchasing = await CreateGroupAsync(admin, "採購組");
        (await admin.Spa.PutAsync($"{GroupsPath}/{equipment}/members", admin.Token, new { accountIds = new[] { org.Member.Id, org.Colleague.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.Spa.PutAsync($"{GroupsPath}/{purchasing}/members", admin.Token, new { accountIds = new[] { org.Other.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return new Setup(equipment, purchasing, await CreateTypeAsync(admin, "設備故障報修", equipment, 72));
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

    private static async Task<Guid> CreateTypeAsync(SignedIn admin, string name, Guid groupId, int hours, bool isActive = true)
    {
        var response = await admin.Spa.PostAsync(TypesPath, admin.Token, new
        {
            name, description = "", defaultGroupId = groupId, defaultDueHours = hours, isActive,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCaseAsync(
        SignedIn caller,
        Guid typeId,
        Guid groupId,
        string title,
        Guid? databaseId = null,
        Guid? submissionId = null,
        Guid? assistantId = null,
        Guid? threadId = null,
        Guid? previousCaseId = null)
    {
        var response = await caller.Spa.PostAsync(Path, caller.Token, Body(
            typeId, groupId, title, databaseId: databaseId, submissionId: submissionId,
            assistantId: assistantId, threadId: threadId, previousCaseId: previousCaseId));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("case").GetProperty("id").GetGuid();
    }

    private static async Task ShouldBeLinkRefusalAsync(HttpResponseMessage response, string field, string message)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, field);
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe("link-not-available");
        body.GetProperty("errors").GetProperty(field)[0].GetString().ShouldBe(message);
    }

    private static async Task<Guid> CreateDatabaseAsync(SignedIn owner, string name = "客戶資料庫")
    {
        var response = await owner.Spa.PostAsync("/api/v1/databases", owner.Token, new { templateId = "template-customer-profile", name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> SubmitAsync(SignedIn caller, Guid databaseId)
    {
        var response = await caller.Spa.PostAsync($"/api/v1/databases/{databaseId}/submissions", caller.Token, new
        {
            submissionId = Guid.NewGuid(),
            formVersionNumber = 1,
            consent = true,
            answers = new Dictionary<string, object>
            {
                ["field-customer-name"] = "王小明",
                ["field-phone"] = "0912-345-678",
                ["field-customer-type"] = "企業",
            },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateAssistantAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = Assistant.Create(
            org.Organization.Id, org.Internal.Id, "設備助理", "測試用途", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: true, DateTimeOffset.UtcNow);
        dbContext.Assistants.Add(assistant);
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    private async Task<Guid> SeedThreadAsync(TestOrganization org, Guid assistantId, Guid accountId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var assistant = await dbContext.Assistants.SingleAsync(candidate => candidate.Id == assistantId, CancellationToken);
        var at = DateTimeOffset.UtcNow;
        var thread = new ChatThread(assistant, accountId, ThreadTitle, at);
        dbContext.ChatThreads.Add(thread);
        dbContext.ChatMessages.AddRange(
            ChatMessage.Account(thread, ThreadQuestion, at),
            ChatMessage.Assistant(thread, ThreadAnswer, ChatReplyKind.CompanyData, null, [], at));
        await dbContext.SaveChangesAsync(CancellationToken);
        return thread.Id;
    }

    /// <summary>Posts a case action (M7-4) with the case's current <c>eventCount</c>; it must succeed.</summary>
    private async Task ActAsync(TestOrganization org, SignedIn caller, Guid caseId, string action, object? fields = null)
    {
        int eventCount;
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            eventCount = (await dbContext.Cases.AsNoTracking().SingleAsync(item => item.Id == caseId, CancellationToken)).EventCount;
        }

        var body = new Dictionary<string, object?> { ["eventCount"] = eventCount };
        foreach (var property in JsonSerializer.SerializeToElement(fields ?? new { }).EnumerateObject())
        {
            body[property.Name] = property.Value.Clone();
        }

        var response = await caller.Spa.PostAsync($"{Path}/{caseId}:{action}", caller.Token, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync(CancellationToken)}");
    }

    private async Task<int> CaseCountAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.Cases.CountAsync(CancellationToken) + await dbContext.CaseEvents.CountAsync(CancellationToken);
    }

    private static async Task<List<Guid>> ListedIdsAsync(SignedIn caller, string path)
    {
        var response = await caller.Spa.GetAsync(path, caller.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        return [.. (await BodyJsonAsync(response)).EnumerateArray().Select(row => row.GetProperty("id").GetGuid())];
    }

    private static async Task<HttpStatusCode> DetailStatusAsync(SignedIn caller, Guid caseId) =>
        (await caller.Spa.GetAsync($"{Path}/{caseId}", caller.Token)).StatusCode;

    private static async Task<string> DetailRawAsync(SignedIn caller, Guid caseId)
    {
        var response = await caller.Spa.GetAsync($"{Path}/{caseId}", caller.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(CancellationToken);
    }

    private static async Task<(Guid, Guid, string?, bool)> RecordLinkAsync(SignedIn caller, Guid caseId)
    {
        var record = JsonDocument.Parse(await DetailRawAsync(caller, caseId)).RootElement.GetProperty("links").GetProperty("record");
        OpenApiContract.AssertKeysMatchSchema(record, "CaseRecordLinkView");
        return (record.GetProperty("databaseId").GetGuid(), record.GetProperty("submissionId").GetGuid(),
            record.GetProperty("state").GetString(), record.GetProperty("canRead").GetBoolean());
    }

    private static (Guid, Guid, bool) ThreadLink(string raw)
    {
        var thread = JsonDocument.Parse(raw).RootElement.GetProperty("links").GetProperty("thread");
        OpenApiContract.AssertKeysMatchSchema(thread, "CaseThreadLinkView");
        return (thread.GetProperty("assistantId").GetGuid(), thread.GetProperty("threadId").GetGuid(), thread.GetProperty("canOpen").GetBoolean());
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
