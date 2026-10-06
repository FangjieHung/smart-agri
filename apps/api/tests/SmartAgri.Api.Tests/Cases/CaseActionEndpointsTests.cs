using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
/// The action table over HTTP against real PostgreSQL (M7 plan §3 D, §5 Slice M7-4; issue #249): each
/// row's 「可以」 (<c>200</c>, one event) and 「不可以」 (<c>403 case-action</c> for the wrong person,
/// <c>409 case-changed</c> for the wrong status), <c>eventCount</c> and two people accepting at once,
/// the creator's answer resuming a 待補件 case, a due time in the past, 阿明's transfer, a case owner who
/// left the group, closed cases and 「另開新案」, and the timeline. The pure rule is
/// <c>CaseActionRulesTests</c> (Application); the state changes <c>CaseActionTests</c> (Domain).
/// </summary>
/// <remarks>
/// Accounts (<see cref="CreateOrganizationAsync"/>): <c>admin</c> the manager (in no group),
/// <c>internal</c> who creates the cases (in no group), <c>member</c> 阿明 and <c>colleague</c> in
/// 設備組, <c>other</c> in 採購組, <c>external</c> a customer.
/// </remarks>
[Trait("Category", TestCategories.Docker)]
public class CaseActionEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Case-Action-Pass-1!";
    private const string Path = "/api/v1/cases";
    private const string GroupsPath = "/api/v1/case-groups";
    private const string TypesPath = "/api/v1/case-types";

    private readonly AuthHostFixture _host;

    /// <summary>Each case's version just before the last action posted on it (<see cref="ShouldBeUntouchedAsync"/>).</summary>
    private readonly Dictionary<Guid, int> _versionBeforeLastAction = [];

    public CaseActionEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Accept_is_for_the_groups_members_while_pending()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);

        await ShouldBeActionDeniedAsync(org, caseId, await ActAsync(org, who["internal"], caseId, "accept"), "the creator is not in the group");
        await ShouldBeActionDeniedAsync(org, caseId, await ActAsync(org, who["admin"], caseId, "accept"), "the manager is not in the group");

        var accepted = await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "accept"));
        (accepted.GetProperty("case").GetProperty("status").GetString(), OwnerName(accepted), accepted.GetProperty("case").GetProperty("eventCount").GetInt32())
            .ShouldBe(("in-progress", "案件商行阿明", 2));
        Actions(accepted).ShouldBe(["request-info", "complete", "cancel", "transfer", "set-due", "comment"]);

        await ShouldBeChangedAsync(org, caseId, await ActAsync(org, who["colleague"], caseId, "accept"), "already accepted");
    }

    [Fact]
    public async Task Request_info_and_resume_are_the_case_owners()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);
        await ShouldBeChangedAsync(org, caseId, await ActAsync(org, who["member"], caseId, "request-info", new { note = "請補照片" }), "pending");
        await AcceptAsync(org, who["member"], caseId);

        foreach (var name in new[] { "colleague", "internal", "admin" })
        {
            await ShouldBeActionDeniedAsync(org, caseId, await ActAsync(org, who[name], caseId, "request-info", new { note = "請補照片" }), name);
        }

        await ShouldBeRefusedAsync(org, caseId, await ActAsync(org, who["member"], caseId, "request-info", new { note = "  " }),
            "note-required", "note", "請說明需要補充哪些資料。");
        var requested = await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "request-info", new { note = "請補上溫度紀錄的照片" }));
        requested.GetProperty("case").GetProperty("status").GetString().ShouldBe("awaiting-info");
        await ShouldBeChangedAsync(org, caseId, await ActAsync(org, who["member"], caseId, "request-info", new { note = "再補" }), "already awaiting");

        foreach (var name in new[] { "colleague", "internal", "admin" })
        {
            await ShouldBeActionDeniedAsync(org, caseId, await ActAsync(org, who[name], caseId, "resume"), name);
        }

        var resumed = await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "resume", new { note = "對方已用電話補件" }));
        resumed.GetProperty("case").GetProperty("status").GetString().ShouldBe("in-progress");
        LastEvent(resumed).GetProperty("note").GetString().ShouldBe("對方已用電話補件");
        await ShouldBeChangedAsync(org, caseId, await ActAsync(org, who["member"], caseId, "resume"), "already in progress");
    }

    [Fact]
    public async Task The_creators_answer_resumes_an_awaiting_info_case_and_the_owners_note_does_not()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);
        await AcceptAsync(org, who["member"], caseId);
        await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "request-info", new { note = "請補上溫度紀錄的照片" }));

        foreach (var name in new[] { "colleague", "admin" })
        {
            await ShouldBeActionDeniedAsync(org, caseId, await ActAsync(org, who[name], caseId, "comment", new { note = "補充" }), name);
        }

        var ownersNote = await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "comment", new { note = "已打電話提醒" }));
        ownersNote.GetProperty("case").GetProperty("status").GetString().ShouldBe("awaiting-info", "the case owner's note keeps the status");
        LastEvent(ownersNote).GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);

        await ShouldBeRefusedAsync(org, caseId, await ActAsync(org, who["internal"], caseId, "comment", new { note = "" }),
            "note-required", "note", "請輸入補充內容。");
        var answer = await ShouldBeOkAsync(await ActAsync(org, who["internal"], caseId, "comment", new { note = "照片已上傳到共用資料夾" }));
        answer.GetProperty("case").GetProperty("status").GetString().ShouldBe("in-progress", "the creator's answer resumes it");
        var answered = LastEvent(answer);
        (answered.GetProperty("action").GetString(), answered.GetProperty("status").GetString(), answered.GetProperty("note").GetString(),
                answered.GetProperty("actor").GetProperty("displayName").GetString())
            .ShouldBe(("commented", "in-progress", "照片已上傳到共用資料夾", "案件商行同仁"));
        answer.GetProperty("events").GetArrayLength().ShouldBe(5, "one event per action, the resume included in the comment's");

        // In progress, the creator's note is only a note.
        var later = await ShouldBeOkAsync(await ActAsync(org, who["internal"], caseId, "comment", new { note = "型號是 RX-200" }));
        later.GetProperty("case").GetProperty("status").GetString().ShouldBe("in-progress");
    }

    [Fact]
    public async Task Complete_is_the_case_owners_and_needs_a_resolution()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);
        await ShouldBeChangedAsync(org, caseId, await ActAsync(org, who["member"], caseId, "complete", new { resolution = "已修好" }), "pending");
        await AcceptAsync(org, who["member"], caseId);

        foreach (var name in new[] { "colleague", "internal", "admin" })
        {
            await ShouldBeActionDeniedAsync(org, caseId, await ActAsync(org, who[name], caseId, "complete", new { resolution = "已修好" }), name);
        }

        await ShouldBeRefusedAsync(org, caseId, await ActAsync(org, who["member"], caseId, "complete", new { resolution = " " }),
            "resolution-required", "resolution", "請填寫處理結果。");
        await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "request-info", new { note = "請補照片" }));
        var completed = await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "complete", new { resolution = "已更換壓縮機" }));
        var item = completed.GetProperty("case");
        (item.GetProperty("status").GetString(), item.GetProperty("resolution").GetString(), item.GetProperty("completedAt").ValueKind)
            .ShouldBe(("completed", "已更換壓縮機", JsonValueKind.String), "completing works from 待補件 too");
        Actions(completed).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_before_acceptance_is_the_creators_or_the_managers_and_afterwards_the_owners_or_the_managers()
    {
        var (org, setup, who) = await ArrangeAsync();

        var byCreator = await CreateCaseAsync(who["internal"], setup);
        await ShouldBeActionDeniedAsync(org, byCreator, await ActAsync(org, who["member"], byCreator, "cancel", new { reason = "不需要了" }), "a member before acceptance");
        var creatorView = await DetailAsync(who["internal"], byCreator);
        creatorView.GetProperty("cancelReasonRequired").GetBoolean().ShouldBeFalse();
        var cancelled = await ShouldBeOkAsync(await ActAsync(org, who["internal"], byCreator, "cancel"));
        (cancelled.GetProperty("case").GetProperty("status").GetString(), cancelled.GetProperty("case").GetProperty("cancelReason").ValueKind)
            .ShouldBe(("cancelled", JsonValueKind.Null), "the creator's reason is optional before acceptance");

        var byManager = await CreateCaseAsync(who["internal"], setup);
        (await DetailAsync(who["admin"], byManager)).GetProperty("cancelReasonRequired").GetBoolean().ShouldBeTrue();
        await ShouldBeRefusedAsync(org, byManager, await ActAsync(org, who["admin"], byManager, "cancel"),
            "reason-required", "reason", "請填寫取消原因。");
        await ShouldBeOkAsync(await ActAsync(org, who["admin"], byManager, "cancel", new { reason = "重複建立" }));

        var accepted = await CreateCaseAsync(who["internal"], setup);
        await AcceptAsync(org, who["member"], accepted);
        await ShouldBeActionDeniedAsync(org, accepted, await ActAsync(org, who["internal"], accepted, "cancel", new { reason = "不需要了" }), "the creator after acceptance");
        await ShouldBeActionDeniedAsync(org, accepted, await ActAsync(org, who["colleague"], accepted, "cancel", new { reason = "不需要了" }), "a member");
        await ShouldBeRefusedAsync(org, accepted, await ActAsync(org, who["member"], accepted, "cancel"),
            "reason-required", "reason", "請填寫取消原因。");
        var ownerCancelled = await ShouldBeOkAsync(await ActAsync(org, who["member"], accepted, "cancel", new { reason = "客戶撤回需求" }));
        ownerCancelled.GetProperty("case").GetProperty("cancelReason").GetString().ShouldBe("客戶撤回需求");

        var managerAfter = await CreateCaseAsync(who["internal"], setup);
        await AcceptAsync(org, who["member"], managerAfter);
        await ShouldBeOkAsync(await ActAsync(org, who["admin"], managerAfter, "cancel", new { reason = "改由外包處理" }));
    }

    [Fact]
    public async Task Transfer_is_the_owners_or_the_managers_and_only_the_manager_may_move_a_pending_case()
    {
        var (org, setup, who) = await ArrangeAsync();
        var admin = who["admin"];
        var archived = await CreateGroupAsync(admin, "舊倉儲組");
        (await admin.Spa.PostAsync($"{GroupsPath}/{archived}:archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var caseId = await CreateCaseAsync(who["internal"], setup);

        await ShouldBeActionDeniedAsync(org, caseId,
            await ActAsync(org, who["member"], caseId, "transfer", new { groupId = setup.Purchasing }), "a member while pending");
        await ShouldBeActionDeniedAsync(org, caseId,
            await ActAsync(org, who["internal"], caseId, "transfer", new { groupId = setup.Purchasing }), "the creator");
        await AcceptAsync(org, who["member"], caseId);
        await ShouldBeActionDeniedAsync(org, caseId,
            await ActAsync(org, who["colleague"], caseId, "transfer", new { groupId = setup.Purchasing }), "a member who is not the owner");

        foreach (var (target, reason, message) in new[]
                 {
                     (setup.Equipment, "case-group-unchanged", "案件已經在這個承辦組，請選擇其他承辦組。"),
                     (archived, "case-group-archived", "這個承辦組已封存，請選擇其他承辦組。"),
                     (Guid.CreateVersion7(), "case-group-not-found", "找不到這個承辦組，請重新選擇。"),
                 })
        {
            await ShouldBeRefusedAsync(org, caseId, await ActAsync(org, who["member"], caseId, "transfer", new { groupId = target }), reason, "groupId", message);
        }

        var transferred = await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "transfer", new { groupId = setup.Purchasing, note = "需要採購零件" }));
        var item = transferred.GetProperty("case");
        (item.GetProperty("status").GetString(), item.GetProperty("group").GetProperty("name").GetString(), item.GetProperty("owner").ValueKind)
            .ShouldBe(("pending", "採購組", JsonValueKind.Null));
        var moved = LastEvent(transferred);
        (moved.GetProperty("action").GetString(), moved.GetProperty("fromGroup").GetProperty("name").GetString(),
                moved.GetProperty("toGroup").GetProperty("name").GetString(), moved.GetProperty("note").GetString())
            .ShouldBe(("transferred", "設備組", "採購組", "需要採購零件"));

        // Pending again: only the manager may move it (the former owner still sees it, but may not).
        await ShouldBeActionDeniedAsync(org, caseId,
            await ActAsync(org, who["member"], caseId, "transfer", new { groupId = setup.Equipment }), "the former owner while pending");
        var back = await ShouldBeOkAsync(await ActAsync(org, admin, caseId, "transfer", new { groupId = setup.Equipment }));
        back.GetProperty("case").GetProperty("group").GetProperty("name").GetString().ShouldBe("設備組");
    }

    [Fact]
    public async Task Set_due_is_the_case_owners_and_a_time_in_the_past_changes_nothing()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);
        await ShouldBeActionDeniedAsync(org, caseId,
            await ActAsync(org, who["member"], caseId, "set-due", new { dueAt = _host.Clock.GetUtcNow().AddDays(5) }), "pending: no owner yet");
        await AcceptAsync(org, who["member"], caseId);

        foreach (var name in new[] { "colleague", "internal", "admin" })
        {
            await ShouldBeActionDeniedAsync(org, caseId,
                await ActAsync(org, who[name], caseId, "set-due", new { dueAt = _host.Clock.GetUtcNow().AddDays(5) }), name);
        }

        var before = await DetailAsync(who["member"], caseId);
        await ShouldBeRefusedAsync(org, caseId,
            await ActAsync(org, who["member"], caseId, "set-due", new { dueAt = _host.Clock.GetUtcNow().AddMinutes(-1) }),
            "due-in-past", "dueAt", "時限不能早於現在。");
        var after = await DetailAsync(who["member"], caseId);
        after.GetProperty("case").GetProperty("dueAt").GetDateTimeOffset().ShouldBe(before.GetProperty("case").GetProperty("dueAt").GetDateTimeOffset());
        after.GetProperty("events").GetArrayLength().ShouldBe(before.GetProperty("events").GetArrayLength(), "no event for a refused due time");

        var due = _host.Clock.GetUtcNow().AddDays(5);
        var changed = await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "set-due", new { dueAt = due, note = "等廠商報價" }));
        changed.GetProperty("case").GetProperty("dueAt").GetDateTimeOffset().ShouldBe(due, TimeSpan.FromMilliseconds(1));
        var recorded = LastEvent(changed);
        (recorded.GetProperty("action").GetString(), recorded.GetProperty("note").GetString()).ShouldBe(("due-changed", "等廠商報價"));
        recorded.GetProperty("dueAt").GetDateTimeOffset().ShouldBe(due, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task A_stale_event_count_is_409_and_a_missing_one_422_and_neither_changes_anything()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);
        await ShouldBeOkAsync(await ActAsync(org, who["internal"], caseId, "comment", new { note = "補充一張照片" }));

        var stale = await ActAsync(org, who["member"], caseId, "accept", eventCount: 1);
        await ShouldBeChangedAsync(org, caseId, stale, "the screen showed version 1, the case is at 2");
        var expected = await ApiErrorsTests.ExecuteAsync(
            ApiErrors.WithReason(StatusCodes.Status409Conflict, "case-changed", "這件案件剛被其他人更新，請重新整理後再試。"));
        (await stale.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body);

        var missing = await who["member"].Spa.PostAsync($"{Path}/{caseId}:accept", who["member"].Token, new { });
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(missing)).GetProperty("errors").GetProperty("eventCount")[0].GetString()
            .ShouldBe("缺少畫面上的案件版本（eventCount），請重新整理後再試。");
        (await EventCountAsync(org, caseId)).ShouldBe(2);
    }

    [Fact]
    public async Task Two_members_accepting_at_once_only_one_succeeds()
    {
        var (org, setup, who) = await ArrangeAsync();
        for (var round = 0; round < 6; round++)
        {
            var caseId = await CreateCaseAsync(who["internal"], setup, $"同時受理第 {round + 1} 件");
            using var start = new SemaphoreSlim(0, 2);
            var racers = new[] { who["member"], who["colleague"] }.Select(async caller =>
            {
                await start.WaitAsync(CancellationToken);
                return await caller.Spa.PostAsync($"{Path}/{caseId}:accept", caller.Token, new { eventCount = 1 });
            }).ToList();
            start.Release(2);
            var responses = await Task.WhenAll(racers);

            responses.Select(response => (int)response.StatusCode).Order().ToArray()
                .ShouldBe([StatusCodes.Status200OK, StatusCodes.Status409Conflict], $"round {round}");
            (await BodyJsonAsync(responses.Single(response => response.StatusCode == HttpStatusCode.Conflict)))
                .GetProperty("reason").GetString().ShouldBe("case-changed");
            await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
            (await dbContext.CaseEvents.CountAsync(caseEvent => caseEvent.CaseId == caseId && caseEvent.Action == CaseEventAction.Accepted, CancellationToken))
                .ShouldBe(1, $"round {round}");
            (await dbContext.Cases.SingleAsync(item => item.Id == caseId, CancellationToken)).EventCount.ShouldBe(2);
        }
    }

    [Fact]
    public async Task The_event_count_is_the_concurrency_token_even_when_both_saw_the_same_version()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);

        // Both read version 1 before either saves (what the endpoint's own eventCount check cannot catch).
        await using var first = _host.Postgres.CreateDbContext(org.Organization.Id);
        await using var second = _host.Postgres.CreateDbContext(org.Organization.Id);
        var mine = await first.Cases.SingleAsync(item => item.Id == caseId, CancellationToken);
        var theirs = await second.Cases.SingleAsync(item => item.Id == caseId, CancellationToken);
        first.CaseEvents.Add(mine.Accept(org.Member.Id, _host.Clock.GetUtcNow()));
        second.CaseEvents.Add(theirs.Accept(org.Colleague.Id, _host.Clock.GetUtcNow()));

        await first.SaveChangesAsync(CancellationToken);
        await Should.ThrowAsync<DbUpdateException>(() => second.SaveChangesAsync(CancellationToken));

        var detail = await DetailAsync(who["admin"], caseId);
        OwnerName(detail).ShouldBe("案件商行阿明");
        detail.GetProperty("events").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Aming_accepts_and_transfers_and_keeps_seeing_the_case_while_his_colleague_does_not()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup, "冷藏庫溫度異常");
        var aming = who["member"];
        var colleague = who["colleague"];
        var purchaser = who["other"];
        (await StatusOfDetailAsync(purchaser, caseId)).ShouldBe(HttpStatusCode.Forbidden, "採購組 before the transfer");

        await AcceptAsync(org, aming, caseId);
        await ShouldBeOkAsync(await ActAsync(org, aming, caseId, "transfer", new { groupId = setup.Purchasing, note = "需要採購壓縮機" }));

        (await StatusOfDetailAsync(aming, caseId)).ShouldBe(HttpStatusCode.OK, "阿明 accepted it once");
        (await ListedIdsAsync(aming, $"{Path}?status=all")).ShouldBe([caseId]);
        Actions(await DetailAsync(aming, caseId)).ShouldBeEmpty("a former owner sees it but may do nothing on it now");

        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.CaseFeature));
        var hidden = await colleague.Spa.GetAsync($"{Path}/{caseId}", colleague.Token);
        hidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "設備組's member who never accepted it");
        (await hidden.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body);
        var refused = await ActAsync(org, colleague, caseId, "accept");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, "an action on a case you cannot see is the same 403 case");
        (await ListedIdsAsync(colleague, $"{Path}?status=all")).ShouldBeEmpty();

        Actions(await DetailAsync(purchaser, caseId)).ShouldBe(["accept"]);
        var accepted = await ShouldBeOkAsync(await ActAsync(org, purchaser, caseId, "accept"));
        OwnerName(accepted).ShouldBe("案件商行採購組成員");
        (await StatusOfDetailAsync(aming, caseId)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_case_owner_who_left_the_group_can_still_finish_the_case()
    {
        var (org, setup, who) = await ArrangeAsync();
        var caseId = await CreateCaseAsync(who["internal"], setup);
        await AcceptAsync(org, who["member"], caseId);
        var admin = who["admin"];
        (await admin.Spa.PutAsync($"{GroupsPath}/{setup.Equipment}/members", admin.Token, new { accountIds = new[] { org.Colleague.Id } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        Actions(await DetailAsync(who["member"], caseId)).ShouldContain("complete");
        await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "set-due", new { dueAt = _host.Clock.GetUtcNow().AddDays(2) }));
        var completed = await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "complete", new { resolution = "已修好，阿明調到門市後補完紀錄" }));
        completed.GetProperty("case").GetProperty("status").GetString().ShouldBe("completed");
    }

    [Fact]
    public async Task A_closed_case_refuses_every_action_and_a_new_case_continues_it()
    {
        var (org, setup, who) = await ArrangeAsync();
        var completed = await CreateCaseAsync(who["internal"], setup, "灌溉馬達異音");
        await AcceptAsync(org, who["member"], completed);
        await ShouldBeOkAsync(await ActAsync(org, who["member"], completed, "complete", new { resolution = "已更換軸承" }));
        var cancelled = await CreateCaseAsync(who["internal"], setup, "重複的案件");
        await ShouldBeOkAsync(await ActAsync(org, who["internal"], cancelled, "cancel"));

        var everyone = new[] { "admin", "internal", "member", "colleague" };
        var attempts = new (string Action, object Fields)[]
        {
            ("accept", new { }),
            ("request-info", new { note = "補件" }),
            ("resume", new { }),
            ("complete", new { resolution = "再完成一次" }),
            ("cancel", new { reason = "取消" }),
            ("transfer", new { groupId = setup.Purchasing }),
            ("set-due", new { dueAt = _host.Clock.GetUtcNow().AddDays(1) }),
            ("comment", new { note = "補充" }),
        };
        foreach (var caseId in new[] { completed, cancelled })
        {
            foreach (var name in everyone)
            {
                Actions(await DetailAsync(who[name], caseId)).ShouldBeEmpty(name);
                foreach (var (action, fields) in attempts)
                {
                    await ShouldBeChangedAsync(org, caseId, await ActAsync(org, who[name], caseId, action, fields), $"{name} {action}");
                }
            }
        }

        // 「另開新案」: the screen fills in the old case's type and title, and links it.
        var old = (await DetailAsync(who["internal"], completed)).GetProperty("case");
        var response = await who["internal"].Spa.PostAsync(Path, who["internal"].Token, new
        {
            typeId = old.GetProperty("type").GetProperty("id").GetGuid(),
            groupId = old.GetProperty("group").GetProperty("id").GetGuid(),
            dueAt = _host.Clock.GetUtcNow().AddDays(3),
            title = old.GetProperty("title").GetString(),
            description = "馬達又有異音",
            previousCaseId = completed,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var next = await BodyJsonAsync(response);
        (next.GetProperty("case").GetProperty("title").GetString(), next.GetProperty("case").GetProperty("status").GetString())
            .ShouldBe(("灌溉馬達異音", "pending"));
        var previous = next.GetProperty("links").GetProperty("previousCase");
        (previous.GetProperty("caseId").GetGuid(), previous.GetProperty("canOpen").GetBoolean()).ShouldBe((completed, true));
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.Cases.SingleAsync(item => item.Id == next.GetProperty("case").GetProperty("id").GetGuid(), CancellationToken)).PreviousCaseId
            .ShouldBe(completed);
    }

    [Fact]
    public async Task Every_action_writes_one_event_and_the_timeline_shows_who_and_when()
    {
        var (org, setup, who) = await ArrangeAsync();
        var before = _host.Clock.GetUtcNow().AddSeconds(-1);
        var caseId = await CreateCaseAsync(who["internal"], setup);
        await AcceptAsync(org, who["member"], caseId);
        await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "request-info", new { note = "請補照片" }));
        await ShouldBeOkAsync(await ActAsync(org, who["internal"], caseId, "comment", new { note = "照片已上傳" }));
        await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "set-due", new { dueAt = _host.Clock.GetUtcNow().AddDays(4) }));
        await ShouldBeOkAsync(await ActAsync(org, who["member"], caseId, "transfer", new { groupId = setup.Purchasing }));
        await AcceptAsync(org, who["other"], caseId);
        var done = await ShouldBeOkAsync(await ActAsync(org, who["other"], caseId, "complete", new { resolution = "已採購並更換" }));
        var after = _host.Clock.GetUtcNow().AddSeconds(1);

        var events = done.GetProperty("events").EnumerateArray().ToList();
        events.Select(caseEvent => (caseEvent.GetProperty("ordinal").GetInt32(), caseEvent.GetProperty("action").GetString(),
                caseEvent.GetProperty("actor").GetProperty("displayName").GetString()))
            .ShouldBe([
                (1, "created", "案件商行同仁"),
                (2, "accepted", "案件商行阿明"),
                (3, "info-requested", "案件商行阿明"),
                (4, "commented", "案件商行同仁"),
                (5, "due-changed", "案件商行阿明"),
                (6, "transferred", "案件商行阿明"),
                (7, "accepted", "案件商行採購組成員"),
                (8, "completed", "案件商行採購組成員"),
            ]);
        foreach (var caseEvent in events)
        {
            OpenApiContract.AssertKeysMatchSchema(caseEvent, "CaseEventView");
            caseEvent.GetProperty("at").GetDateTimeOffset().ShouldBeInRange(before, after);
        }

        events[1].GetProperty("owner").GetProperty("displayName").GetString().ShouldBe("案件商行阿明");
        done.GetProperty("case").GetProperty("eventCount").GetInt32().ShouldBe(8);
    }

    [Fact]
    public async Task External_customers_other_organizations_and_unknown_ids_get_the_one_403_case()
    {
        var (org, setup, who) = await ArrangeAsync();
        var other = await CreateOrganizationAsync("別家商行");
        var otherAdmin = await SignInAsync(other, "admin");
        var caseId = await CreateCaseAsync(who["internal"], setup);
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.CaseFeature));

        foreach (var (label, caller, id) in new[]
                 {
                     ("external customer", who["external"], caseId),
                     ("another organization's manager", otherAdmin, caseId),
                     ("an id that does not exist", who["member"], Guid.CreateVersion7()),
                 })
        {
            foreach (var path in new[] { $"{Path}/{id}:accept", $"{Path}/{id}/comments" })
            {
                var response = await caller.Spa.PostAsync(path, caller.Token, new { eventCount = 1, note = "補充" });
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, label);
                (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBe(expected.Body, label);
            }
        }

        (await EventCountAsync(org, caseId)).ShouldBe(1);
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private sealed record TestOrganization(Organization Organization, Account Admin, Account Internal, Account Member, Account Colleague, Account Other);

    private sealed record Setup(Guid Equipment, Guid Purchasing, Guid Type);

    private sealed record SignedIn(SpaClient Spa, string Token);

    private async Task<(TestOrganization Org, Setup Setup, Dictionary<string, SignedIn> Who)> ArrangeAsync()
    {
        var org = await CreateOrganizationAsync();
        var setup = await SetUpAsync(org);
        var who = new Dictionary<string, SignedIn>();
        foreach (var login in new[] { "admin", "internal", "member", "colleague", "other", "external" })
        {
            who[login] = await SignInAsync(org, login);
        }

        return (org, setup, who);
    }

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "案件商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await _host.CreateAccountAsync(organization, "admin", Password, AccountRole.SmbAdmin, $"{name}管理者", AccountPermission.ManageAssistants);
        var internalAccount = await _host.CreateAccountAsync(
            organization, "internal", Password, AccountRole.InternalEmployee, $"{name}同仁", AccountPermission.UseSharedAssistants);
        var member = await _host.CreateAccountAsync(
            organization, "member", Password, AccountRole.InternalEmployee, $"{name}阿明", AccountPermission.UseSharedAssistants);
        var colleague = await _host.CreateAccountAsync(
            organization, "colleague", Password, AccountRole.InternalEmployee, $"{name}設備組同事", AccountPermission.UseSharedAssistants);
        var other = await _host.CreateAccountAsync(
            organization, "other", Password, AccountRole.InternalEmployee, $"{name}採購組成員", AccountPermission.UseSharedAssistants);
        await _host.CreateAccountAsync(
            organization, "external", Password, AccountRole.ExternalCustomer, $"{name}客戶", AccountPermission.UseSharedAssistants);
        return new TestOrganization(organization, admin, internalAccount, member, colleague, other);
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

    private async Task<Guid> CreateCaseAsync(SignedIn caller, Setup setup, string title = "冷藏庫溫度降不下來")
    {
        var response = await caller.Spa.PostAsync(Path, caller.Token, new
        {
            typeId = setup.Type,
            groupId = setup.Equipment,
            dueAt = _host.Clock.GetUtcNow().AddHours(72),
            title,
            description = "說明",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("case").GetProperty("id").GetGuid();
    }

    /// <summary>Posts <paramref name="action"/> with the case's current <c>eventCount</c> (read from the
    /// database, as a fresh screen would show it) unless <paramref name="eventCount"/> is given.</summary>
    private async Task<HttpResponseMessage> ActAsync(
        TestOrganization org, SignedIn caller, Guid caseId, string action, object? fields = null, int? eventCount = null)
    {
        var current = await EventCountAsync(org, caseId);
        _versionBeforeLastAction[caseId] = current;
        var body = new Dictionary<string, object?> { ["eventCount"] = eventCount ?? current };
        if (fields is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(fields).EnumerateObject())
            {
                body[property.Name] = property.Value.Clone();
            }
        }

        var path = action == "comment" ? $"{Path}/{caseId}/comments" : $"{Path}/{caseId}:{action}";
        return await caller.Spa.PostAsync(path, caller.Token, body);
    }

    private async Task AcceptAsync(TestOrganization org, SignedIn caller, Guid caseId) =>
        await ShouldBeOkAsync(await ActAsync(org, caller, caseId, "accept"));

    private async Task<int> EventCountAsync(TestOrganization org, Guid caseId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return (await dbContext.Cases.AsNoTracking().SingleOrDefaultAsync(item => item.Id == caseId, CancellationToken))?.EventCount ?? 1;
    }

    private static async Task<JsonElement> ShouldBeOkAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        var body = JsonDocument.Parse(raw).RootElement.Clone();
        OpenApiContract.AssertKeysMatchSchema(body, "CaseDetailView");
        OpenApiContract.AssertKeysMatchSchema(body.GetProperty("case"), "CaseView");
        return body;
    }

    /// <summary><c>403 case-action</c>, byte for byte, and the case is untouched.</summary>
    private async Task ShouldBeActionDeniedAsync(TestOrganization org, Guid caseId, HttpResponseMessage response, string because)
    {
        var expected = await ApiErrorsTests.ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.CaseAction));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, because);
        var raw = await response.Content.ReadAsByteArrayAsync(CancellationToken);
        raw.ShouldBe(expected.Body, because);
        JsonDocument.Parse(raw).RootElement.GetProperty("reason").GetString().ShouldBe("case-action", because);
        await ShouldBeUntouchedAsync(org, caseId, because);
    }

    /// <summary><c>409 case-changed</c> and the case is untouched.</summary>
    private async Task ShouldBeChangedAsync(TestOrganization org, Guid caseId, HttpResponseMessage response, string because)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, because);
        (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe("case-changed", because);
        await ShouldBeUntouchedAsync(org, caseId, because);
    }

    /// <summary>A <c>422</c> with <paramref name="reason"/> under <paramref name="field"/>, and the case is untouched.</summary>
    private async Task ShouldBeRefusedAsync(TestOrganization org, Guid caseId, HttpResponseMessage response, string reason, string field, string message)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, reason);
        var body = await BodyJsonAsync(response);
        (body.GetProperty("reason").GetString(), body.GetProperty("errors").GetProperty(field)[0].GetString()).ShouldBe((reason, message));
        await ShouldBeUntouchedAsync(org, caseId, reason);
    }

    /// <summary>A refusal wrote no event: the case's version and its number of events are what they were
    /// before the action was posted.</summary>
    private async Task ShouldBeUntouchedAsync(TestOrganization org, Guid caseId, string because)
    {
        var before = _versionBeforeLastAction[caseId];
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.Cases.AsNoTracking().SingleAsync(item => item.Id == caseId, CancellationToken)).EventCount.ShouldBe(before, because);
        (await dbContext.CaseEvents.CountAsync(caseEvent => caseEvent.CaseId == caseId, CancellationToken)).ShouldBe(before, because);
    }

    private static async Task<JsonElement> DetailAsync(SignedIn caller, Guid caseId)
    {
        var response = await caller.Spa.GetAsync($"{Path}/{caseId}", caller.Token);
        var raw = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static async Task<HttpStatusCode> StatusOfDetailAsync(SignedIn caller, Guid caseId) =>
        (await caller.Spa.GetAsync($"{Path}/{caseId}", caller.Token)).StatusCode;

    private static async Task<List<Guid>> ListedIdsAsync(SignedIn caller, string path)
    {
        var response = await caller.Spa.GetAsync(path, caller.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        return [.. (await BodyJsonAsync(response)).EnumerateArray().Select(row => row.GetProperty("id").GetGuid())];
    }

    private static List<string?> Actions(JsonElement detail) =>
        [.. detail.GetProperty("allowedActions").EnumerateArray().Select(action => action.GetString())];

    private static string? OwnerName(JsonElement detail) => detail.GetProperty("case").GetProperty("owner").GetProperty("displayName").GetString();

    private static JsonElement LastEvent(JsonElement detail) => detail.GetProperty("events").EnumerateArray().Last();

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
