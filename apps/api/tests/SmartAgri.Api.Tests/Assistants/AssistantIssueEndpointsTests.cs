using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Assistants;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// 處理事項: <c>POST /api/v1/assistants/{id}/issues</c> and <c>/api/v1/issues[...]</c> against
/// real PostgreSQL (M3.5 plan §5 Slice 4, issue #126). Test runs and results are seeded directly
/// through the domain (the run pipeline itself is covered by <c>AssistantTestRunEndpointsTests</c>).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class AssistantIssueEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Issue-Endpoint-Pass-1!";
    private const string FailedQuestion = "收到商品後七天內可申請退貨嗎？";
    private const string FailedAnswer = "運費說明：訂單滿一千元即享免費配送。";

    private readonly AuthHostFixture _host;

    public AssistantIssueEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static string CreatePath(Guid assistantId) => $"/api/v1/assistants/{assistantId}/issues";

    private static string IssuePath(Guid issueId) => $"/api/v1/issues/{issueId}";

    // --- Create from a test result ---------------------------------------------------------------

    [Fact]
    public async Task Creating_from_a_failed_test_result_snapshots_it_and_records_a_created_event()
    {
        var org = await CreateOrganizationWithOwnerAsync();
        var (runId, resultId) = await SeedResultAsync(org, org.AssistantId, passed: false);

        var response = await org.Owner.Spa.PostAsync(CreatePath(org.AssistantId), org.Owner.Token, new { testResultId = resultId });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "AssistantIssueView");
        var issueId = body.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe(IssuePath(issueId));
        body.GetProperty("source").GetString().ShouldBe("test-failure");
        body.GetProperty("status").GetString().ShouldBe("open");
        body.GetProperty("title").GetString().ShouldBe(FailedQuestion);
        body.GetProperty("question").GetString().ShouldBe(FailedQuestion);
        body.GetProperty("answer").GetString().ShouldBe(FailedAnswer);
        body.GetProperty("testFailureReason").GetString().ShouldBe("missing-document");
        body.GetProperty("testRunId").GetGuid().ShouldBe(runId);
        body.GetProperty("testResultId").GetGuid().ShouldBe(resultId);
        body.GetProperty("assistantName").GetString().ShouldBe("退貨小幫手");
        body.GetProperty("reporterAccountId").GetGuid().ShouldBe(org.Owner.AccountId);
        body.GetProperty("assigneeAccountId").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("viewerIsAssistantOwner").GetBoolean().ShouldBeTrue();
        body.GetProperty("viewerIsAssignee").GetBoolean().ShouldBeFalse();

        var detail = await org.Owner.Spa.GetAsync(IssuePath(issueId), org.Owner.Token);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detailBody = await BodyJsonAsync(detail);
        OpenApiContract.AssertKeysMatchSchema(detailBody, "AssistantIssueDetailView");
        var created = detailBody.GetProperty("events").EnumerateArray().ShouldHaveSingleItem();
        created.GetProperty("action").GetString().ShouldBe("created");
        created.GetProperty("actorAccountId").GetGuid().ShouldBe(org.Owner.AccountId);
        created.GetProperty("actorDisplayName").GetString().ShouldBe("擁有者");
        created.GetProperty("status").GetString().ShouldBe("open");

        // The issue keeps its snapshot even after the run (and its results) are pruned.
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            await dbContext.AssistantTestRuns.Where(run => run.Id == runId).ExecuteDeleteAsync(CancellationToken);
        }

        var afterPrune = await BodyJsonAsync(await org.Owner.Spa.GetAsync(IssuePath(issueId), org.Owner.Token));
        afterPrune.GetProperty("issue").GetProperty("question").GetString().ShouldBe(FailedQuestion);
    }

    [Fact]
    public async Task A_result_of_another_assistant_a_passed_result_and_a_missing_one_are_rejected_and_a_duplicate_is_409()
    {
        var org = await CreateOrganizationWithOwnerAsync();
        var secondAssistant = await AddAssistantAsync(org.Organization.Id, org.Owner.AccountId, "第二個助理");
        var (_, otherAssistantsResult) = await SeedResultAsync(org, secondAssistant, passed: false);
        var (_, passedResult) = await SeedResultAsync(org, org.AssistantId, passed: true);
        var (_, failedResult) = await SeedResultAsync(org, org.AssistantId, passed: false);

        var viaWrongAssistant = await org.Owner.Spa.PostAsync(
            CreatePath(org.AssistantId), org.Owner.Token, new { testResultId = otherAssistantsResult });
        await AssertReasonAsync(viaWrongAssistant, HttpStatusCode.UnprocessableEntity, AssistantIssueEndpoints.TestResultNotFoundReason);

        var missing = await org.Owner.Spa.PostAsync(CreatePath(org.AssistantId), org.Owner.Token, new { testResultId = Guid.NewGuid() });
        await AssertReasonAsync(missing, HttpStatusCode.UnprocessableEntity, AssistantIssueEndpoints.TestResultNotFoundReason);

        var noId = await org.Owner.Spa.PostAsync(CreatePath(org.AssistantId), org.Owner.Token, new { });
        await AssertReasonAsync(noId, HttpStatusCode.UnprocessableEntity, AssistantIssueEndpoints.TestResultNotFoundReason);

        var passed = await org.Owner.Spa.PostAsync(CreatePath(org.AssistantId), org.Owner.Token, new { testResultId = passedResult });
        await AssertReasonAsync(passed, HttpStatusCode.UnprocessableEntity, AssistantIssueEndpoints.TestResultPassedReason);

        var first = await org.Owner.Spa.PostAsync(CreatePath(org.AssistantId), org.Owner.Token, new { testResultId = failedResult });
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var firstId = (await BodyJsonAsync(first)).GetProperty("id").GetGuid();
        var duplicate = await org.Owner.Spa.PostAsync(CreatePath(org.AssistantId), org.Owner.Token, new { testResultId = failedResult });
        var duplicateBody = await AssertReasonAsync(duplicate, HttpStatusCode.Conflict, AssistantIssueEndpoints.IssueAlreadyOpenReason);
        duplicateBody.GetProperty("issueId").GetString().ShouldBe(firstId.ToString());

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantIssues.CountAsync(CancellationToken)).ShouldBe(1);
    }

    // --- List filters per role ---------------------------------------------------------------------

    [Fact]
    public async Task The_list_shows_each_role_only_its_own_issues_and_an_assignee_sees_theirs_without_owning_the_assistant()
    {
        var org = await CreateOrganizationWithOwnerAsync();
        var handler = await CreateMemberAsync(org.Organization, "handler", "處理人", AccountPermission.HandleAssistantIssues);
        var colleague = await CreateMemberAsync(
            org.Organization, "colleague", "同事", AccountPermission.ManageAssistants, AccountPermission.HandleAssistantIssues);
        var colleaguesAssistant = await AddAssistantAsync(org.Organization.Id, colleague.AccountId, "同事的助理");

        var assigned = await CreateIssueAsync(org, org.AssistantId, assignee: handler.AccountId);
        var unassigned = await CreateIssueAsync(org, org.AssistantId);
        var colleaguesIssue = await CreateIssueAsync(org, colleaguesAssistant, owner: colleague);

        // Owner: both issues of their own assistant, not the colleague's.
        (await ListIdsAsync(org.Owner)).ShouldBe([unassigned, assigned]);
        (await ListIdsAsync(org.Owner, "?scope=owned")).ShouldBe([unassigned, assigned]);
        (await ListIdsAsync(org.Owner, "?scope=assigned")).ShouldBeEmpty();
        (await ListIdsAsync(org.Owner, "?scope=forwarded")).ShouldBeEmpty();

        // Assignee: only the one assigned to them, although they own no assistant.
        (await ListIdsAsync(handler)).ShouldBe([assigned]);
        (await ListIdsAsync(handler, "?scope=assigned")).ShouldBe([assigned]);
        (await ListIdsAsync(handler, "?scope=owned")).ShouldBeEmpty();
        var handlerView = await BodyJsonAsync(await handler.Spa.GetAsync(IssuePath(assigned), handler.Token));
        OpenApiContract.AssertKeysMatchSchema(handlerView, "AssistantIssueDetailView");
        handlerView.GetProperty("issue").GetProperty("viewerIsAssignee").GetBoolean().ShouldBeTrue();
        handlerView.GetProperty("issue").GetProperty("viewerIsAssistantOwner").GetBoolean().ShouldBeFalse();
        handlerView.GetProperty("issue").GetProperty("assigneeDisplayName").GetString().ShouldBe("處理人");

        // Colleague (can manage assistants and handle issues): only their own assistant's.
        (await ListIdsAsync(colleague)).ShouldBe([colleaguesIssue]);

        // Filters.
        (await ListIdsAsync(org.Owner, $"?assistantId={colleaguesAssistant}")).ShouldBeEmpty();
        (await ListIdsAsync(org.Owner, "?status=resolved")).ShouldBeEmpty();
        (await ListIdsAsync(org.Owner, "?status=open")).ShouldBe([unassigned, assigned]);

        var list = await BodyJsonAsync(await org.Owner.Spa.GetAsync("/api/v1/issues", org.Owner.Token));
        foreach (var item in list.EnumerateArray())
        {
            OpenApiContract.AssertKeysMatchSchema(item, "AssistantIssueView");
        }

        var badScope = await org.Owner.Spa.GetAsync("/api/v1/issues?scope=everything", org.Owner.Token);
        badScope.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var badStatus = await org.Owner.Spa.GetAsync("/api/v1/issues?status=closed", org.Owner.Token);
        badStatus.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    // --- Assignment -----------------------------------------------------------------------------

    [Fact]
    public async Task Only_an_account_of_the_same_organization_with_handle_assistant_issues_can_be_assigned()
    {
        var org = await CreateOrganizationWithOwnerAsync();
        var withoutPermission = await CreateMemberAsync(org.Organization, "plain", "沒有權限的同仁", AccountPermission.ManageAssistants);
        var handler = await CreateMemberAsync(org.Organization, "handler", "處理人", AccountPermission.HandleAssistantIssues);
        var otherOrganization = await _host.CreateOrganizationAsync("別的組織");
        var foreignHandler = await _host.CreateAccountAsync(
            otherOrganization, "handler", Password, AccountRole.SmbAdmin, "別的組織處理人", AccountPermission.HandleAssistantIssues);
        var issueId = await CreateIssueAsync(org, org.AssistantId);

        foreach (var ineligible in new[] { withoutPermission.AccountId, foreignHandler.Id, Guid.NewGuid() })
        {
            var refused = await org.Owner.Spa.PatchAsync(IssuePath(issueId), org.Owner.Token, new { assigneeAccountId = ineligible });
            await AssertReasonAsync(refused, HttpStatusCode.UnprocessableEntity, AssistantIssueEndpoints.AssigneeNotEligibleReason);
        }

        var refusedOnCreate = await org.Owner.Spa.PostAsync(CreatePath(org.AssistantId), org.Owner.Token, new
        {
            testResultId = (await SeedResultAsync(org, org.AssistantId, passed: false)).ResultId,
            assigneeAccountId = withoutPermission.AccountId,
        });
        await AssertReasonAsync(refusedOnCreate, HttpStatusCode.UnprocessableEntity, AssistantIssueEndpoints.AssigneeNotEligibleReason);

        var assigned = await org.Owner.Spa.PatchAsync(IssuePath(issueId), org.Owner.Token, new { assigneeAccountId = handler.AccountId });
        assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(assigned);
        OpenApiContract.AssertKeysMatchSchema(body, "AssistantIssueDetailView");
        body.GetProperty("issue").GetProperty("assigneeAccountId").GetGuid().ShouldBe(handler.AccountId);
        var events = body.GetProperty("events").EnumerateArray().ToList();
        events.Select(issueEvent => issueEvent.GetProperty("action").GetString()).ShouldBe(["created", "assigned"]);
        events[1].GetProperty("assigneeAccountId").GetGuid().ShouldBe(handler.AccountId);
        events[1].GetProperty("assigneeDisplayName").GetString().ShouldBe("處理人");
        events[1].GetProperty("actorAccountId").GetGuid().ShouldBe(org.Owner.AccountId);

        // Assigning the same account again records nothing; unassigning does.
        var again = await BodyJsonAsync(
            await org.Owner.Spa.PatchAsync(IssuePath(issueId), org.Owner.Token, new { assigneeAccountId = handler.AccountId }));
        again.GetProperty("events").GetArrayLength().ShouldBe(2);
        var unassigned = await BodyJsonAsync(await org.Owner.Spa.PatchAsync(IssuePath(issueId), org.Owner.Token, new { unassign = true }));
        unassigned.GetProperty("issue").GetProperty("assigneeAccountId").ValueKind.ShouldBe(JsonValueKind.Null);
        var last = unassigned.GetProperty("events").EnumerateArray().Last();
        last.GetProperty("action").GetString().ShouldBe("assigned");
        last.GetProperty("assigneeAccountId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // --- Status changes, notes, history ---------------------------------------------------------

    [Fact]
    public async Task Status_changes_and_notes_each_record_an_event_and_the_history_is_ordered()
    {
        var org = await CreateOrganizationWithOwnerAsync();
        var handler = await CreateMemberAsync(org.Organization, "handler", "處理人", AccountPermission.HandleAssistantIssues);
        var issueId = await CreateIssueAsync(org, org.AssistantId, assignee: handler.AccountId);
        var due = DateTimeOffset.UtcNow.AddDays(3);

        // The assignee handles it (without owning the assistant).
        (await handler.Spa.PatchAsync(IssuePath(issueId), handler.Token, new { status = "in-progress", note = "開始查看" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await handler.Spa.PatchAsync(IssuePath(issueId), handler.Token, new { note = "退貨政策文件需要補上七天的說明" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Owner.Spa.PatchAsync(IssuePath(issueId), org.Owner.Token, new { dueAt = due }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var resolved = await handler.Spa.PatchAsync(IssuePath(issueId), handler.Token, new { status = "resolved", note = "已更新文件並重跑" });
        resolved.StatusCode.ShouldBe(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync(CancellationToken));

        var body = await BodyJsonAsync(resolved);
        OpenApiContract.AssertKeysMatchSchema(body, "AssistantIssueDetailView");
        var issue = body.GetProperty("issue");
        issue.GetProperty("status").GetString().ShouldBe("resolved");
        issue.GetProperty("resolutionNote").GetString().ShouldBe("已更新文件並重跑");
        issue.GetProperty("resolvedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);

        var events = body.GetProperty("events").EnumerateArray().ToList();
        events.Select(issueEvent => issueEvent.GetProperty("action").GetString())
            .ShouldBe(["created", "status-changed", "commented", "due-date-changed", "status-changed"]);
        events.Select(issueEvent => issueEvent.GetProperty("at").GetDateTimeOffset()).ShouldBeInOrder();
        events[1].GetProperty("status").GetString().ShouldBe("in-progress");
        events[1].GetProperty("note").GetString().ShouldBe("開始查看");
        events[1].GetProperty("actorDisplayName").GetString().ShouldBe("處理人");
        events[2].GetProperty("note").GetString().ShouldBe("退貨政策文件需要補上七天的說明");
        events[2].GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
        events[3].GetProperty("actorAccountId").GetGuid().ShouldBe(org.Owner.AccountId);
        events[4].GetProperty("status").GetString().ShouldBe("resolved");

        // Reopening clears the resolution.
        var reopened = await BodyJsonAsync(await org.Owner.Spa.PatchAsync(IssuePath(issueId), org.Owner.Token, new { status = "open" }));
        reopened.GetProperty("issue").GetProperty("resolvedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        reopened.GetProperty("issue").GetProperty("resolutionNote").ValueKind.ShouldBe(JsonValueKind.Null);
        reopened.GetProperty("events").GetArrayLength().ShouldBe(6);

        // Nothing to change, or an unknown status, is 422 and records nothing.
        (await org.Owner.Spa.PatchAsync(IssuePath(issueId), org.Owner.Token, new { })).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await org.Owner.Spa.PatchAsync(IssuePath(issueId), org.Owner.Token, new { status = "closed" })).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantIssueEvents.CountAsync(issueEvent => issueEvent.IssueId == issueId, CancellationToken)).ShouldBe(6);
        (await dbContext.AssistantIssueEvents.Where(issueEvent => issueEvent.IssueId == issueId)
                .OrderBy(issueEvent => issueEvent.Ordinal).Select(issueEvent => issueEvent.Ordinal).ToListAsync(CancellationToken))
            .ShouldBe([1, 2, 3, 4, 5, 6]);
    }

    // --- Summaries ------------------------------------------------------------------------------

    [Fact]
    public async Task The_summary_counts_the_callers_unresolved_issues()
    {
        var org = await CreateOrganizationWithOwnerAsync(ownerCanHandleIssues: true);
        var handler = await CreateMemberAsync(org.Organization, "handler", "處理人", AccountPermission.HandleAssistantIssues);

        var open = await CreateIssueAsync(org, org.AssistantId, assignee: org.Owner.AccountId);
        var inProgress = await CreateIssueAsync(org, org.AssistantId, assignee: handler.AccountId);
        var overdue = await CreateIssueAsync(org, org.AssistantId);
        var resolved = await CreateIssueAsync(org, org.AssistantId, assignee: org.Owner.AccountId);
        await PatchOkAsync(org.Owner, inProgress, new { status = "in-progress" });
        await PatchOkAsync(org.Owner, overdue, new { dueAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await PatchOkAsync(org.Owner, resolved, new { status = "resolved", dueAt = DateTimeOffset.UtcNow.AddDays(-1) });
        _ = open;

        var ownerSummary = await BodyJsonAsync(await org.Owner.Spa.GetAsync("/api/v1/issues/summary", org.Owner.Token));
        OpenApiContract.AssertKeysMatchSchema(ownerSummary, "AssistantIssueSummaryView");
        ownerSummary.GetProperty("openCount").GetInt32().ShouldBe(2);
        ownerSummary.GetProperty("inProgressCount").GetInt32().ShouldBe(1);
        ownerSummary.GetProperty("assignedToMeCount").GetInt32().ShouldBe(1, "the resolved one no longer counts");
        ownerSummary.GetProperty("overdueCount").GetInt32().ShouldBe(1, "a resolved issue is never overdue");

        var handlerSummary = await BodyJsonAsync(await handler.Spa.GetAsync("/api/v1/issues/summary", handler.Token));
        OpenApiContract.AssertKeysMatchSchema(handlerSummary, "AssistantIssueSummaryView");
        (handlerSummary.GetProperty("openCount").GetInt32(), handlerSummary.GetProperty("inProgressCount").GetInt32(),
                handlerSummary.GetProperty("assignedToMeCount").GetInt32(), handlerSummary.GetProperty("overdueCount").GetInt32())
            .ShouldBe((0, 1, 1, 0));
    }

    [Fact]
    public async Task The_operations_summary_issue_numbers_match_the_rows()
    {
        var org = await CreateOrganizationWithOwnerAsync();
        var now = DateTimeOffset.UtcNow;

        // Resolved within the default range after 2h and 4h (average 3h); one resolved long
        // before the range (ignored); one open and one in progress (the backlog).
        await SeedIssueAsync(org, createdAt: now.AddDays(-3), resolvedAfter: TimeSpan.FromHours(2));
        await SeedIssueAsync(org, createdAt: now.AddDays(-2), resolvedAfter: TimeSpan.FromHours(4));
        await SeedIssueAsync(org, createdAt: now.AddDays(-200), resolvedAfter: TimeSpan.FromHours(100));
        await SeedIssueAsync(org, createdAt: now.AddDays(-1), resolvedAfter: null);
        await SeedIssueAsync(org, createdAt: now.AddDays(-1), resolvedAfter: null, inProgress: true);

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await dbContext.AssistantIssues.CountAsync(issue => issue.Status != AssistantIssueStatus.Resolved, CancellationToken))
                .ShouldBe(2);
        }

        var response = await org.Owner.Spa.GetAsync("/api/v1/operations/summary", org.Owner.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "OperationsSummaryView");
        var issues = body.GetProperty("issues");
        issues.GetProperty("openCount").GetInt32().ShouldBe(2);
        issues.GetProperty("averageResolutionHours").GetDouble().ShouldBe(3, tolerance: 0.001);

        // Another organization's issues never count.
        var other = await CreateOrganizationWithOwnerAsync();
        var otherIssues = (await BodyJsonAsync(await other.Owner.Spa.GetAsync("/api/v1/operations/summary", other.Owner.Token)))
            .GetProperty("issues");
        otherIssues.GetProperty("openCount").GetInt32().ShouldBe(0);
        otherIssues.GetProperty("averageResolutionHours").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // --- Permissions ----------------------------------------------------------------------------

    [Fact]
    public async Task Someone_who_may_not_see_an_issue_gets_the_byte_identical_403_of_a_missing_one()
    {
        var org = await CreateOrganizationWithOwnerAsync();
        var handler = await CreateMemberAsync(org.Organization, "handler", "處理人", AccountPermission.HandleAssistantIssues);
        var issueId = await CreateIssueAsync(org, org.AssistantId, assignee: handler.AccountId);

        var colleague = await CreateMemberAsync(
            org.Organization, "colleague", "同事", AccountPermission.ManageAssistants, AccountPermission.HandleAssistantIssues);
        var otherOrganization = await CreateOrganizationWithOwnerAsync(ownerCanHandleIssues: true);

        var missingId = Guid.NewGuid();
        var reference = await ResponseFingerprint.FromAsync(await org.Owner.Spa.GetAsync(IssuePath(missingId), org.Owner.Token));
        reference.Status.ShouldBe(HttpStatusCode.Forbidden);
        JsonDocument.Parse(reference.Body).RootElement.GetProperty("reason").GetString().ShouldBe("assistant-issue");
        var referencePatch = await ResponseFingerprint.FromAsync(
            await org.Owner.Spa.PatchAsync(IssuePath(missingId), org.Owner.Token, new { note = "x" }));
        referencePatch.Body.ShouldBe(reference.Body);

        foreach (var outsider in new[] { colleague, otherOrganization.Owner })
        {
            await AssertIdenticalAsync(await outsider.Spa.GetAsync(IssuePath(issueId), outsider.Token), reference);
            await AssertIdenticalAsync(await outsider.Spa.PatchAsync(IssuePath(issueId), outsider.Token, new { note = "x" }), referencePatch);
            (await ListIdsAsync(outsider)).ShouldNotContain(issueId);
        }

        // The assignee loses handle-assistant-issues: the issue is no longer theirs to see.
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            await dbContext.AccountPermissions
                .Where(grant => grant.AccountId == handler.AccountId && grant.Permission == AccountPermission.HandleAssistantIssues)
                .ExecuteDeleteAsync(CancellationToken);
        }

        await AssertIdenticalAsync(await handler.Spa.GetAsync(IssuePath(issueId), handler.Token), reference);
        (await ListIdsAsync(handler)).ShouldBeEmpty();

        // The owner who loses manage-assistants likewise.
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            await dbContext.AccountPermissions
                .Where(grant => grant.AccountId == org.Owner.AccountId && grant.Permission == AccountPermission.ManageAssistants)
                .ExecuteDeleteAsync(CancellationToken);
        }

        await AssertIdenticalAsync(await org.Owner.Spa.GetAsync(IssuePath(issueId), org.Owner.Token), reference);
    }

    [Fact]
    public async Task Creating_on_another_organizations_a_colleagues_or_a_missing_assistant_is_the_same_403()
    {
        var org = await CreateOrganizationWithOwnerAsync();
        var (_, resultId) = await SeedResultAsync(org, org.AssistantId, passed: false);
        var colleague = await CreateMemberAsync(org.Organization, "colleague", "同事", AccountPermission.ManageAssistants);
        var other = await CreateOrganizationWithOwnerAsync();

        var missing = await ResponseFingerprint.FromAsync(
            await org.Owner.Spa.PostAsync(CreatePath(Guid.NewGuid()), org.Owner.Token, new { testResultId = resultId }));
        missing.Status.ShouldBe(HttpStatusCode.Forbidden);
        JsonDocument.Parse(missing.Body).RootElement.GetProperty("reason").GetString().ShouldBe("assistant-configuration");

        await AssertIdenticalAsync(
            await colleague.Spa.PostAsync(CreatePath(org.AssistantId), colleague.Token, new { testResultId = resultId }), missing);
        await AssertIdenticalAsync(
            await other.Owner.Spa.PostAsync(CreatePath(org.AssistantId), other.Owner.Token, new { testResultId = resultId }), missing);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AssistantIssues.CountAsync(CancellationToken)).ShouldBe(0);
    }

    // --- Fixtures -------------------------------------------------------------------------------

    private sealed record Member(Guid AccountId, SpaClient Spa, string Token);

    private sealed record OrganizationWithOwner(Organization Organization, Member Owner, Guid AssistantId);

    private async Task<OrganizationWithOwner> CreateOrganizationWithOwnerAsync(bool ownerCanHandleIssues = false)
    {
        var organization = await _host.CreateOrganizationAsync("處理事項商行");
        AccountPermission[] permissions = ownerCanHandleIssues
            ? [AccountPermission.ManageAssistants, AccountPermission.HandleAssistantIssues]
            : [AccountPermission.ManageAssistants];
        var owner = await CreateMemberAsync(organization, "owner", "擁有者", permissions);
        var assistantId = await AddAssistantAsync(organization.Id, owner.AccountId, "退貨小幫手");
        return new OrganizationWithOwner(organization, owner, assistantId);
    }

    private async Task<Member> CreateMemberAsync(
        Organization organization, string loginName, string displayName, params AccountPermission[] permissions)
    {
        var account = await _host.CreateAccountAsync(organization, loginName, Password, AccountRole.SmbAdmin, displayName, permissions);
        var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, loginName, Password)).AccessToken;
        return new Member(account.Id, spa, token);
    }

    private async Task<Guid> AddAssistantAsync(Guid organizationId, Guid ownerAccountId, string name)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);
        var assistant = Assistant.Create(
            organizationId, ownerAccountId, name, "回答退換貨問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
            showCitations: true, keepConversations: true, DateTimeOffset.UtcNow);
        dbContext.Assistants.Add(assistant);
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    /// <summary>A completed run of <paramref name="assistantId"/> with one result: passed, or a
    /// <c>missing-document</c> failure.</summary>
    private async Task<(Guid RunId, Guid ResultId)> SeedResultAsync(OrganizationWithOwner org, Guid assistantId, bool passed)
    {
        var (run, result) = await SeedRunAsync(org.Organization.Id, assistantId, passed, DateTimeOffset.UtcNow);
        return (run.Id, result.Id);
    }

    private async Task<(AssistantTestRun Run, AssistantTestResult Result)> SeedRunAsync(
        Guid organizationId, Guid assistantId, bool passed, DateTimeOffset now)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);
        var testCase = new AssistantTestCase(
            organizationId, assistantId, FailedQuestion, AssistantTestCaseCategory.Common, AssistantTestExpectedKind.CompanyData,
            [Guid.NewGuid()], null, ordinal: 1, now);
        var run = AssistantTestRun.Queue(organizationId, assistantId, AssistantTestRunTrigger.Manual, now);
        run.Start("test", AuthHostFixture.ChatModel, 0.3, now);
        run.Complete(passed ? 1 : 0, passed ? 0 : 1, now);
        var result = AssistantTestResult.Record(
            run, testCase, AnswerReplyKind.CompanyData, FailedAnswer, [Guid.NewGuid()], null, 0.8,
            passed ? null : AssistantTestFailureReason.MissingDocument);
        dbContext.AssistantTestCases.Add(testCase);
        dbContext.AssistantTestRuns.Add(run);
        dbContext.AssistantTestResults.Add(result);
        await dbContext.SaveChangesAsync(CancellationToken);
        return (run, result);
    }

    /// <summary>Creates an issue through the endpoint, as <paramref name="owner"/> (the
    /// organization's owner by default), on a fresh failed result of <paramref name="assistantId"/>.</summary>
    private async Task<Guid> CreateIssueAsync(OrganizationWithOwner org, Guid assistantId, Guid? assignee = null, Member? owner = null)
    {
        var creator = owner ?? org.Owner;
        var (_, resultId) = await SeedResultAsync(org, assistantId, passed: false);
        var response = await creator.Spa.PostAsync(CreatePath(assistantId), creator.Token, new
        {
            testResultId = resultId,
            assigneeAccountId = assignee,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>An issue with chosen timestamps, written through the domain.</summary>
    private async Task SeedIssueAsync(OrganizationWithOwner org, DateTimeOffset createdAt, TimeSpan? resolvedAfter, bool inProgress = false)
    {
        var (run, result) = await SeedRunAsync(org.Organization.Id, org.AssistantId, passed: false, createdAt);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var (issue, created) = AssistantIssue.OpenFromTestFailure(
            org.AssistantId, run, result, null, org.Owner.AccountId, null, null, createdAt);
        dbContext.AssistantIssues.Add(issue);
        dbContext.AssistantIssueEvents.Add(created);
        if (inProgress)
        {
            dbContext.AssistantIssueEvents.Add(issue.ChangeStatus(AssistantIssueStatus.InProgress, null, org.Owner.AccountId, createdAt)!);
        }

        if (resolvedAfter is { } after)
        {
            dbContext.AssistantIssueEvents.Add(
                issue.ChangeStatus(AssistantIssueStatus.Resolved, null, org.Owner.AccountId, createdAt + after)!);
        }

        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private static async Task PatchOkAsync(Member member, Guid issueId, object body)
    {
        var response = await member.Spa.PatchAsync(IssuePath(issueId), member.Token, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<List<Guid>> ListIdsAsync(Member member, string query = "")
    {
        var response = await member.Spa.GetAsync("/api/v1/issues" + query, member.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return [.. (await BodyJsonAsync(response)).EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];
    }

    private static async Task<JsonElement> AssertReasonAsync(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe(reason);
        return body;
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AssertIdenticalAsync(HttpResponseMessage actual, ResponseFingerprint a)
    {
        var b = await ResponseFingerprint.FromAsync(actual);

        b.Status.ShouldBe(a.Status);
        b.ContentType.ShouldBe(a.ContentType);
        b.Body.ShouldBe(a.Body);
        b.SetsCookie.ShouldBe(a.SetsCookie);
    }
}
