using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tenancy;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Knowledge;
using SmartAgri.Application.Answers;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Api.Tests.Answers;

/// <summary>
/// <see cref="GroundedAnswerService"/> as the Api's container builds it, against real PostgreSQL
/// with the <c>Fake</c> embedding model and <c>FakeChatClient</c> (M3 plan Slice 5): a document
/// uploaded, processed and approved through the real endpoints, an assistant connected to its
/// knowledge base, and one answer above and one below the threshold.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class GroundedAnswerIntegrationTests : IClassFixture<AuthHostFixture>
{
    private const string RefusalMessage = "目前的資料中找不到這個問題的答案。";
    private const string ReturnClause = "收到商品後七天內可申請退貨，退貨運費由買家負擔。";

    private readonly AuthHostFixture _host;

    public GroundedAnswerIntegrationTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_related_question_is_a_company_data_reply_citing_the_document_and_an_unrelated_one_is_no_result_without_a_model_call()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var documentId = await UploadAndApproveAsync(owner);
        var assistantId = await CreateAssistantAsync(owner);

        // Above the threshold: grounded on the approved document, cited by the fake model's [1].
        var answered = await AnswerAsync(owner, assistantId, "收到商品後幾天內可以申請退貨？退貨運費由誰負擔？");

        answered.Reply.Kind.ShouldBe(GroundedReplyKind.CompanyData, $"top score {answered.Retrieval.Passages.FirstOrDefault()?.Score}");
        answered.Reply.Text.ShouldContain("[1]");
        var citation = answered.Reply.Citations.ShouldHaveSingleItem();
        (citation.Ordinal, citation.KnowledgeBaseId, citation.KnowledgeBaseName, citation.DocumentId, citation.DocumentName, citation.VersionNumber)
            .ShouldBe((1, owner.KnowledgeBaseId, "退換貨政策", documentId, "退貨政策.md", 1));
        citation.Text.ShouldContain(ReturnClause);
        citation.ChunkId.ShouldBe(answered.Retrieval.Relevant[0].ChunkId);

        // Below the threshold: company-data-only refuses without calling the chat model.
        var refused = await AnswerAsync(owner, assistantId, "zzzz qqqq xxxx");

        refused.Retrieval.BelowThreshold.ShouldBeTrue($"top score {refused.Retrieval.Passages.FirstOrDefault()?.Score}");
        (refused.Reply.Kind, refused.Reply.Text, refused.Reply.RejectionReason).ShouldBe(
            (GroundedReplyKind.NoResult, RefusalMessage, (GroundedRejectionReason?)GroundedRejectionReason.BelowThreshold));

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var calls = await dbContext.ModelInvocations.AsNoTracking()
            .Where(invocation => invocation.Purpose == ModelInvocationPurpose.GenerateAnswer)
            .ToListAsync(CancellationToken);
        var call = calls.ShouldHaveSingleItem("only the answer above the threshold called the chat model");
        (call.AccountId, call.AssistantId, call.Model, call.Succeeded).ShouldBe(
            ((Guid?)owner.AccountId, (Guid?)assistantId, AuthHostFixture.ChatModel, true));
        (await dbContext.ModelInvocations.CountAsync(invocation => invocation.Purpose == ModelInvocationPurpose.EmbedQuery, CancellationToken))
            .ShouldBe(2, "both questions were embedded");
    }

    [Fact]
    public async Task The_streamed_answer_ends_with_the_same_validated_reply()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        await UploadAndApproveAsync(owner);
        var assistantId = await CreateAssistantAsync(owner);

        var events = new List<GroundedAnswerEvent>();
        await InOrganizationScopeAsync(owner, async services =>
        {
            var request = await RequestAsync(services, owner, assistantId, "收到商品後幾天內可以申請退貨？退貨運費由誰負擔？");
            await foreach (var answerEvent in services.GetRequiredService<GroundedAnswerService>().StreamAsync(request, CancellationToken))
            {
                events.Add(answerEvent);
            }
        });

        events.Count.ShouldBeGreaterThan(2, "the fake model streams at least two pieces");
        events[..^1].ShouldAllBe(answerEvent => answerEvent is GroundedAnswerTextDelta);
        string.Concat(events.OfType<GroundedAnswerTextDelta>().Select(delta => delta.Text)).ShouldContain("[1]");
        var reply = events[^1].ShouldBeOfType<GroundedAnswerCompleted>().Reply;
        (reply.Kind, reply.Citations.Count).ShouldBe((GroundedReplyKind.CompanyData, 1));
    }

    private async Task<GroundedAnswerResult> AnswerAsync(KnowledgeTestOwner owner, Guid assistantId, string question)
    {
        GroundedAnswerResult? result = null;
        await InOrganizationScopeAsync(owner, async services =>
        {
            var request = await RequestAsync(services, owner, assistantId, question);
            result = await services.GetRequiredService<GroundedAnswerService>().AnswerAsync(request, CancellationToken);
        });
        return result!;
    }

    private static async Task<GroundedAnswerRequest> RequestAsync(IServiceProvider services, KnowledgeTestOwner owner, Guid assistantId, string question)
    {
        var dbContext = services.GetRequiredService<SmartAgri.Infrastructure.AppDbContext>();
        var assistant = await dbContext.Assistants.AsNoTracking().SingleAsync(candidate => candidate.Id == assistantId, CancellationToken);
        var connected = await services.GetRequiredService<IAnswerKnowledgeBases>().ConnectedToAsync(assistantId, CancellationToken);
        return new GroundedAnswerRequest(GroundedAnswerProfile.For(assistant, connected), question, [], owner.AccountId, assistantId);
    }

    /// <summary>Runs <paramref name="action"/> in a DI scope acting for the owner's organization
    /// the way a request does: through the <c>org_id</c> claim of the current principal.</summary>
    private async Task InOrganizationScopeAsync(KnowledgeTestOwner owner, Func<IServiceProvider, Task> action)
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(OrganizationClaimTypes.OrganizationId, owner.Organization.Id.ToString("D"))], authenticationType: "test")),
        };
        try
        {
            await action(scope.ServiceProvider);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private async Task<Guid> UploadAndApproveAsync(KnowledgeTestOwner owner)
    {
        var content = Encoding.UTF8.GetBytes($"# 退貨政策\n\n{ReturnClause}\n");
        var uploaded = await owner.PostFileAsync($"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/documents", "退貨政策.md", content);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(CancellationToken));
        var view = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(CancellationToken)).RootElement;
        var (documentId, versionId) = (view.GetProperty("id").GetGuid(), view.GetProperty("latestVersionId").GetGuid());

        await _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        var approved = await owner.Spa.PostAsync(
            $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/versions/approve", owner.Token, new { versionIds = new[] { versionId.ToString() } });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(CancellationToken));
        return documentId;
    }

    /// <summary>Seeds the assistant directly (there is no create endpoint yet; that is #72).</summary>
    private async Task<Guid> CreateAssistantAsync(KnowledgeTestOwner owner)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var assistant = Assistant.Create(
            owner.Organization.Id, owner.AccountId, "退貨小幫手", "回答退換貨問題", null, AssistantTone.Friendly, string.Empty,
            AssistantKnowledgeScope.CompanyDataOnly, RefusalMessage, showCitations: true, keepConversations: true, now);
        var knowledgeBase = await dbContext.KnowledgeBases.SingleAsync(candidate => candidate.Id == owner.KnowledgeBaseId, CancellationToken);
        dbContext.Assistants.Add(assistant);
        dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
        await dbContext.SaveChangesAsync(CancellationToken);
        return assistant.Id;
    }
}
