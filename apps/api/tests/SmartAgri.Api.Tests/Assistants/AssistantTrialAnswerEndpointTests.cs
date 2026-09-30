using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// <c>POST /api/v1/assistants/{id}/trial-answers</c> against real PostgreSQL, with the
/// <c>Fake</c> embedding and chat models (M3.5 plan Slice 1, issue #123): a real (already built)
/// assistant, unlike <see cref="AssistantDraftTrialAnswerEndpointTests"/>'s wizard draft. The
/// response shape is asserted identical to the draft's own trial answer.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class AssistantTrialAnswerEndpointTests : IClassFixture<AuthHostFixture>
{
    private const string Password = "Assistant-Trial-Pass-1!";
    private const string ReturnClause = "收到商品後七天內可申請退貨，退貨運費由買家負擔。";
    private const string RefusalMessage = "目前的資料中找不到這個問題的答案。";

    private readonly AuthHostFixture _host;

    public AssistantTrialAnswerEndpointTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_related_question_is_company_data_and_an_unrelated_one_is_no_result_calling_the_model_only_once()
    {
        var owner = await CreateOwnerWithAssistantAsync();
        var documentId = await UploadAndApproveAsync(owner);

        var answered = await TrialAnswerAsync(owner, "收到商品後幾天內可以申請退貨？退貨運費由誰負擔？");
        answered.StatusCode.ShouldBe(HttpStatusCode.OK, await answered.Content.ReadAsStringAsync(CancellationToken));
        var answeredBody = await BodyJsonAsync(answered);
        OpenApiContract.AssertKeysMatchSchema(answeredBody, "TrialAnswerResponse");

        answeredBody.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("company-data");
        var citations = answeredBody.GetProperty("reply").GetProperty("citations");
        citations.GetArrayLength().ShouldBe(1);
        citations[0].GetProperty("documentId").GetGuid().ShouldBe(documentId);

        // Below the threshold: no model call at all for this question (issue #123 acceptance:
        // "試問低於門檻時不呼叫模型").
        var refused = await TrialAnswerAsync(owner, "zzzz qqqq xxxx");
        refused.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refusedBody = await BodyJsonAsync(refused);
        refusedBody.GetProperty("reply").GetProperty("kind").GetString().ShouldBe("no-result");
        refusedBody.GetProperty("reply").GetProperty("text").GetString().ShouldBe(RefusalMessage);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var calls = await dbContext.ModelInvocations.AsNoTracking()
            .Where(invocation => invocation.Purpose == ModelInvocationPurpose.TrialAnswer)
            .ToListAsync(CancellationToken);
        var call = calls.ShouldHaveSingleItem("only the answer above the threshold called the chat model");
        (call.AccountId, call.AssistantId).ShouldBe((owner.AccountId, (Guid?)owner.AssistantId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task A_blank_question_is_422(string? question)
    {
        var owner = await CreateOwnerWithAssistantAsync();

        var response = await TrialAnswerAsync(owner, question);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Someone_elses_assistant_and_a_nonexistent_one_get_identical_403s()
    {
        var orgA = await CreateOwnerWithAssistantAsync();
        var orgB = await _host.CreateOrganizationAsync("試答商行 B");
        await _host.CreateAccountAsync(orgB, "admin", Password, AccountRole.SmbAdmin, "B 管理者", AccountPermission.ManageAssistants);
        var spaB = _host.CreateSpaClient();
        var tokenB = (await spaB.SignInAsync(orgB.Code, "admin", Password)).AccessToken;

        var onForeign = await spaB.PostAsync(
            $"/api/v1/assistants/{orgA.AssistantId}/trial-answers", tokenB, new { question = "有人在嗎？" });
        var onMissing = await spaB.PostAsync(
            $"/api/v1/assistants/{Guid.NewGuid()}/trial-answers", tokenB, new { question = "有人在嗎？" });

        onForeign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var a = await ResponseFingerprint.FromAsync(onForeign);
        var b = await ResponseFingerprint.FromAsync(onMissing);
        b.Status.ShouldBe(a.Status);
        b.Body.ShouldBe(a.Body);
    }

    // --- Fixtures -------------------------------------------------------------------------

    private sealed record Owner(
        AuthHostFixture Host, Organization Organization, SpaClient Spa, string Token, Guid AccountId, Guid KnowledgeBaseId, Guid AssistantId);

    private async Task<Owner> CreateOwnerWithAssistantAsync()
    {
        var organization = await _host.CreateOrganizationAsync("試答商行");
        await _host.CreateAccountAsync(
            organization, "admin", Password, AccountRole.SmbAdmin, "試答商行管理者",
            AccountPermission.ManageAssistants, AccountPermission.ManageDataSources);
        var spa = _host.CreateSpaClient();
        var token = (await spa.SignInAsync(organization.Code, "admin", Password)).AccessToken;
        await using var dbContext = _host.Postgres.CreateDbContext(organization.Id);
        var accountId = await dbContext.Accounts.Select(account => account.Id).SingleAsync(CancellationToken);

        var created = await spa.PostAsync("/api/v1/knowledge-bases", token, new { name = "試答知識庫", purpose = "" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var knowledgeBaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync(CancellationToken)).RootElement.GetProperty("id").GetGuid();

        var now = DateTimeOffset.UtcNow;
        var assistant = Assistant.Create(
            organization.Id, accountId, "退貨小幫手", "回答退換貨問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly, RefusalMessage,
            showCitations: true, keepConversations: true, now);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == knowledgeBaseId, CancellationToken);
        dbContext.Assistants.Add(assistant);
        dbContext.AssistantKnowledgeBases.Add(new SmartAgri.Domain.Assistants.AssistantKnowledgeBase(assistant, knowledgeBase, now));
        await dbContext.SaveChangesAsync(CancellationToken);

        return new Owner(_host, organization, spa, token, accountId, knowledgeBaseId, assistant.Id);
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

    private static Task<HttpResponseMessage> TrialAnswerAsync(Owner owner, string? question) =>
        owner.Spa.PostAsync($"/api/v1/assistants/{owner.AssistantId}/trial-answers", owner.Token, new { question });

    private static async Task<JsonElement> BodyJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
