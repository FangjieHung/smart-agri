using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// 「另開案件」: <c>POST /api/v1/issues/{issueId}:open-case</c> (M7 plan §3 G, §5 Slice M7-7; issue #252)
/// against real PostgreSQL: who may do it (whoever may change the issue, if internal), that the case,
/// the 「非助理問題」 resolution, the <c>case-opened</c> event and both links are written together or not
/// at all (a trigger makes one write fail), the two <c>409</c>s, and the operations numbers.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class AssistantIssueOpenCaseEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Issue-Open-Case-Pass-1!";
    private const string Question = "冷藏庫的溫度一直降不下來，可以派人來看嗎？";
    private const string Answer = "請參考冷藏庫的保養說明。";
    private const string GroupsPath = "/api/v1/case-groups";
    private const string TypesPath = "/api/v1/case-types";

    private readonly AuthHostFixture _host;

    public AssistantIssueOpenCaseEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static string OpenCasePath(Guid issueId) => $"/api/v1/issues/{issueId}:open-case";

    private static string IssuePath(Guid issueId) => $"/api/v1/issues/{issueId}";

    [Fact]
    public async Task The_owner_and_the_assignee_may_open_a_case_and_everyone_else_gets_the_403_of_a_missing_issue()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync("別家商行");
        var byOwner = await CreateIssueAsync(org, assignee: org.Handler.AccountId);
        var byHandler = await CreateIssueAsync(org, assignee: org.Handler.AccountId);
        var ofExternal = await CreateIssueAsync(org, assignee: org.External.AccountId);

        var reference = await ResponseFingerprint.FromAsync(
            await org.Owner.Spa.PostAsync(OpenCasePath(Guid.NewGuid()), org.Owner.Token, Body(org)));
        reference.Status.ShouldBe(HttpStatusCode.Forbidden);
        JsonDocument.Parse(reference.Body).RootElement.GetProperty("reason").GetString().ShouldBe("assistant-issue");

        // The external customer is the assignee and may see the issue, but cases are internal only.
        (await org.External.Spa.GetAsync(IssuePath(ofExternal), org.External.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var (who, caller, issueId) in new[]
                 {
                     ("a colleague who neither owns the assistant nor is assigned", org.Colleague, byOwner),
                     ("a case group member", org.GroupMember, byOwner),
                     ("another organization's owner", other.Owner, byOwner),
                     ("the external customer who is the assignee", org.External, ofExternal),
                 })
        {
            await AssertIdenticalAsync(await caller.Spa.PostAsync(OpenCasePath(issueId), caller.Token, Body(org)), reference, who);
        }

        (await _host.CreateSpaClient().Http.PostAsync(OpenCasePath(byOwner), null, CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CaseCountAsync(org)).ShouldBe(0);

        foreach (var (caller, issueId) in new[] { (org.Owner, byOwner), (org.Handler, byHandler) })
        {
            var response = await caller.Spa.PostAsync(OpenCasePath(issueId), caller.Token, Body(org));
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
            var caseId = (await BodyJsonAsync(response)).GetProperty("caseId").GetGuid();
            var detail = await BodyJsonAsync(await caller.Spa.GetAsync($"/api/v1/cases/{caseId}", caller.Token));
            detail.GetProperty("case").GetProperty("createdBy").GetProperty("id").GetGuid().ShouldBe(caller.AccountId);
        }

        (await CaseCountAsync(org)).ShouldBe(4, "two cases with one created event each");
    }

    [Fact]
    public async Task Opening_a_case_writes_the_case_the_resolution_the_event_and_both_links()
    {
        var org = await CreateOrganizationAsync();
        var issueId = await CreateIssueAsync(org, assignee: org.Handler.AccountId);
        var due = _host.Clock.GetUtcNow().AddHours(48);

        var response = await org.Owner.Spa.PostAsync(OpenCasePath(issueId), org.Owner.Token, new
        {
            typeId = org.Type,
            groupId = org.Equipment,
            dueAt = due,
            title = "  派人檢查冷藏庫  ",
            description = Question,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "AssistantIssueOpenedCaseView");
        var caseId = body.GetProperty("caseId").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/cases/{caseId}");

        var detail = body.GetProperty("issue");
        OpenApiContract.AssertKeysMatchSchema(detail, "AssistantIssueDetailView");
        var issue = detail.GetProperty("issue");
        OpenApiContract.AssertKeysMatchSchema(issue, "AssistantIssueView");
        (issue.GetProperty("status").GetString(), issue.GetProperty("resolutionKind").GetString(),
                issue.GetProperty("linkedCaseId").GetGuid(), issue.GetProperty("resolutionNote").ValueKind)
            .ShouldBe(("resolved", "not-assistant-issue", caseId, JsonValueKind.Null));
        OpenApiContract.AssertKeysMatchSchema(detail.GetProperty("linkedCase"), "AssistantIssueCaseLinkView");
        (detail.GetProperty("linkedCase").GetProperty("caseId").GetGuid(), detail.GetProperty("linkedCase").GetProperty("canOpen").GetBoolean())
            .ShouldBe((caseId, true));
        var opened = detail.GetProperty("events").EnumerateArray().Last();
        (opened.GetProperty("action").GetString(), opened.GetProperty("status").GetString(), opened.GetProperty("actorAccountId").GetGuid())
            .ShouldBe(("case-opened", "resolved", org.Owner.AccountId));

        var caseDetail = await BodyJsonAsync(await org.Owner.Spa.GetAsync($"/api/v1/cases/{caseId}", org.Owner.Token));
        var item = caseDetail.GetProperty("case");
        (item.GetProperty("origin").GetString(), item.GetProperty("status").GetString(), item.GetProperty("title").GetString(),
                item.GetProperty("description").GetString(), item.GetProperty("group").GetProperty("id").GetGuid())
            .ShouldBe(("assistant-issue", "pending", "派人檢查冷藏庫", Question, org.Equipment));
        item.GetProperty("dueAt").GetDateTimeOffset().ShouldBe(due, TimeSpan.FromMilliseconds(1));
        caseDetail.GetProperty("events").EnumerateArray().ShouldHaveSingleItem().GetProperty("action").GetString().ShouldBe("created");
        caseDetail.GetProperty("allowedActions").EnumerateArray().Select(action => action.GetString())
            .ShouldContain("cancel", "the creator may cancel before acceptance: the action table applies as for any case");
        OpenApiContract.AssertKeysMatchSchema(caseDetail.GetProperty("links").GetProperty("assistantIssue"), "CaseIssueLinkView");
        (await IssueLinkAsync(org.Owner, caseId)).ShouldBe((issueId, true));

        // The group member sees the case (its group) but may not open the issue; the assignee may open
        // the issue but not the case (neither creator nor member).
        (await IssueLinkAsync(org.GroupMember, caseId)).ShouldBe((issueId, false));
        var handlerView = await BodyJsonAsync(await org.Handler.Spa.GetAsync(IssuePath(issueId), org.Handler.Token));
        handlerView.GetProperty("linkedCase").GetProperty("canOpen").GetBoolean().ShouldBeFalse();
        handlerView.GetProperty("issue").GetProperty("linkedCaseId").GetGuid().ShouldBe(caseId);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var row = await dbContext.AssistantIssues.AsNoTracking().SingleAsync(candidate => candidate.Id == issueId, CancellationToken);
        (row.LinkedCaseId, row.ResolutionKind).ShouldBe((caseId, AssistantIssueResolutionKind.NotAssistantIssue));
        (await dbContext.Cases.AsNoTracking().SingleAsync(candidate => candidate.Id == caseId, CancellationToken)).AssistantIssueId
            .ShouldBe(issueId);
    }

    [Fact]
    public async Task A_resolved_issue_is_409_and_a_second_open_case_is_409_with_the_existing_case_even_after_reopening()
    {
        var org = await CreateOrganizationAsync();
        var resolved = await CreateIssueAsync(org);
        await PatchOkAsync(org.Owner, resolved, new { status = "resolved", note = "已修正" });

        await AssertReasonAsync(
            await org.Owner.Spa.PostAsync(OpenCasePath(resolved), org.Owner.Token, Body(org)), HttpStatusCode.Conflict, "issue-changed");
        (await CaseCountAsync(org)).ShouldBe(0);
        var fixedIssue = (await BodyJsonAsync(await org.Owner.Spa.GetAsync(IssuePath(resolved), org.Owner.Token))).GetProperty("issue");
        (fixedIssue.GetProperty("resolutionKind").GetString(), fixedIssue.GetProperty("linkedCaseId").ValueKind)
            .ShouldBe(("fixed", JsonValueKind.Null));

        var issueId = await CreateIssueAsync(org);
        var first = await org.Owner.Spa.PostAsync(OpenCasePath(issueId), org.Owner.Token, Body(org));
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var caseId = (await BodyJsonAsync(first)).GetProperty("caseId").GetGuid();

        var again = await AssertReasonAsync(
            await org.Owner.Spa.PostAsync(OpenCasePath(issueId), org.Owner.Token, new { }), HttpStatusCode.Conflict, "case-already-opened");
        again.GetProperty("caseId").GetGuid().ShouldBe(caseId);

        // Reopening clears the issue's resolution and link (like its note); the case keeps its link,
        // and the issue still cannot open a second case.
        await PatchOkAsync(org.Owner, issueId, new { status = "open" });
        var reopened = (await BodyJsonAsync(await org.Owner.Spa.GetAsync(IssuePath(issueId), org.Owner.Token)));
        (reopened.GetProperty("issue").GetProperty("resolutionKind").ValueKind, reopened.GetProperty("issue").GetProperty("linkedCaseId").ValueKind,
                reopened.GetProperty("linkedCase").ValueKind)
            .ShouldBe((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null));
        var afterReopen = await AssertReasonAsync(
            await org.Owner.Spa.PostAsync(OpenCasePath(issueId), org.Owner.Token, Body(org)), HttpStatusCode.Conflict, "case-already-opened");
        afterReopen.GetProperty("caseId").GetGuid().ShouldBe(caseId);
        (await IssueLinkAsync(org.Owner, caseId)).ShouldBe((issueId, true));
        (await CaseCountAsync(org)).ShouldBe(2, "one case and its created event");
    }

    [Fact]
    public async Task A_refused_case_field_leaves_the_issue_as_it_was()
    {
        var org = await CreateOrganizationAsync();
        var issueId = await CreateIssueAsync(org);
        await PatchOkAsync(org.Owner, issueId, new { status = "in-progress" });
        var admin = org.Admin;
        var inactive = await CreateTypeAsync(admin, "停用的類型", org.Equipment, 24, isActive: false);
        var archived = await CreateGroupAsync(admin, "舊倉儲組");
        (await admin.Spa.PostAsync($"{GroupsPath}/{archived}:archive", admin.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await IssueRowAsync(org, issueId);

        foreach (var (who, body, reason, field) in new (string, object, string?, string)[]
                 {
                     ("inactive type", Body(org, typeId: inactive), "case-type-inactive", "typeId"),
                     ("archived group", Body(org, groupId: archived), "case-group-archived", "groupId"),
                     ("due in the past", Body(org, dueAt: _host.Clock.GetUtcNow().AddMinutes(-1)), "due-in-past", "dueAt"),
                     ("blank title", Body(org, title: "   "), null, "title"),
                 })
        {
            var response = await org.Owner.Spa.PostAsync(OpenCasePath(issueId), org.Owner.Token, body);
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

            json.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(who);
        }

        (await IssueRowAsync(org, issueId)).ShouldBe(before);
        (await CaseCountAsync(org)).ShouldBe(0);
    }

    [Fact]
    public async Task When_one_write_of_the_transaction_fails_neither_the_case_nor_the_resolution_is_kept()
    {
        var org = await CreateOrganizationAsync();
        var issueId = await CreateIssueAsync(org, assignee: org.Handler.AccountId);
        var before = await IssueRowAsync(org, issueId);
        var name = $"fail_case_opened_{issueId:N}";

        // The issue's case-opened event is the write that fails: by then the case, its created event
        // and the issue's resolution have been sent in the same SaveChanges.
        // The trigger's name and the issue id are this test's own values, not input.
        var install = $"""
                CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."IssueId" = '{issueId}' AND NEW."Action" = 'case-opened' THEN
                    RAISE EXCEPTION 'simulated failure of the case-opened event';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER {name} BEFORE INSERT ON "AssistantIssueEvents" FOR EACH ROW EXECUTE FUNCTION {name}();
                """;
        var uninstall = $"""DROP TRIGGER {name} ON "AssistantIssueEvents"; DROP FUNCTION {name}();""";
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            await dbContext.Database.ExecuteSqlRawAsync(install, CancellationToken);
        }

        try
        {
            HttpStatusCode? status = null;
            try
            {
                status = (await org.Owner.Spa.PostAsync(OpenCasePath(issueId), org.Owner.Token, Body(org))).StatusCode;
            }
            catch (Exception exception) when (exception.ToString().Contains("simulated failure", StringComparison.Ordinal))
            {
                // The test server rethrows the unhandled database exception to the caller.
            }

            status.ShouldBeOneOf(null, HttpStatusCode.InternalServerError);
        }
        finally
        {
            await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
            await dbContext.Database.ExecuteSqlRawAsync(uninstall, CancellationToken);
        }

        (await IssueRowAsync(org, issueId)).ShouldBe(before, "the issue is exactly as it was");
        (await CaseCountAsync(org)).ShouldBe(0, "no case and no case event");

        // Nothing half-done blocks a later attempt.
        (await org.Owner.Spa.PostAsync(OpenCasePath(issueId), org.Owner.Token, Body(org))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Two_people_opening_a_case_at_once_make_exactly_one_case()
    {
        var org = await CreateOrganizationAsync();
        var issueId = await CreateIssueAsync(org, assignee: org.Handler.AccountId);

        var responses = await Task.WhenAll(
            org.Owner.Spa.PostAsync(OpenCasePath(issueId), org.Owner.Token, Body(org)),
            org.Handler.Spa.PostAsync(OpenCasePath(issueId), org.Handler.Token, Body(org)));

        responses.Select(response => response.StatusCode).OrderBy(code => code)
            .ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        (await CaseCountAsync(org)).ShouldBe(2, "one case and its created event");
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantIssueEvents.CountAsync(
                issueEvent => issueEvent.IssueId == issueId && issueEvent.Action == AssistantIssueEventAction.CaseOpened, CancellationToken))
            .ShouldBe(1);
    }

    [Fact]
    public async Task The_operations_summary_counts_a_not_assistant_issue_as_resolved()
    {
        var org = await CreateOrganizationAsync();
        await CreateIssueAsync(org);
        var issueId = await CreateIssueAsync(org);

        (await OperationsIssuesAsync(org)).GetProperty("openCount").GetInt32().ShouldBe(2);
        (await org.Owner.Spa.PostAsync(OpenCasePath(issueId), org.Owner.Token, Body(org))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var issues = await OperationsIssuesAsync(org);
        issues.GetProperty("openCount").GetInt32().ShouldBe(1);
        issues.GetProperty("averageResolutionHours").ValueKind.ShouldBe(JsonValueKind.Number, "resolved within the range");
        var summary = await BodyJsonAsync(await org.Owner.Spa.GetAsync("/api/v1/issues/summary", org.Owner.Token));
        summary.GetProperty("openCount").GetInt32().ShouldBe(1);
    }

    // --- Fixtures -------------------------------------------------------------------------------

    private sealed record Member(Guid AccountId, SpaClient Spa, string Token);

    /// <param name="Owner">Owns the assistant (manage-assistants), an internal employee.</param>
    /// <param name="Handler">May be assigned (handle-assistant-issues).</param>
    /// <param name="Colleague">Holds both permissions but neither owns the assistant nor is assigned.</param>
    /// <param name="GroupMember">A member of 設備組.</param>
    /// <param name="External">An external customer holding handle-assistant-issues.</param>
    private sealed record TestOrganization(
        Organization Organization,
        Member Admin,
        Member Owner,
        Member Handler,
        Member Colleague,
        Member GroupMember,
        Member External,
        Guid AssistantId,
        Guid Equipment,
        Guid Type);

    private object Body(
        TestOrganization org, Guid? typeId = null, Guid? groupId = null, DateTimeOffset? dueAt = null, string title = "派人檢查冷藏庫") =>
        new
        {
            typeId = typeId ?? org.Type,
            groupId = groupId ?? org.Equipment,
            dueAt = dueAt ?? _host.Clock.GetUtcNow().AddHours(72),
            title,
            description = Question,
        };

    private async Task<TestOrganization> CreateOrganizationAsync(string name = "另開案件商行")
    {
        var organization = await _host.CreateOrganizationAsync(name);
        var admin = await CreateMemberAsync(organization, "admin", "管理者", AccountRole.SmbAdmin);
        var owner = await CreateMemberAsync(organization, "owner", "擁有者", AccountRole.InternalEmployee, AccountPermission.ManageAssistants);
        var handler = await CreateMemberAsync(
            organization, "handler", "處理人", AccountRole.InternalEmployee, AccountPermission.HandleAssistantIssues);
        var colleague = await CreateMemberAsync(
            organization, "colleague", "同事", AccountRole.InternalEmployee,
            AccountPermission.ManageAssistants, AccountPermission.HandleAssistantIssues);
        var groupMember = await CreateMemberAsync(
            organization, "member", "設備組成員", AccountRole.InternalEmployee, AccountPermission.UseSharedAssistants);
        var external = await CreateMemberAsync(
            organization, "external", "外部客戶", AccountRole.ExternalCustomer, AccountPermission.HandleAssistantIssues);

        await using (var dbContext = _host.Postgres.CreateDbContext(organization.Id))
        {
            var assistant = Assistant.Create(
                organization.Id, owner.AccountId, "設備助理", "回答設備問題", null,
                AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
                showCitations: true, keepConversations: true, DateTimeOffset.UtcNow);
            dbContext.Assistants.Add(assistant);
            await dbContext.SaveChangesAsync(CancellationToken);
            var equipment = await CreateGroupAsync(admin, "設備組");
            (await admin.Spa.PutAsync($"{GroupsPath}/{equipment}/members", admin.Token, new { accountIds = new[] { groupMember.AccountId } }))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            var type = await CreateTypeAsync(admin, "設備故障報修", equipment, 72);
            return new TestOrganization(organization, admin, owner, handler, colleague, groupMember, external, assistant.Id, equipment, type);
        }
    }

    private async Task<Member> CreateMemberAsync(
        Organization organization, string loginName, string displayName, AccountRole role, params AccountPermission[] permissions)
    {
        var account = await _host.CreateAccountAsync(organization, loginName, Password, role, displayName, permissions);
        var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, loginName, Password)).AccessToken;
        return new Member(account.Id, spa, token);
    }

    private static async Task<Guid> CreateGroupAsync(Member admin, string name)
    {
        var response = await admin.Spa.PostAsync(GroupsPath, admin.Token, new { name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateTypeAsync(Member admin, string name, Guid groupId, int hours, bool isActive = true)
    {
        var response = await admin.Spa.PostAsync(TypesPath, admin.Token, new
        {
            name, description = "", defaultGroupId = groupId, defaultDueHours = hours, isActive,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Opens a test-failure issue through the endpoint as the owner, on a fresh failed result.</summary>
    private async Task<Guid> CreateIssueAsync(TestOrganization org, Guid? assignee = null)
    {
        var now = DateTimeOffset.UtcNow;
        Guid resultId;
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var testCase = new AssistantTestCase(
                org.Organization.Id, org.AssistantId, Question, AssistantTestCaseCategory.Common, AssistantTestExpectedKind.CompanyData,
                [Guid.NewGuid()], null, ordinal: 1, now);
            var run = AssistantTestRun.Queue(org.Organization.Id, org.AssistantId, AssistantTestRunTrigger.Manual, now);
            run.Start("test", AuthHostFixture.ChatModel, 0.3, now);
            run.Complete(0, 1, now);
            var result = AssistantTestResult.Record(
                run, testCase, AnswerReplyKind.CompanyData, Answer, [Guid.NewGuid()], null, 0.8, AssistantTestFailureReason.MissingDocument);
            dbContext.AssistantTestCases.Add(testCase);
            dbContext.AssistantTestRuns.Add(run);
            dbContext.AssistantTestResults.Add(result);
            await dbContext.SaveChangesAsync(CancellationToken);
            resultId = result.Id;
        }

        var response = await org.Owner.Spa.PostAsync($"/api/v1/assistants/{org.AssistantId}/issues", org.Owner.Token, new
        {
            testResultId = resultId,
            assigneeAccountId = assignee,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Everything about the issue row that 「另開案件」 would change, plus its event count.</summary>
    private async Task<(AssistantIssueStatus, DateTimeOffset?, AssistantIssueResolutionKind?, Guid?, string?, int, int)> IssueRowAsync(
        TestOrganization org, Guid issueId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var row = await dbContext.AssistantIssues.AsNoTracking().SingleAsync(candidate => candidate.Id == issueId, CancellationToken);
        var events = await dbContext.AssistantIssueEvents.CountAsync(issueEvent => issueEvent.IssueId == issueId, CancellationToken);
        return (row.Status, row.ResolvedAt, row.ResolutionKind, row.LinkedCaseId, row.ResolutionNote, row.EventCount, events);
    }

    private async Task<int> CaseCountAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.Cases.CountAsync(CancellationToken) + await dbContext.CaseEvents.CountAsync(CancellationToken);
    }

    private static async Task<(Guid, bool)> IssueLinkAsync(Member caller, Guid caseId)
    {
        var response = await caller.Spa.GetAsync($"/api/v1/cases/{caseId}", caller.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var link = (await BodyJsonAsync(response)).GetProperty("links").GetProperty("assistantIssue");
        return (link.GetProperty("issueId").GetGuid(), link.GetProperty("canOpen").GetBoolean());
    }

    private static async Task<JsonElement> OperationsIssuesAsync(TestOrganization org)
    {
        var response = await org.Owner.Spa.GetAsync("/api/v1/operations/summary", org.Owner.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("issues");
    }

    private static async Task PatchOkAsync(Member member, Guid issueId, object body)
    {
        var response = await member.Spa.PatchAsync(IssuePath(issueId), member.Token, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<JsonElement> AssertReasonAsync(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe(reason);
        return body;
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();

    private static async Task AssertIdenticalAsync(HttpResponseMessage actual, ResponseFingerprint expected, string who)
    {
        var fingerprint = await ResponseFingerprint.FromAsync(actual);
        fingerprint.Status.ShouldBe(expected.Status, who);
        fingerprint.ContentType.ShouldBe(expected.ContentType, who);
        fingerprint.Body.ShouldBe(expected.Body, who);
        fingerprint.SetsCookie.ShouldBe(expected.SetsCookie, who);
    }
}
