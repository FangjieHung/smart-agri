using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Ai;
using SmartAgri.Api.Answers;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Operations;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Ai;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Answers;

/// <summary>
/// <c>AnswerOutcome</c> recording during real conversations and trial answers, and the two
/// analytics endpoints it feeds (M3.5 plan §3, §4, Slice 6; issue #128), against real PostgreSQL
/// with the <c>Fake</c> embedding and chat models.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class AnswerOutcomeAndAnalyticsEndpointsTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Answer-Outcome-Pass-1!";
    private const string ReturnClause = "收到商品後七天內可申請退貨，退貨運費由買家負擔。";
    private const string RelatedQuestion = "收到商品後幾天內可以申請退貨？退貨運費由誰負擔？";
    private const string UnrelatedQuestion = "zzzz qqqq xxxx";
    private const string RefusalMessage = "目前的資料中找不到這個問題的答案。";

    private readonly AuthHostFixture _host;

    public AnswerOutcomeAndAnalyticsEndpointsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Writing: real conversations and trial answers -----------------------------------------

    [Fact]
    public async Task A_completed_chat_run_records_one_chat_outcome_and_a_mid_stream_failure_records_none()
    {
        var org = await CreateOwnerAsync();
        var documentId = await UploadAndApproveAsync(org);
        var assistantId = await CreateAssistantAsync(org, keepConversations: true);

        var ok = await RunChatAsync(org, assistantId, RelatedQuestion);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(CancellationToken));

        var failed = await RunChatAsync(org, assistantId, $"{RelatedQuestion} {FakeChatDirectives.FailMidway}", runId: "run-2");
        failed.StatusCode.ShouldBe(HttpStatusCode.OK, "a mid-stream failure is still a 200 SSE stream ending in RUN_ERROR");

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var outcome = await dbContext.AnswerOutcomes.AsNoTracking().SingleAsync(CancellationToken);
        outcome.AssistantId.ShouldBe(assistantId);
        outcome.Channel.ShouldBe(AnswerOutcomeChannel.Chat);
        outcome.ReplyKind.ShouldBe(AnswerReplyKind.CompanyData);
        outcome.RejectionReason.ShouldBeNull();
        outcome.CitedDocumentIds.ShouldBe([documentId]);
    }

    [Fact]
    public async Task A_trial_answer_records_the_trial_channel_with_no_assistant_and_a_below_threshold_one_records_its_reason()
    {
        var org = await CreateOwnerAsync();
        await UploadAndApproveAsync(org);
        var draftId = await CreateDraftAsync(org, DraftPayload(org.KnowledgeBaseId));

        var related = await org.Spa.PostAsync(
            $"/api/v1/assistant-drafts/{draftId}/trial-answers", org.Token, new { question = RelatedQuestion });
        related.StatusCode.ShouldBe(HttpStatusCode.OK, await related.Content.ReadAsStringAsync(CancellationToken));

        var unrelated = await org.Spa.PostAsync(
            $"/api/v1/assistant-drafts/{draftId}/trial-answers", org.Token, new { question = UnrelatedQuestion });
        unrelated.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var outcomes = await dbContext.AnswerOutcomes.AsNoTracking().OrderBy(outcome => outcome.At).ToListAsync(CancellationToken);
        outcomes.Count.ShouldBe(2);
        outcomes.ShouldAllBe(outcome => outcome.Channel == AnswerOutcomeChannel.Trial && outcome.AssistantId == null);
        outcomes[0].ReplyKind.ShouldBe(AnswerReplyKind.CompanyData);
        outcomes[1].ReplyKind.ShouldBe(AnswerReplyKind.NoResult);
        outcomes[1].RejectionReason.ShouldBe(AnswerRejectionReason.BelowThreshold);
    }

    // --- GET assistants/{id}/analytics -----------------------------------------------------

    [Fact]
    public async Task Analytics_aggregate_exactly_the_rows_written_for_that_assistant()
    {
        var org = await CreateOwnerAsync();
        var documentId = await UploadAndApproveAsync(org);
        var assistantId = await CreateAssistantAsync(org, keepConversations: false);
        var otherAssistantId = await CreateAssistantAsync(org, keepConversations: false);
        var now = DateTimeOffset.UtcNow;

        await SeedOutcomeAsync(org, assistantId, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [documentId], now);
        await SeedOutcomeAsync(org, assistantId, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [documentId], now);
        await SeedOutcomeAsync(
            org, assistantId, AnswerOutcomeChannel.Chat, AnswerReplyKind.NoResult, AnswerRejectionReason.BelowThreshold, [], now);
        // A different assistant's row must not leak into this one's analytics.
        await SeedOutcomeAsync(org, otherAssistantId, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [documentId], now);
        // Outside the default 30-day window: must not be counted either.
        await SeedOutcomeAsync(
            org, assistantId, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [documentId], now.AddDays(-40));
        // #178: this assistant's database query answers are not answer-quality data — no number here changes.
        await SeedDatabaseQueryOutcomeAsync(org, assistantId, AnswerDatabaseQueryResult.Answered, now);
        await SeedDatabaseQueryOutcomeAsync(org, assistantId, AnswerDatabaseQueryResult.Failed, now);

        var response = await org.Spa.GetAsync($"/api/v1/assistants/{assistantId}/analytics", org.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "AssistantAnalyticsView");

        body.GetProperty("totalReplies").GetInt32().ShouldBe(3);
        var replyKinds = body.GetProperty("replyKinds").EnumerateArray()
            .ToDictionary(item => item.GetProperty("kind").GetString()!, item => item.GetProperty("count").GetInt32());
        replyKinds["company-data"].ShouldBe(2);
        replyKinds["no-result"].ShouldBe(1);
        replyKinds["general-knowledge"].ShouldBe(0);
        replyKinds.Keys.ShouldBe(["company-data", "general-knowledge", "no-result"], ignoreOrder: true);

        var rejectionReasons = body.GetProperty("rejectionReasons").EnumerateArray()
            .ToDictionary(item => item.GetProperty("reason").GetString()!, item => item.GetProperty("count").GetInt32());
        rejectionReasons["below-threshold"].ShouldBe(1);
        rejectionReasons["no-citation"].ShouldBe(0);

        var mostCited = body.GetProperty("mostCitedDocuments").EnumerateArray().ToList();
        mostCited.ShouldHaveSingleItem();
        mostCited[0].GetProperty("documentId").GetGuid().ShouldBe(documentId);
        mostCited[0].GetProperty("count").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Another_organizations_assistant_and_a_missing_one_and_a_non_owner_all_get_the_same_403()
    {
        var org = await CreateOwnerAsync();
        var assistantId = await CreateAssistantAsync(org, keepConversations: false);

        var otherOrg = await _host.CreateOrganizationAsync("別的組織");
        await _host.CreateAccountAsync(otherOrg, "admin", Password, AccountRole.SmbAdmin, "別的組織管理者", AccountPermission.ManageAssistants);
        var otherSpa = _host.CreateSpaClient();
        var otherToken = (await otherSpa.SignInAsync(otherOrg.Code, "admin", Password)).AccessToken;

        await _host.CreateAccountAsync(
            org.Organization, "colleague", Password, AccountRole.InternalEmployee, "非擁有者", AccountPermission.ManageAssistants);
        var colleagueSpa = _host.CreateSpaClient();
        var colleagueToken = (await colleagueSpa.SignInAsync(org.Organization.Code, "colleague", Password)).AccessToken;

        var foreign = await otherSpa.GetAsync($"/api/v1/assistants/{assistantId}/analytics", otherToken);
        var missing = await org.Spa.GetAsync($"/api/v1/assistants/{Guid.NewGuid()}/analytics", org.Token);
        var notOwner = await colleagueSpa.GetAsync($"/api/v1/assistants/{assistantId}/analytics", colleagueToken);

        foreach (var response in new[] { foreign, missing, notOwner })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var reference = await ResponseFingerprint.FromAsync(foreign);
        foreach (var response in new[] { missing, notOwner })
        {
            var fingerprint = await ResponseFingerprint.FromAsync(response);
            fingerprint.Status.ShouldBe(reference.Status);
            fingerprint.ContentType.ShouldBe(reference.ContentType);
            fingerprint.Body.ShouldBe(reference.Body, "not-found and forbidden must be byte-identical (M3.5 issue #128)");
            fingerprint.SetsCookie.ShouldBe(reference.SetsCookie);
        }
    }

    [Fact]
    public async Task An_invalid_date_range_is_422()
    {
        var org = await CreateOwnerAsync();
        var assistantId = await CreateAssistantAsync(org, keepConversations: false);

        var response = await org.Spa.GetAsync(
            $"/api/v1/assistants/{assistantId}/analytics?from=2026-02-01&to=2026-01-01", org.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe(AssistantAnalyticsEndpoints.InvalidDateRangeReason);
    }

    // --- GET operations/summary -------------------------------------------------------------

    [Fact]
    public async Task Operations_summary_rolls_up_every_assistant_and_the_knowledge_backlog()
    {
        var org = await CreateOwnerAsync();
        var documentId = await UploadAndApproveAsync(org);
        var assistantId = await CreateAssistantAsync(org, keepConversations: false);
        var now = DateTimeOffset.UtcNow;

        await SeedOutcomeAsync(org, assistantId, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [documentId], now);
        await SeedOutcomeAsync(org, assistantId, AnswerOutcomeChannel.Chat, AnswerReplyKind.CompanyData, null, [documentId], now);
        await SeedOutcomeAsync(
            org, assistantId, AnswerOutcomeChannel.Chat, AnswerReplyKind.NoResult, AnswerRejectionReason.NoCitation, [], now);
        await SeedOutcomeAsync(
            org, assistantId, AnswerOutcomeChannel.Chat, AnswerReplyKind.NoResult, AnswerRejectionReason.BelowThreshold, [], now);
        // #178: database query answers, counted on their own — the assistant's replies and rates
        // below are exactly what they were without them.
        foreach (var result in new[]
        {
            AnswerDatabaseQueryResult.Answered, AnswerDatabaseQueryResult.Answered, AnswerDatabaseQueryResult.Answered,
            AnswerDatabaseQueryResult.NotPermitted, AnswerDatabaseQueryResult.InsufficientRecords, AnswerDatabaseQueryResult.InsufficientRecords,
            AnswerDatabaseQueryResult.Failed, AnswerDatabaseQueryResult.Failed,
        })
        {
            await SeedDatabaseQueryOutcomeAsync(org, assistantId, result, now);
        }

        // Outside the range: not counted.
        await SeedDatabaseQueryOutcomeAsync(org, assistantId, AnswerDatabaseQueryResult.Failed, now.AddDays(-40));

        await SeedFailedKnowledgeVersionAsync(org);
        await SeedOverduePendingReviewVersionAsync(org, now);

        var response = await org.Spa.GetAsync("/api/v1/operations/summary", org.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);
        OpenApiContract.AssertKeysMatchSchema(body, "OperationsSummaryView");

        var assistants = body.GetProperty("assistants").EnumerateArray()
            .Where(view => view.GetProperty("assistantId").GetGuid() == assistantId)
            .ToList();
        var assistant = assistants.ShouldHaveSingleItem();
        assistant.GetProperty("totalReplies").GetInt32().ShouldBe(4);
        // no-result: 2 of 4 = 0.5; rejected-citation (no-citation only, not below-threshold): 1 of 4 = 0.25.
        assistant.GetProperty("noResultRate").GetDouble().ShouldBe(0.5);
        assistant.GetProperty("rejectedCitationRate").GetDouble().ShouldBe(0.25);

        var mostCited = body.GetProperty("mostCitedDocuments").EnumerateArray().ToList();
        mostCited.ShouldHaveSingleItem();
        mostCited[0].GetProperty("count").GetInt32().ShouldBe(2);

        var knowledge = body.GetProperty("knowledge");
        knowledge.GetProperty("processingFailedCount").GetInt32().ShouldBe(1);
        knowledge.GetProperty("overduePendingReviewCount").GetInt32().ShouldBe(1);

        var queries = body.GetProperty("databaseQueries");
        (queries.GetProperty("totalCount").GetInt32(), queries.GetProperty("answeredCount").GetInt32(),
                queries.GetProperty("notPermittedCount").GetInt32(), queries.GetProperty("insufficientRecordsCount").GetInt32(),
                queries.GetProperty("failedCount").GetInt32())
            .ShouldBe((8, 3, 1, 2, 2));
        queries.GetProperty("failureRate").GetDouble().ShouldBe(0.25);

        var issues = body.GetProperty("issues");
        issues.GetProperty("openCount").GetInt32().ShouldBe(0, "this organization has no issue");
        issues.GetProperty("averageResolutionHours").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Without_manage_assistants_the_summary_is_403()
    {
        var org = await CreateOwnerAsync();
        await _host.CreateAccountAsync(
            org.Organization, "member", Password, AccountRole.InternalEmployee, "一般成員", AccountPermission.UseSharedAssistants);
        var memberSpa = _host.CreateSpaClient();
        var memberToken = (await memberSpa.SignInAsync(org.Organization.Code, "member", Password)).AccessToken;

        var response = await memberSpa.GetAsync("/api/v1/operations/summary", memberToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(response)).GetProperty("reason").GetString().ShouldBe("operations-summary");
    }

    // --- Fixtures -------------------------------------------------------------------------

    private sealed record Owner(Organization Organization, SpaClient Spa, string Token, Guid AccountId, Guid KnowledgeBaseId);

    private async Task<Owner> CreateOwnerAsync()
    {
        var organization = await _host.CreateOrganizationAsync("追蹤商行");
        await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "追蹤商行管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources);
        var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, "admin", Password)).AccessToken;
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var accountId = await dbContext.Accounts.Select(account => account.Id).SingleAsync(CancellationToken);

        var created = await spa.PostAsync("/api/v1/knowledge-bases", token, new { name = "追蹤知識庫", purpose = "" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var knowledgeBaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync(CancellationToken)).RootElement.GetProperty("id").GetGuid();

        return new Owner(organization, spa, token, accountId, knowledgeBaseId);
    }

    private async Task<Guid> UploadAndApproveAsync(Owner owner)
    {
        var content = Encoding.UTF8.GetBytes($"# 退貨政策\n\n{ReturnClause}\n");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "退貨政策.md");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var uploaded = await owner.Spa.Http.SendAsync(request, CancellationToken);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(CancellationToken));
        var view = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(CancellationToken)).RootElement;
        var (documentId, versionId) = (view.GetProperty("id").GetGuid(), view.GetProperty("latestVersionId").GetGuid());

        await _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        var approved = await owner.Spa.PostAsync(
            $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/versions/approve", owner.Token, new { versionIds = new[] { versionId.ToString() } });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(CancellationToken));
        return documentId;
    }

    private async Task<Guid> CreateAssistantAsync(Owner owner, bool keepConversations)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var assistant = Assistant.Create(
            owner.Organization.Id, owner.AccountId, "追蹤小幫手", "回答退換貨問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            RefusalMessage, showCitations: true, keepConversations: keepConversations, now);
        dbContext.Assistants.Add(assistant);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == owner.KnowledgeBaseId, CancellationToken);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }

    private async Task<Guid> CreateDraftAsync(Owner owner, string payloadJson)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var draft = new AssistantDraft(owner.Organization.Id, owner.AccountId, payloadJson, schemaVersion: 1, DateTimeOffset.UtcNow);
        dbContext.AssistantDrafts.Add(draft);
        await dbContext.SaveChangesAsync(CancellationToken);
        return draft.Id;
    }

    private static string DraftPayload(Guid knowledgeBaseId) =>
        $$"""
        {
          "name": "追蹤小幫手",
          "purpose": "回答退換貨問題",
          "tone": "friendly",
          "roleInstructions": "",
          "sources": [ { "id": "{{knowledgeBaseId}}", "type": "knowledge-base" } ],
          "rules": {
            "knowledgeScope": "company-data-only",
            "refusalMessage": "目前的資料中找不到這個問題的答案。",
            "showCitations": true,
            "keepOwnConversations": true
          }
        }
        """;

    private async Task<HttpResponseMessage> RunChatAsync(Owner owner, Guid assistantId, string question, string runId = "run-1")
    {
        var input = new
        {
            threadId = string.Empty,
            runId,
            state = new { },
            messages = new[] { new { id = "question-" + runId, role = "user", content = question } },
            tools = Array.Empty<object>(),
            context = Array.Empty<object>(),
            forwardedProps = new { },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/assistants/{assistantId}/chat/runs")
        {
            Content = JsonContent.Create(input),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await owner.Spa.Http.SendAsync(request, CancellationToken);
        await response.Content.LoadIntoBufferAsync(CancellationToken);
        return response;
    }

    private async Task SeedOutcomeAsync(
        Owner owner,
        Guid? assistantId,
        AnswerOutcomeChannel channel,
        AnswerReplyKind replyKind,
        AnswerRejectionReason? rejectionReason,
        IReadOnlyCollection<Guid> citedDocumentIds,
        DateTimeOffset at)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        dbContext.AnswerOutcomes.Add(
            AnswerOutcome.Record(owner.Organization.Id, assistantId, channel, replyKind, rejectionReason, citedDocumentIds, at));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task SeedDatabaseQueryOutcomeAsync(Owner owner, Guid assistantId, AnswerDatabaseQueryResult result, DateTimeOffset at)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        dbContext.AnswerOutcomes.Add(AnswerOutcome.RecordDatabaseQuery(owner.Organization.Id, assistantId, result, at));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task SeedFailedKnowledgeVersionAsync(Owner owner)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == owner.KnowledgeBaseId, CancellationToken);
        var document = KnowledgeDocument.CreateUploaded(knowledgeBase, "壞掉的檔案.md", now);
        dbContext.KnowledgeDocuments.Add(document);
        var version = KnowledgeDocumentVersion.Create(
            document, 1, "壞掉的檔案.md", "text/markdown", 10, new string('a', 64), owner.AccountId, null, now);
        dbContext.KnowledgeDocumentVersions.Add(version);
        version.StartProcessing(now);
        version.MarkFailed("測試用的處理失敗。", now);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    /// <summary>A version still pending review, uploaded well over the overdue window ago.</summary>
    private async Task SeedOverduePendingReviewVersionAsync(Owner owner, DateTimeOffset now)
    {
        var uploadedAt = now.AddDays(-(OperationsSummaryEndpoints.OverduePendingReviewDays + 1));
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == owner.KnowledgeBaseId, CancellationToken);
        var document = KnowledgeDocument.CreateUploaded(knowledgeBase, "遲遲沒確認.md", uploadedAt);
        dbContext.KnowledgeDocuments.Add(document);
        var version = KnowledgeDocumentVersion.Create(
            document, 1, "遲遲沒確認.md", "text/markdown", 10, new string('b', 64), owner.AccountId, null, uploadedAt);
        dbContext.KnowledgeDocumentVersions.Add(version);
        version.StartProcessing(uploadedAt);
        version.CompleteProcessing(KnowledgeDocumentStatus.Ready, null, uploadedAt);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
