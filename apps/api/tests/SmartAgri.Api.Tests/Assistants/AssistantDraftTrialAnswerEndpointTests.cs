using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Ai;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// <c>POST /api/v1/assistant-drafts/{id}/trial-answers</c> against real PostgreSQL, with the
/// <c>Fake</c> embedding and chat models (M3 plan Slice 8; ticket #78): a document uploaded,
/// processed and approved through the real endpoints, a draft naming its knowledge base, and one
/// question above and one below the threshold. The other endpoints' <c>403 assistant-draft</c>
/// (someone else's draft id, or one that does not exist) is covered by
/// <see cref="AssistantDraftEndpointsTests"/>'s shared <c>EndpointsById</c> table, which this
/// endpoint was added to.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class AssistantDraftTrialAnswerEndpointTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Trial-Answer-Pass-1!";
    private const string ReturnClause = "收到商品後七天內可申請退貨，退貨運費由買家負擔。";
    private const string RefusalMessage = "目前的資料中找不到這個問題的答案。";

    private readonly AuthHostFixture _host;

    public AssistantDraftTrialAnswerEndpointTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_related_question_is_company_data_and_an_unrelated_one_is_no_result_calling_the_model_only_once()
    {
        var owner = await CreateOwnerAsync();
        var documentId = await UploadAndApproveAsync(owner);
        var draftId = await CreateDraftAsync(owner, DraftPayload(owner.KnowledgeBaseId));

        var answered = await TrialAnswerAsync(owner, draftId, "收到商品後幾天內可以申請退貨？退貨運費由誰負擔？");
        answered.StatusCode.ShouldBe(HttpStatusCode.OK, await answered.Content.ReadAsStringAsync(CancellationToken));
        var answeredBody = await BodyJsonAsync(answered);
        OpenApiContract.AssertKeysMatchSchema(answeredBody, "TrialAnswerResponse");

        answeredBody.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("company-data");
        answeredBody.GetProperty("reply").GetProperty("text").GetString().ShouldNotBeNull().ShouldContain("[1]");
        var citations = answeredBody.GetProperty("reply").GetProperty("citations");
        citations.GetArrayLength().ShouldBe(1);
        var citation = citations[0];
        citation.GetProperty("documentId").GetGuid().ShouldBe(documentId);
        citation.GetProperty("knowledgeBaseId").GetGuid().ShouldBe(owner.KnowledgeBaseId);
        citation.GetProperty("knowledgeBaseName").GetString().ShouldBe("試答知識庫");
        var passages = answeredBody.GetProperty("passages");
        passages.GetArrayLength().ShouldBeGreaterThan(0);

        // Below the threshold: no model call at all for this question.
        var refused = await TrialAnswerAsync(owner, draftId, "zzzz qqqq xxxx");
        refused.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refusedBody = await BodyJsonAsync(refused);
        refusedBody.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("no-result");
        refusedBody.GetProperty("reply").GetProperty("text").GetString().ShouldBe(RefusalMessage);
        refusedBody.GetProperty("reply").GetProperty("nextSteps").GetArrayLength().ShouldBeGreaterThan(0);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var calls = await dbContext.ModelInvocations.AsNoTracking()
            .Where(invocation => invocation.Purpose == ModelInvocationPurpose.TrialAnswer)
            .ToListAsync(CancellationToken);
        var call = calls.ShouldHaveSingleItem("only the answer above the threshold called the chat model");
        (call.AccountId, call.AssistantId).ShouldBe(((Guid?)owner.AccountId, (Guid?)null));
    }

    [Fact]
    public async Task The_passages_only_come_from_a_knowledge_base_the_owner_can_still_connect()
    {
        var owner = await CreateOwnerAsync();
        await UploadAndApproveAsync(owner);

        var otherOrg = await _host.CreateOrganizationAsync("別的組織");
        await _host.CreateAccountAsync(otherOrg, "admin", Password, AccountRole.SmbAdmin, "別的組織管理者", AccountPermission.ManageDataSources);
        var foreignKnowledgeBaseId = await CreateForeignKnowledgeBaseWithMatchingContentAsync(otherOrg);

        // The draft names both its own (connectable) knowledge base and another organization's
        // (never connectable, whatever the draft says).
        var draftId = await CreateDraftAsync(owner, DraftPayload(owner.KnowledgeBaseId, foreignKnowledgeBaseId));

        var response = await TrialAnswerAsync(owner, draftId, "收到商品後幾天內可以申請退貨？退貨運費由誰負擔？");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        var body = await BodyJsonAsync(response);

        var passageKnowledgeBaseIds = body.GetProperty("passages").EnumerateArray()
            .Select(passage => passage.GetProperty("knowledgeBaseId").GetGuid())
            .ToList();
        passageKnowledgeBaseIds.ShouldNotBeEmpty();
        passageKnowledgeBaseIds.ShouldAllBe(id => id == owner.KnowledgeBaseId);
        passageKnowledgeBaseIds.ShouldNotContain(foreignKnowledgeBaseId);
    }

    [Fact]
    public async Task A_trial_answer_tolerates_an_incomplete_draft_and_uses_defaults()
    {
        var owner = await CreateOwnerAsync();
        await UploadAndApproveAsync(owner);
        // Only the knowledge base is filled in; name, purpose, tone, rules are all still blank.
        var draftId = await CreateDraftAsync(owner, $$"""{"sources":[{"id":"{{owner.KnowledgeBaseId}}","type":"knowledge-base"}]}""");

        var response = await TrialAnswerAsync(owner, draftId, "收到商品後幾天內可以申請退貨？退貨運費由誰負擔？");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        (await BodyJsonAsync(response)).GetProperty("reply").GetProperty("kind").GetString().ShouldBe("company-data");
    }

    [Theory]
    [InlineData(null, AssistantDraftTrialAnswerRules.QuestionRequiredMessage)]
    [InlineData("   ", AssistantDraftTrialAnswerRules.QuestionRequiredMessage)]
    public async Task A_blank_question_is_422(string? question, string expectedMessage)
    {
        var owner = await CreateOwnerAsync();
        var draftId = await CreateDraftAsync(owner, DraftPayload(owner.KnowledgeBaseId));

        var response = await TrialAnswerAsync(owner, draftId, question);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(response)).GetProperty("errors").GetProperty("question")[0].GetString().ShouldBe(expectedMessage);
    }

    [Fact]
    public async Task A_question_over_2000_characters_is_422()
    {
        var owner = await CreateOwnerAsync();
        var draftId = await CreateDraftAsync(owner, DraftPayload(owner.KnowledgeBaseId));

        var response = await TrialAnswerAsync(owner, draftId, new string('問', 2001));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyJsonAsync(response)).GetProperty("errors").GetProperty("question")[0].GetString()
            .ShouldBe(AssistantDraftTrialAnswerRules.QuestionTooLongMessage);
    }

    [Fact]
    public async Task Without_a_chat_provider_it_is_503_chat_not_configured()
    {
        await using var unconfigured = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Ai:Chat:Provider", string.Empty));
        var owner = await CreateOwnerAsync(unconfigured);
        await UploadAndApproveAsync(owner);
        var draftId = await CreateDraftAsync(owner, DraftPayload(owner.KnowledgeBaseId));

        var response = await TrialAnswerAsync(owner, draftId, "收到商品後幾天內可以申請退貨？退貨運費由誰負擔？");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await BodyJsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe(ChatErrors.ChatNotConfiguredReason);
    }

    // --- Fixtures -------------------------------------------------------------------------

    private sealed record Owner(AuthHostFixture Host, Organization Organization, SpaClient Spa, string Token, Guid AccountId, Guid KnowledgeBaseId);

    private async Task<Owner> CreateOwnerAsync(WebApplicationFactory<Program>? via = null)
    {
        var organization = await _host.CreateOrganizationAsync("試答商行");
        await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "試答商行管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources);
        var spa = via is null
            ? _host.CreateSpaClient()
            : new SpaClient(via.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var token = (await spa.SignInAsync(organization.Code, "admin", Password)).AccessToken;
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var accountId = await dbContext.Accounts.Select(account => account.Id).SingleAsync(CancellationToken);

        var created = await spa.PostAsync("/api/v1/knowledge-bases", token, new { name = "試答知識庫", purpose = "" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var knowledgeBaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync(CancellationToken)).RootElement.GetProperty("id").GetGuid();

        return new Owner(_host, organization, spa, token, accountId, knowledgeBaseId);
    }

    private async Task<Guid> UploadAndApproveAsync(Owner owner)
    {
        var content = Encoding.UTF8.GetBytes($"# 退貨政策\n\n{ReturnClause}\n");
        var uploaded = await PostFileAsync(owner, $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/documents", "退貨政策.md", content);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(CancellationToken));
        var view = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(CancellationToken)).RootElement;
        var (documentId, versionId) = (view.GetProperty("id").GetGuid(), view.GetProperty("latestVersionId").GetGuid());

        await _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        var approved = await owner.Spa.PostAsync(
            $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/versions/approve", owner.Token, new { versionIds = new[] { versionId.ToString() } });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(CancellationToken));
        return documentId;
    }

    /// <summary>A knowledge base of another organization, with a document approved and
    /// containing the very same clause, so it would score just as well if it were ever
    /// searched — proving the trial answer excludes it because of connectability, not content.</summary>
    private async Task<Guid> CreateForeignKnowledgeBaseWithMatchingContentAsync(Organization otherOrg)
    {
        var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(otherOrg.Code, "admin", Password)).AccessToken;
        var created = await spa.PostAsync("/api/v1/knowledge-bases", token, new { name = "別組織的知識庫", purpose = "" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var knowledgeBaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync(CancellationToken)).RootElement.GetProperty("id").GetGuid();

        var content = Encoding.UTF8.GetBytes($"# 退貨政策\n\n{ReturnClause}\n");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "退貨政策.md");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-bases/{knowledgeBaseId}/documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var uploaded = await spa.Http.SendAsync(request, CancellationToken);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(CancellationToken));
        var versionId = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(CancellationToken)).RootElement.GetProperty("latestVersionId").GetGuid();

        await _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        var approved = await spa.PostAsync(
            $"/api/v1/knowledge-bases/{knowledgeBaseId}/versions/approve", token, new { versionIds = new[] { versionId.ToString() } });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(CancellationToken));
        return knowledgeBaseId;
    }

    private async Task<HttpResponseMessage> PostFileAsync(Owner owner, string path, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var response = await owner.Spa.Http.SendAsync(request, CancellationToken);
        await response.Content.LoadIntoBufferAsync(CancellationToken);
        return response;
    }

    private async Task<Guid> CreateDraftAsync(Owner owner, string payloadJson)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var draft = new AssistantDraft(owner.Organization.Id, owner.AccountId, payloadJson, schemaVersion: 1, DateTimeOffset.UtcNow);
        dbContext.AssistantDrafts.Add(draft);
        await dbContext.SaveChangesAsync(CancellationToken);
        return draft.Id;
    }

    private static Task<HttpResponseMessage> TrialAnswerAsync(Owner owner, Guid draftId, string? question) =>
        owner.Spa.PostAsync($"/api/v1/assistant-drafts/{draftId}/trial-answers", owner.Token, new { question });

    private static string DraftPayload(Guid knowledgeBaseId, Guid? secondKnowledgeBaseId = null) =>
        $$"""
        {
          "name": "退貨小幫手",
          "purpose": "回答退換貨問題",
          "tone": "friendly",
          "roleInstructions": "",
          "sources": [
            { "id": "{{knowledgeBaseId}}", "type": "knowledge-base" }{{(secondKnowledgeBaseId is { } id ? $$""", { "id": "{{id}}", "type": "knowledge-base" }""" : "")}}
          ],
          "rules": {
            "knowledgeScope": "company-data-only",
            "refusalMessage": "目前的資料中找不到這個問題的答案。",
            "showCitations": true,
            "keepOwnConversations": true
          }
        }
        """;

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
