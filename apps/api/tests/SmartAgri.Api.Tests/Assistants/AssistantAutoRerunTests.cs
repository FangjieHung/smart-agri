using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Assistants;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// Automatic reruns and the acceptance status (M3.5 plan §3, §5 Slice 3, issue #125) against real
/// PostgreSQL with the <c>Fake</c> models: which changes ask for which assistants' reruns, with
/// which trigger and when, and how <c>acceptanceStatus</c> reads on the list and settings.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class AssistantAutoRerunTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "AutoRerun-Endpoint-Pass-1!";
    private const string ReturnClause = "收到商品後七天內可申請退貨，退貨運費由買家負擔。";
    private const string UnrelatedQuestion = "zzzz qqqq xxxx";

    private readonly AuthHostFixture _host;

    public AssistantAutoRerunTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Version confirmed effective ------------------------------------------------------------

    [Fact]
    public async Task Confirming_a_version_queues_one_knowledge_changed_run_per_connected_assistant_with_test_cases_only()
    {
        var owner = await CreateOwnerAsync();
        var tested = await AddAssistantAsync(owner, "有題組 1", owner.KnowledgeBaseId);
        var alsoTested = await AddAssistantAsync(owner, "有題組 2", owner.KnowledgeBaseId);
        var untested = await AddAssistantAsync(owner, "沒有題組", owner.KnowledgeBaseId);
        var otherKnowledgeBase = await CreateKnowledgeBaseAsync(owner, "別的知識庫");
        var notConnected = await AddAssistantAsync(owner, "沒有連接", otherKnowledgeBase);
        foreach (var assistantId in new[] { tested, alsoTested, notConnected })
        {
            await CreateTestCaseAsync(owner, assistantId);
        }

        var versionId = await UploadAsync(owner, "退貨政策.md");
        (await ApproveAsync(owner, versionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var runs = await dbContext.AssistantTestRuns.AsNoTracking().ToListAsync(CancellationToken);
        runs.Select(run => run.AssistantId).Order().ShouldBe(new[] { tested, alsoTested }.Order());
        runs.ShouldAllBe(run => run.Trigger == AssistantTestRunTrigger.KnowledgeChanged && run.Status == AssistantTestRunStatus.Queued);
        (await dbContext.BackgroundJobs.CountAsync(job => job.Kind == RunAssistantTestSetJob.Kind, CancellationToken)).ShouldBe(2);
        (await dbContext.BackgroundJobs.CountAsync(job => job.Kind == RequestAssistantTestRunsJob.Kind, CancellationToken))
            .ShouldBe(0, "an approval effective now asks for the reruns at once");
        untested.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task A_future_effective_date_schedules_the_reruns_for_that_date()
    {
        var owner = await CreateOwnerAsync();
        var assistantId = await AddAssistantAsync(owner, "未來生效", owner.KnowledgeBaseId);
        await CreateTestCaseAsync(owner, assistantId);
        var versionId = await UploadAsync(owner, "新版退貨政策.md");

        var now = _host.Clock.GetUtcNow();
        var effectiveFrom = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.FromHours(8)).AddDays(2);
        var approved = await ApproveAsync(owner, versionId, effectiveFrom.ToString("yyyy-MM-ddTHH:mm:sszzz"));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(CancellationToken));

        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            (await dbContext.AssistantTestRuns.CountAsync(CancellationToken)).ShouldBe(0, "nothing changes before the version takes effect");
            var scheduled = (await dbContext.BackgroundJobs.AsNoTracking()
                    .Where(job => job.Kind == RequestAssistantTestRunsJob.Kind)
                    .ToListAsync(CancellationToken))
                .ShouldHaveSingleItem();
            scheduled.RunAfter.ShouldBe(effectiveFrom);
            JsonDocument.Parse(scheduled.Payload).RootElement.GetProperty("knowledgeBaseId").GetGuid().ShouldBe(owner.KnowledgeBaseId);
        }

        await RunJobsAsync();
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            (await dbContext.AssistantTestRuns.CountAsync(CancellationToken)).ShouldBe(0, "the scheduled job waits for its RunAfter");

            // The effective date has come.
            await dbContext.BackgroundJobs
                .Where(job => job.Kind == RequestAssistantTestRunsJob.Kind)
                .ExecuteUpdateAsync(set => set.SetProperty(job => job.RunAfter, DateTimeOffset.UtcNow.AddSeconds(-1)), CancellationToken);
        }

        await RunJobsAsync();
        await using var after = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var run = (await after.AssistantTestRuns.AsNoTracking().ToListAsync(CancellationToken)).ShouldHaveSingleItem();
        (run.AssistantId, run.Trigger, run.Status)
            .ShouldBe((assistantId, AssistantTestRunTrigger.KnowledgeChanged, AssistantTestRunStatus.Completed));
    }

    // --- Document disabled / restored -----------------------------------------------------------

    [Fact]
    public async Task Disabling_and_restoring_a_document_each_queue_a_knowledge_changed_run()
    {
        var owner = await CreateOwnerAsync();
        var assistantId = await AddAssistantAsync(owner, "停用與恢復", owner.KnowledgeBaseId);
        var documentId = await UploadAndApproveAsync(owner, "退貨政策.md");
        await CreateTestCaseAsync(owner, assistantId);

        (await owner.Spa.PostAsync(DocumentPath(owner, documentId) + "/disable", owner.Token, new { reason = "條款待確認" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TriggersAsync(owner, assistantId)).ShouldBe([AssistantTestRunTrigger.KnowledgeChanged]);

        await RunJobsAsync();
        (await AcceptanceStatusAsync(owner, assistantId)).ShouldBe("passed");

        (await owner.Spa.PostAsync(DocumentPath(owner, documentId) + "/enable", owner.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TriggersAsync(owner, assistantId)).ShouldBe([AssistantTestRunTrigger.KnowledgeChanged, AssistantTestRunTrigger.KnowledgeChanged]);
        (await AcceptanceStatusAsync(owner, assistantId)).ShouldBe("outdated");
    }

    [Fact]
    public async Task A_change_while_a_run_is_queued_only_sets_rerun_requested_on_it()
    {
        var owner = await CreateOwnerAsync();
        var assistantId = await AddAssistantAsync(owner, "合併重跑", owner.KnowledgeBaseId);
        var documentId = await UploadAndApproveAsync(owner, "退貨政策.md");
        await CreateTestCaseAsync(owner, assistantId);

        (await owner.Spa.PostAsync(DocumentPath(owner, documentId) + "/disable", owner.Token, new { reason = "條款待確認" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Spa.PostAsync(DocumentPath(owner, documentId) + "/enable", owner.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Spa.PatchAsync(SettingsPath(assistantId), owner.Token, new { tone = "concise" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var run = (await dbContext.AssistantTestRuns.AsNoTracking().ToListAsync(CancellationToken))
            .ShouldHaveSingleItem("an event while a run is queued never adds a second row");
        (run.Status, run.Trigger, run.RerunRequested, run.RerunTrigger).ShouldBe(
            (AssistantTestRunStatus.Queued, AssistantTestRunTrigger.KnowledgeChanged, true,
                (AssistantTestRunTrigger?)AssistantTestRunTrigger.AssistantChanged));
        (await dbContext.BackgroundJobs.CountAsync(job => job.Kind == RunAssistantTestSetJob.Kind, CancellationToken)).ShouldBe(1);
    }

    // --- Assistant settings / sources -----------------------------------------------------------

    [Fact]
    public async Task Changing_answering_settings_or_sources_queues_an_assistant_changed_run_and_other_saves_do_not()
    {
        var owner = await CreateOwnerAsync();
        var assistantId = await AddAssistantAsync(owner, "設定變更", owner.KnowledgeBaseId);
        var untested = await AddAssistantAsync(owner, "沒有題組", owner.KnowledgeBaseId);
        await CreateTestCaseAsync(owner, assistantId);
        var otherKnowledgeBase = await CreateKnowledgeBaseAsync(owner, "第二個知識庫");

        // A no-op save, and a change that does not affect answers (showCitations), ask for nothing.
        (await owner.Spa.PatchAsync(SettingsPath(assistantId), owner.Token, new { name = "設定變更" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Spa.PatchAsync(SettingsPath(assistantId), owner.Token, new { rules = new { showCitations = false } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TriggersAsync(owner, assistantId)).ShouldBeEmpty();

        var patched = await owner.Spa.PatchAsync(SettingsPath(assistantId), owner.Token, new { tone = "professional" });
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TriggersAsync(owner, assistantId)).ShouldBe([AssistantTestRunTrigger.AssistantChanged]);
        await RunJobsAsync();

        (await owner.Spa.PutAsync($"/api/v1/assistants/{assistantId}/sources/knowledge-base/{otherKnowledgeBase}", owner.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TriggersAsync(owner, assistantId)).ShouldBe([AssistantTestRunTrigger.AssistantChanged, AssistantTestRunTrigger.AssistantChanged]);
        await RunJobsAsync();

        (await owner.Spa.PutAsync($"/api/v1/assistants/{assistantId}/sources/knowledge-base/{otherKnowledgeBase}", owner.Token, new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TriggersAsync(owner, assistantId)).Count.ShouldBe(2, "an already-connected source changes nothing");

        (await owner.Spa.DeleteAsync($"/api/v1/assistants/{assistantId}/sources/knowledge-base/{otherKnowledgeBase}", owner.Token))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TriggersAsync(owner, assistantId)).ShouldBe(
            [AssistantTestRunTrigger.AssistantChanged, AssistantTestRunTrigger.AssistantChanged, AssistantTestRunTrigger.AssistantChanged]);

        (await owner.Spa.PatchAsync(SettingsPath(untested), owner.Token, new { tone = "professional" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TriggersAsync(owner, untested)).ShouldBeEmpty("an assistant without test cases has nothing to rerun");
    }

    // --- acceptanceStatus -----------------------------------------------------------------------

    [Fact]
    public async Task Acceptance_status_is_derived_on_the_list_and_the_settings()
    {
        var owner = await CreateOwnerAsync();
        var noTestCases = await AddAssistantAsync(owner, "沒有題組", owner.KnowledgeBaseId);
        var neverRun = await AddAssistantAsync(owner, "從未跑過", owner.KnowledgeBaseId);
        var passed = await AddAssistantAsync(owner, "通過", owner.KnowledgeBaseId);
        var failed = await AddAssistantAsync(owner, "未通過", owner.KnowledgeBaseId);
        var outdated = await AddAssistantAsync(owner, "已過期", owner.KnowledgeBaseId);
        var manualRerun = await AddAssistantAsync(owner, "手動重跑中", owner.KnowledgeBaseId);
        foreach (var assistantId in new[] { neverRun, passed, failed, outdated, manualRerun })
        {
            await CreateTestCaseAsync(owner, assistantId);
        }

        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var start = DateTimeOffset.UtcNow.AddHours(-1);
            dbContext.AssistantTestRuns.AddRange(
                Completed(owner, passed, start, failedCount: 0),
                Completed(owner, failed, start, failedCount: 1),
                Completed(owner, outdated, start, failedCount: 0),
                AssistantTestRun.Queue(owner.Organization.Id, outdated, AssistantTestRunTrigger.KnowledgeChanged, start.AddMinutes(5)),
                Completed(owner, manualRerun, start, failedCount: 1),
                AssistantTestRun.Queue(owner.Organization.Id, manualRerun, AssistantTestRunTrigger.Manual, start.AddMinutes(5)));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var expected = new Dictionary<Guid, string>
        {
            [noTestCases] = "not-accepted",
            [neverRun] = "not-accepted",
            [passed] = "passed",
            [failed] = "failed",
            [outdated] = "outdated",
            [manualRerun] = "failed",
        };

        var list = await BodyJsonAsync(await owner.Spa.GetAsync("/api/v1/assistants", owner.Token));
        var listed = list.EnumerateArray().ToList();
        listed.Count.ShouldBe(expected.Count);
        foreach (var item in listed)
        {
            OpenApiContract.AssertKeysMatchSchema(item, "AssistantConfigurationView");
            item.GetProperty("acceptanceStatus").GetString().ShouldBe(expected[item.GetProperty("id").GetGuid()]);
        }

        foreach (var (assistantId, status) in expected)
        {
            var settings = await BodyJsonAsync(await owner.Spa.GetAsync(SettingsPath(assistantId), owner.Token));
            OpenApiContract.AssertKeysMatchSchema(settings, "AssistantSettingsView");
            OpenApiContract.AssertKeysMatchSchema(settings.GetProperty("configuration"), "AssistantConfigurationView");
            settings.GetProperty("configuration").GetProperty("acceptanceStatus").GetString().ShouldBe(status);
        }
    }

    [Fact]
    public async Task Acceptance_statuses_of_any_number_of_assistants_take_two_queries()
    {
        var owner = await CreateOwnerAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var assistantId = await AddAssistantAsync(owner, $"助理 {i}", owner.KnowledgeBaseId);
            await CreateTestCaseAsync(owner, assistantId);
            ids.Add(assistantId);
        }

        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_host.Postgres.ConnectionString)
            .AddInterceptors(counter)
            .Options;
        await using var dbContext = new AppDbContext(options, new FixedOrganizationContext(owner.Organization.Id));

        var one = await AssistantEndpoints.AcceptanceStatusesAsync(dbContext, ids.Take(1).ToList(), CancellationToken);
        counter.Count.ShouldBe(2);
        counter.Count = 0;
        var all = await AssistantEndpoints.AcceptanceStatusesAsync(dbContext, ids, CancellationToken);
        counter.Count.ShouldBe(2, "no query per assistant");
        (one.Count, all.Count).ShouldBe((1, 4));
        all.Values.ShouldAllBe(status => status == AssistantAcceptanceStatus.NotAccepted);
    }

    // --- Fixtures -------------------------------------------------------------------------------

    private sealed record Owner(Organization Organization, SpaClient Spa, string Token, Guid AccountId, Guid KnowledgeBaseId);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static AssistantTestRun Completed(Owner owner, Guid assistantId, DateTimeOffset at, int failedCount)
    {
        var run = AssistantTestRun.Queue(owner.Organization.Id, assistantId, AssistantTestRunTrigger.Manual, at);
        run.Start("test", AuthHostFixture.ChatModel, 0.3, at);
        run.Complete(1 - Math.Min(failedCount, 1), failedCount, at.AddMinutes(1));
        return run;
    }

    private async Task<Owner> CreateOwnerAsync()
    {
        var organization = await _host.CreateOrganizationAsync("自動重跑商行");
        var account = await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "自動重跑管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources);
        var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, "admin", Password)).AccessToken;
        var owner = new Owner(organization, spa, token, account.Id, Guid.Empty);
        return owner with { KnowledgeBaseId = await CreateKnowledgeBaseAsync(owner, "自動重跑知識庫") };
    }

    private static async Task<Guid> CreateKnowledgeBaseAsync(Owner owner, string name)
    {
        var created = await owner.Spa.PostAsync("/api/v1/knowledge-bases", owner.Token, new { name, purpose = "" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyJsonAsync(created)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> AddAssistantAsync(Owner owner, string name, Guid knowledgeBaseId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var assistant = Assistant.Create(
            owner.Organization.Id, owner.AccountId, name, "回答退換貨問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
            showCitations: true, keepConversations: true, now);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == knowledgeBaseId, CancellationToken);
        dbContext.Assistants.Add(assistant);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    /// <summary>Uploads a document and processes it; returns its (pending review) version id.</summary>
    private async Task<Guid> UploadAsync(Owner owner, string fileName)
    {
        var content = Encoding.UTF8.GetBytes($"# {Path.GetFileNameWithoutExtension(fileName)}\n\n{ReturnClause}\n");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var uploaded = await owner.Spa.Http.SendAsync(request, CancellationToken);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(CancellationToken));
        var versionId = (await BodyJsonAsync(uploaded)).GetProperty("latestVersionId").GetGuid();
        await RunJobsAsync();
        return versionId;
    }

    /// <summary>Uploads and approves a document (before any test case exists, so no rerun);
    /// returns the document id.</summary>
    private async Task<Guid> UploadAndApproveAsync(Owner owner, string fileName)
    {
        var versionId = await UploadAsync(owner, fileName);
        (await ApproveAsync(owner, versionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeDocumentVersions
            .Where(version => version.Id == versionId)
            .Select(version => version.DocumentId)
            .SingleAsync(CancellationToken);
    }

    private static Task<HttpResponseMessage> ApproveAsync(Owner owner, Guid versionId, string? effectiveFrom = null) =>
        owner.Spa.PostAsync(
            $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/versions/approve",
            owner.Token,
            new { versionIds = new[] { versionId.ToString() }, effectiveFrom });

    private static async Task CreateTestCaseAsync(Owner owner, Guid assistantId)
    {
        var response = await owner.Spa.PostAsync($"/api/v1/assistants/{assistantId}/test-cases", owner.Token, new
        {
            question = UnrelatedQuestion,
            category = "should-refuse",
            expectedKind = "no-result",
            expectedDocumentIds = Array.Empty<Guid>(),
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    /// <summary>The assistant's runs' triggers, oldest first.</summary>
    private async Task<List<AssistantTestRunTrigger>> TriggersAsync(Owner owner, Guid assistantId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.AssistantTestRuns.AsNoTracking()
            .Where(run => run.AssistantId == assistantId)
            .OrderBy(run => run.QueuedAt)
            .Select(run => run.Trigger)
            .ToListAsync(CancellationToken);
    }

    private static async Task<string?> AcceptanceStatusAsync(Owner owner, Guid assistantId)
    {
        var settings = await BodyJsonAsync(await owner.Spa.GetAsync(SettingsPath(assistantId), owner.Token));
        return settings.GetProperty("configuration").GetProperty("acceptanceStatus").GetString();
    }

    private static string SettingsPath(Guid assistantId) => $"/api/v1/assistants/{assistantId}/settings";

    private static string DocumentPath(Owner owner, Guid documentId) =>
        $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/documents/{documentId}";

    private Task<int> RunJobsAsync() =>
        _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
