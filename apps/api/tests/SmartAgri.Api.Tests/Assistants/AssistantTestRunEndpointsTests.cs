using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// 「全部重跑」: <c>/api/v1/assistants/{id}/test-runs[...]</c> and the
/// <c>assistants.run-test-set</c> job against real PostgreSQL, with the <c>Fake</c> embedding and
/// chat models (M3.5 plan §5 Slice 2, issue #124). The fake chat model always cites passage
/// <c>[1]</c> — the best-scoring one — and the fake embedding is a character hash, so questions
/// are written to overlap one document's text heavily.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed partial class AssistantTestRunEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "TestRun-Endpoint-Pass-1!";
    private const string ReturnClause = "收到商品後七天內可申請退貨，退貨運費由買家負擔。";
    private const string ShippingClause = "訂單滿一千元即享免費配送，偏遠離島另計。";
    private const string ReturnQuestion = "收到商品後七天內可申請退貨嗎？";
    private const string UnrelatedQuestion = "zzzz qqqq xxxx";

    private readonly AuthHostFixture _host;

    public AssistantTestRunEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static string BasePath(Guid assistantId) => $"/api/v1/assistants/{assistantId}/test-runs";

    // --- Acceptance: pass / missing-document / kind-mismatch -----------------------------------

    [Fact]
    public async Task A_run_answers_every_case_through_the_answer_pipeline_and_judges_it_by_kind_and_cited_documents()
    {
        var owner = await CreateOwnerWithAssistantAsync();
        var returnDocument = await UploadAndApproveAsync(owner, "退貨政策.md", ReturnClause);
        var shippingDocument = await UploadAndApproveAsync(owner, "運費說明.md", ShippingClause);

        var rightDocument = await CreateTestCaseAsync(owner, ReturnQuestion, "company-data", [returnDocument]);
        var wrongDocument = await CreateTestCaseAsync(owner, ReturnQuestion, "company-data", [shippingDocument]);
        var answeredInsteadOfRefusing = await CreateTestCaseAsync(owner, ReturnQuestion, "no-result", []);
        var refused = await CreateTestCaseAsync(owner, UnrelatedQuestion, "no-result", []);

        var queued = await RequestRunAsync(owner);
        queued.StatusCode.ShouldBe(HttpStatusCode.Accepted, await queued.Content.ReadAsStringAsync(CancellationToken));
        var queuedBody = await BodyJsonAsync(queued);
        OpenApiContract.AssertKeysMatchSchema(queuedBody, "AssistantTestRunView");
        queuedBody.GetProperty("status").GetString().ShouldBe("queued");
        queuedBody.GetProperty("trigger").GetString().ShouldBe("manual");
        queuedBody.GetProperty("rerunRequested").GetBoolean().ShouldBeFalse();
        var runId = queuedBody.GetProperty("id").GetGuid();
        queued.Headers.Location!.ToString().ShouldBe($"{BasePath(owner.AssistantId)}/{runId}");

        await RunJobsAsync();

        var detail = await owner.Spa.GetAsync($"{BasePath(owner.AssistantId)}/{runId}", owner.Token);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detailBody = await BodyJsonAsync(detail);
        OpenApiContract.AssertKeysMatchSchema(detailBody, "AssistantTestRunDetailView");
        var run = detailBody.GetProperty("run");
        run.GetProperty("status").GetString().ShouldBe("completed");
        (run.GetProperty("passedCount").GetInt32(), run.GetProperty("failedCount").GetInt32()).ShouldBe((2, 2));
        run.GetProperty("model").GetString().ShouldBe(AuthHostFixture.ChatModel);
        run.GetProperty("promptVersion").GetString().ShouldNotBeNullOrEmpty();
        run.GetProperty("minScore").GetDouble().ShouldBeGreaterThan(0);

        var results = detailBody.GetProperty("results").EnumerateArray().ToList();
        results.Select(result => result.GetProperty("testCaseId").GetGuid())
            .ShouldBe([rightDocument, wrongDocument, answeredInsteadOfRefusing, refused]);

        // 預期 company-data，而且引用對的文件：通過。
        results[0].GetProperty("passed").GetBoolean().ShouldBeTrue();
        results[0].GetProperty("failureReason").ValueKind.ShouldBe(JsonValueKind.Null);
        results[0].GetProperty("actualKind").GetString().ShouldBe("company-data");
        results[0].GetProperty("citedDocumentIds").EnumerateArray().Select(id => id.GetGuid()).ShouldBe([returnDocument]);
        results[0].GetProperty("question").GetString().ShouldBe(ReturnQuestion);
        results[0].GetProperty("answerText").GetString().ShouldNotBeNullOrWhiteSpace();

        // 引用錯的文件：missing-document。
        results[1].GetProperty("passed").GetBoolean().ShouldBeFalse();
        results[1].GetProperty("failureReason").GetString().ShouldBe("missing-document");

        // 預期 no-result 卻回答了：kind-mismatch。
        results[2].GetProperty("failureReason").GetString().ShouldBe("kind-mismatch");
        results[2].GetProperty("actualKind").GetString().ShouldBe("company-data");

        // 預期 no-result，也真的查無資料：通過，附拒絕原因。
        results[3].GetProperty("passed").GetBoolean().ShouldBeTrue();
        results[3].GetProperty("actualKind").GetString().ShouldBe("no-result");
        results[3].GetProperty("rejectionReason").GetString().ShouldBe("below-threshold");

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var modelCalls = await dbContext.ModelInvocations.AsNoTracking()
            .Where(invocation => invocation.Purpose == ModelInvocationPurpose.AssistantTest)
            .ToListAsync(CancellationToken);
        modelCalls.Count.ShouldBe(3, "the below-threshold question never reaches the model");
        modelCalls.ShouldAllBe(call => call.AccountId == owner.AccountId && call.AssistantId == owner.AssistantId);
        (await dbContext.AnswerOutcomes.CountAsync(
                outcome => outcome.AssistantId == owner.AssistantId && outcome.Channel == AnswerOutcomeChannel.TestRun, CancellationToken))
            .ShouldBe(4);

        var history = await owner.Spa.GetAsync(BasePath(owner.AssistantId), owner.Token);
        history.StatusCode.ShouldBe(HttpStatusCode.OK);
        var historyBody = await BodyJsonAsync(history);
        historyBody.GetArrayLength().ShouldBe(1);
        OpenApiContract.AssertKeysMatchSchema(historyBody[0], "AssistantTestRunView");
        historyBody[0].GetProperty("id").GetGuid().ShouldBe(runId);
    }

    // --- #302: the candidate threshold a run used ---------------------------------------------

    [Fact]
    public async Task A_run_records_the_candidate_threshold_in_effect_and_each_outcome_whether_candidates_were_used()
    {
        // Development (MinScore 0.3, CandidateMinScore turned off): no candidate band.
        var plain = await CreateOwnerWithAssistantAsync();
        await UploadAndApproveAsync(plain, "退貨政策.md", ReturnClause);
        await CreateTestCaseAsync(plain, UnrelatedQuestion, "no-result", []);
        await RequestRunAsync(plain);
        await RunJobsAsync();

        // A deployment whose threshold nothing reaches, with candidates from 0: every passage is a candidate.
        var banded = await CreateOwnerWithAssistantAsync();
        var returnDocument = await UploadAndApproveAsync(banded, "退貨政策.md", ReturnClause);
        await CreateTestCaseAsync(banded, ReturnQuestion, "company-data", [returnDocument]);
        await RequestRunAsync(banded);
        await using (var candidates = _host.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(new KnowledgeRetrievalSettings(MinScore: 0.99, Top: 5, CandidateMinScore: 0)))))
        {
            await candidates.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        }

        await using (var dbContext = _host.Postgres.CreateDbContext(plain.Organization.Id))
        {
            var run = await dbContext.AssistantTestRuns.AsNoTracking().SingleAsync(candidate => candidate.AssistantId == plain.AssistantId, CancellationToken);
            (run.Status, run.MinScore, run.CandidateMinScore).ShouldBe((AssistantTestRunStatus.Completed, (double?)0.3, (double?)null));
            var outcome = await dbContext.AnswerOutcomes.AsNoTracking()
                .SingleAsync(candidate => candidate.AssistantId == plain.AssistantId && candidate.Channel == AnswerOutcomeChannel.TestRun, CancellationToken);
            (outcome.RejectionReason, outcome.UsedCandidates).ShouldBe((AnswerRejectionReason.BelowThreshold, false));
        }

        await using var bandedContext = _host.Postgres.CreateDbContext(banded.Organization.Id);
        var bandedRun = await bandedContext.AssistantTestRuns.AsNoTracking().SingleAsync(run => run.AssistantId == banded.AssistantId, CancellationToken);
        (bandedRun.Status, bandedRun.MinScore, bandedRun.CandidateMinScore, bandedRun.PassedCount)
            .ShouldBe((AssistantTestRunStatus.Completed, (double?)0.99, (double?)0, 1));
        var bandedOutcome = await bandedContext.AnswerOutcomes.AsNoTracking()
            .SingleAsync(outcome => outcome.AssistantId == banded.AssistantId && outcome.Channel == AnswerOutcomeChannel.TestRun, CancellationToken);
        (bandedOutcome.ReplyKind, bandedOutcome.UsedCandidates).ShouldBe((AnswerReplyKind.CompanyData, true));
        bandedOutcome.CitedDocumentIds.ShouldBe([returnDocument]);
    }

    // --- Acceptance: a queued run only gets RerunRequested --------------------------------------

    [Fact]
    public async Task Triggering_while_a_run_is_queued_only_sets_rerun_requested_and_the_queued_run_covers_it()
    {
        var owner = await CreateOwnerWithAssistantAsync();
        await CreateTestCaseAsync(owner, UnrelatedQuestion, "no-result", []);

        var first = await BodyJsonAsync(await RequestRunAsync(owner));
        var second = await RequestRunAsync(owner);

        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var secondBody = await BodyJsonAsync(second);
        secondBody.GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());
        secondBody.GetProperty("status").GetString().ShouldBe("queued");
        secondBody.GetProperty("rerunRequested").GetBoolean().ShouldBeTrue();

        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var runs = await dbContext.AssistantTestRuns.AsNoTracking()
                .Where(run => run.AssistantId == owner.AssistantId)
                .ToListAsync(CancellationToken);
            var run = runs.ShouldHaveSingleItem("a second trigger never adds a row while one is queued");
            run.RerunRequested.ShouldBeTrue();
            (await RunJobsForAsync(dbContext, run.Id)).Count.ShouldBe(1);
        }

        await RunJobsAsync();

        // The queued run read the assistant's current state when it started, so no second run.
        await using var after = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var finished = (await after.AssistantTestRuns.AsNoTracking()
                .Where(run => run.AssistantId == owner.AssistantId)
                .ToListAsync(CancellationToken))
            .ShouldHaveSingleItem();
        (finished.Status, finished.RerunRequested).ShouldBe((AssistantTestRunStatus.Completed, false));
    }

    [Fact]
    public async Task A_rerun_asked_for_while_running_queues_one_more_run_when_it_finishes()
    {
        var owner = await CreateOwnerWithAssistantAsync();
        await CreateTestCaseAsync(owner, UnrelatedQuestion, "no-result", []);
        var runId = (await BodyJsonAsync(await RequestRunAsync(owner))).GetProperty("id").GetGuid();

        // As if the worker had just started it: running, and then the owner pressed again.
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var run = await dbContext.AssistantTestRuns.SingleAsync(candidate => candidate.Id == runId, CancellationToken);
            run.Start("test", AuthHostFixture.ChatModel, 0.3, DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var again = await BodyJsonAsync(await RequestRunAsync(owner));
        (again.GetProperty("id").GetGuid(), again.GetProperty("status").GetString(), again.GetProperty("rerunRequested").GetBoolean())
            .ShouldBe((runId, "running", true));

        await RunJobsAsync();

        await using var after = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var runs = await after.AssistantTestRuns.AsNoTracking()
            .Where(run => run.AssistantId == owner.AssistantId)
            .OrderBy(run => run.QueuedAt)
            .ToListAsync(CancellationToken);
        runs.Count.ShouldBe(2);
        runs[0].Id.ShouldBe(runId);
        runs.ShouldAllBe(run => run.Status == AssistantTestRunStatus.Completed);
        runs[1].Trigger.ShouldBe(AssistantTestRunTrigger.Manual);
    }

    [Fact]
    public async Task The_follow_up_run_records_the_trigger_it_was_asked_for_not_the_finished_runs()
    {
        var owner = await CreateOwnerWithAssistantAsync();
        var documentId = await UploadAndApproveAsync(owner, "退貨政策.md", ReturnClause);
        await CreateTestCaseAsync(owner, UnrelatedQuestion, "no-result", []);
        var runId = (await BodyJsonAsync(await RequestRunAsync(owner))).GetProperty("id").GetGuid();

        // A manual run is running when a document of its knowledge base is disabled (#125).
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var run = await dbContext.AssistantTestRuns.SingleAsync(candidate => candidate.Id == runId, CancellationToken);
            run.Start("test", AuthHostFixture.ChatModel, 0.3, DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var disabled = await owner.Spa.PostAsync(
            $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/documents/{documentId}/disable", owner.Token, new { reason = "條款待確認" });
        disabled.StatusCode.ShouldBe(HttpStatusCode.OK);

        await RunJobsAsync();

        await using var after = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var runs = await after.AssistantTestRuns.AsNoTracking()
            .Where(run => run.AssistantId == owner.AssistantId)
            .OrderBy(run => run.QueuedAt)
            .ToListAsync(CancellationToken);
        runs.Select(run => (run.Id == runId, run.Trigger, run.Status)).ShouldBe(
        [
            (true, AssistantTestRunTrigger.Manual, AssistantTestRunStatus.Completed),
            (false, AssistantTestRunTrigger.KnowledgeChanged, AssistantTestRunStatus.Completed),
        ]);
    }

    // --- Acceptance: a failing job fails its run, and only its run ------------------------------

    [Fact]
    public async Task A_failing_job_marks_its_run_failed_without_affecting_another_assistants_run()
    {
        var failing = await CreateOwnerWithAssistantAsync("會失敗的助理");
        await UploadAndApproveAsync(failing, "退貨政策.md", ReturnClause);
        await CreateTestCaseAsync(failing, ReturnClause + "#fail-midway", "company-data", []);

        var healthy = await CreateOwnerWithAssistantAsync("正常的助理");
        await CreateTestCaseAsync(healthy, UnrelatedQuestion, "no-result", []);

        var failingRunId = (await BodyJsonAsync(await RequestRunAsync(failing))).GetProperty("id").GetGuid();
        var healthyRunId = (await BodyJsonAsync(await RequestRunAsync(healthy))).GetProperty("id").GetGuid();

        await RunJobsAsync();

        var failedDetail = await BodyJsonAsync(
            await failing.Spa.GetAsync($"{BasePath(failing.AssistantId)}/{failingRunId}", failing.Token));
        failedDetail.GetProperty("run").GetProperty("status").GetString().ShouldBe("failed");
        failedDetail.GetProperty("run").GetProperty("completedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        failedDetail.GetProperty("results").GetArrayLength().ShouldBe(0);

        await using (var dbContext = _host.Postgres.CreateDbContext(failing.Organization.Id))
        {
            var job = (await RunJobsForAsync(dbContext, failingRunId)).ShouldHaveSingleItem();
            (job.Status, job.Attempts).ShouldBe((BackgroundJobStatus.Failed, 1), "a model failure is not retried");
        }

        var healthyDetail = await BodyJsonAsync(
            await healthy.Spa.GetAsync($"{BasePath(healthy.AssistantId)}/{healthyRunId}", healthy.Token));
        healthyDetail.GetProperty("run").GetProperty("status").GetString().ShouldBe("completed");
        healthyDetail.GetProperty("run").GetProperty("passedCount").GetInt32().ShouldBe(1);
    }

    // --- Validation, retention ------------------------------------------------------------------

    [Fact]
    public async Task An_assistant_without_test_cases_is_422_and_queues_nothing()
    {
        var owner = await CreateOwnerWithAssistantAsync();

        var response = await RequestRunAsync(owner);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("還沒有任何測試題");
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.AssistantTestRuns.CountAsync(run => run.AssistantId == owner.AssistantId, CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Only_the_newest_20_runs_are_kept()
    {
        var owner = await CreateOwnerWithAssistantAsync();
        await CreateTestCaseAsync(owner, UnrelatedQuestion, "no-result", []);

        Guid oldestId;
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            var old = Enumerable.Range(0, AssistantTestRun.KeptPerAssistant)
                .Select(i =>
                {
                    var run = AssistantTestRun.Queue(owner.Organization.Id, owner.AssistantId, AssistantTestRunTrigger.Manual, start.AddMinutes(i));
                    run.Start("test", AuthHostFixture.ChatModel, 0.3, start.AddMinutes(i));
                    run.Complete(1, 0, start.AddMinutes(i));
                    return run;
                })
                .ToList();
            oldestId = old[0].Id;
            dbContext.AssistantTestRuns.AddRange(old);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        (await RequestRunAsync(owner)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using var after = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var ids = await after.AssistantTestRuns.AsNoTracking()
            .Where(run => run.AssistantId == owner.AssistantId)
            .Select(run => run.Id)
            .ToListAsync(CancellationToken);
        ids.Count.ShouldBe(AssistantTestRun.KeptPerAssistant);
        ids.ShouldNotContain(oldestId);
    }

    // --- Permissions ----------------------------------------------------------------------------

    [Fact]
    public async Task Another_organizations_assistant_and_a_nonexistent_one_get_byte_identical_403s_on_every_endpoint()
    {
        var owner = await CreateOwnerWithAssistantAsync();
        await CreateTestCaseAsync(owner, UnrelatedQuestion, "no-result", []);
        var runId = (await BodyJsonAsync(await RequestRunAsync(owner))).GetProperty("id").GetGuid();

        var orgB = await _host.CreateOrganizationAsync("重跑商行 B");
        await _host.CreateAccountAsync(orgB, "admin", Password, AccountRole.SmbAdmin, "B 管理者", AccountPermission.ManageAssistants);
        var spaB = _host.CreateSpaClient();
        var tokenB = (await spaB.SignInAsync(orgB.Code, "admin", Password)).AccessToken;

        await AssertEveryEndpointIsTheSame403Async(spaB, tokenB, owner.AssistantId, runId);
    }

    [Fact]
    public async Task Someone_elses_assistant_in_the_same_organization_gets_the_byte_identical_403()
    {
        var owner = await CreateOwnerWithAssistantAsync();
        await CreateTestCaseAsync(owner, UnrelatedQuestion, "no-result", []);
        var runId = (await BodyJsonAsync(await RequestRunAsync(owner))).GetProperty("id").GetGuid();

        await _host.CreateAccountAsync(
            owner.Organization, "colleague", Password, AccountRole.SmbAdmin, "同事", AccountPermission.ManageAssistants);
        var colleague = _host.CreateSpaClient();
        var colleagueToken = (await colleague.SignInAsync(owner.Organization.Code, "colleague", Password)).AccessToken;

        await AssertEveryEndpointIsTheSame403Async(colleague, colleagueToken, owner.AssistantId, runId);
    }

    [Fact]
    public async Task A_run_of_another_assistant_is_the_same_403_as_a_missing_run()
    {
        var owner = await CreateOwnerWithAssistantAsync();
        await CreateTestCaseAsync(owner, UnrelatedQuestion, "no-result", []);
        var runId = (await BodyJsonAsync(await RequestRunAsync(owner))).GetProperty("id").GetGuid();
        var otherAssistantId = await AddAssistantAsync(owner, "第二個助理");

        var viaOtherAssistant = await owner.Spa.GetAsync($"{BasePath(otherAssistantId)}/{runId}", owner.Token);
        var missing = await owner.Spa.GetAsync($"{BasePath(owner.AssistantId)}/{Guid.NewGuid()}", owner.Token);

        viaOtherAssistant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(viaOtherAssistant, missing);
    }

    private static async Task AssertEveryEndpointIsTheSame403Async(SpaClient spa, string token, Guid assistantId, Guid runId)
    {
        var missingAssistant = Guid.NewGuid();

        var postForeign = await spa.PostAsync(BasePath(assistantId), token, new { });
        var postMissing = await spa.PostAsync(BasePath(missingAssistant), token, new { });
        postForeign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(postForeign, postMissing);

        var listForeign = await spa.GetAsync(BasePath(assistantId), token);
        var listMissing = await spa.GetAsync(BasePath(missingAssistant), token);
        listForeign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(listForeign, listMissing);

        var getForeign = await spa.GetAsync($"{BasePath(assistantId)}/{runId}", token);
        var getMissing = await spa.GetAsync($"{BasePath(missingAssistant)}/{Guid.NewGuid()}", token);
        getForeign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertIdenticalAsync(getForeign, getMissing);
    }

    // --- Fixtures -------------------------------------------------------------------------------

    private sealed record Owner(
        Organization Organization, SpaClient Spa, string Token, Guid AccountId, Guid KnowledgeBaseId, Guid AssistantId);

    private async Task<Owner> CreateOwnerWithAssistantAsync(string assistantName = "退貨小幫手")
    {
        var organization = await _host.CreateOrganizationAsync("重跑商行");
        var account = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "重跑商行管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources);
        var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, "admin", Password)).AccessToken;

        var created = await spa.PostAsync("/api/v1/knowledge-bases", token, new { name = "重跑知識庫", purpose = "" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var knowledgeBaseId = (await BodyJsonAsync(created)).GetProperty("id").GetGuid();

        var owner = new Owner(organization, spa, token, account.Id, knowledgeBaseId, Guid.Empty);
        return owner with { AssistantId = await AddAssistantAsync(owner, assistantName) };
    }

    private async Task<Guid> AddAssistantAsync(Owner owner, string name)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var assistant = Assistant.Create(
            owner.Organization.Id, owner.AccountId, name, "回答退換貨問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
            showCitations: true, keepConversations: true, now);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == owner.KnowledgeBaseId, CancellationToken);
        dbContext.Assistants.Add(assistant);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    private async Task<Guid> UploadAndApproveAsync(Owner owner, string fileName, string clause)
    {
        var content = Encoding.UTF8.GetBytes($"# {Path.GetFileNameWithoutExtension(fileName)}\n\n{clause}\n");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var uploaded = await owner.Spa.Http.SendAsync(request, CancellationToken);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(CancellationToken));
        var view = await BodyJsonAsync(uploaded);
        var (documentId, versionId) = (view.GetProperty("id").GetGuid(), view.GetProperty("latestVersionId").GetGuid());

        await RunJobsAsync();
        var approved = await owner.Spa.PostAsync(
            $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/versions/approve", owner.Token, new { versionIds = new[] { versionId.ToString() } });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(CancellationToken));
        return documentId;
    }

    private static async Task<Guid> CreateTestCaseAsync(
        Owner owner, string question, string expectedKind, IReadOnlyList<Guid> expectedDocumentIds)
    {
        var response = await owner.Spa.PostAsync($"/api/v1/assistants/{owner.AssistantId}/test-cases", owner.Token, new
        {
            question,
            category = expectedKind == "no-result" ? "should-refuse" : "common",
            expectedKind,
            expectedDocumentIds,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await BodyJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> RequestRunAsync(Owner owner) =>
        owner.Spa.PostAsync(BasePath(owner.AssistantId), owner.Token, new { });

    /// <summary>The <c>assistants.run-test-set</c> jobs whose payload names <paramref name="runId"/>
    /// (the payload is <c>jsonb</c>, so it is matched after reading).</summary>
    private static async Task<List<BackgroundJob>> RunJobsForAsync(SmartAgri.Infrastructure.AppDbContext dbContext, Guid runId) =>
        (await dbContext.BackgroundJobs.AsNoTracking()
            .Where(job => job.Kind == RunAssistantTestSetJob.Kind)
            .ToListAsync(CancellationToken))
        .Where(job => JsonDocument.Parse(job.Payload).RootElement.GetProperty("runId").GetGuid() == runId)
        .ToList();

    private Task<int> RunJobsAsync() =>
        _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

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
