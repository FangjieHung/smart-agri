using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Ai;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Chat;

public sealed partial class ChatRunEndpointsTests
{
    // --- M6-1 (#238): the organization's chat model ---------------------------------------------

    [Fact]
    public async Task A_reply_is_answered_by_and_recorded_against_the_model_the_organization_resolves_to()
    {
        await using var factory = SecondChatModel.Host(_host.Factory);
        var org = await CreateOrganizationWithKnowledgeAsync();
        var admin = await SignInAsync(org, "admin", factory);
        var assistantId = await CreateAssistantAsync(org, keepConversations: true);

        var run = await RunAsync(admin, assistantId, RunInput(RelatedQuestion));

        run.Types[^1].ShouldBe("RUN_FINISHED", run.Body);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var calls = await dbContext.ModelInvocations.AsNoTracking()
            .Where(invocation => invocation.Purpose == ModelInvocationPurpose.GenerateAnswer)
            .ToListAsync(CancellationToken);
        calls.ShouldNotBeEmpty();
        calls.ShouldAllBe(call => call.Provider == "fake" && call.Model == SecondChatModel.Model && call.Succeeded);
    }

    // --- M6-2 (#239): the manager chooses the organization's chat model ---------------------------

    [Fact]
    public async Task After_the_manager_changes_the_model_new_replies_use_and_record_the_new_one()
    {
        await using var factory = SecondChatModel.Offered(_host.Factory);
        var org = await CreateOrganizationWithKnowledgeAsync();
        var admin = await SignInAsync(org, "admin", factory);
        var assistantId = await CreateAssistantAsync(org, keepConversations: true);

        (await RunAsync(admin, assistantId, RunInput(RelatedQuestion))).Types[^1].ShouldBe("RUN_FINISHED");
        (await AnswerModelsAsync(org)).ShouldBe([AuthHostFixture.ChatModel]);

        var changed = await admin.Spa.PutAsync("/api/v1/organization/chat-model", admin.Token, new { modelId = SecondChatModel.Id, revision = 0 });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(CancellationToken));

        var run = await RunAsync(admin, assistantId, RunInput(RelatedQuestion));
        run.Types[^1].ShouldBe("RUN_FINISHED", run.Body);
        (await AnswerModelsAsync(org)).ShouldBe([AuthHostFixture.ChatModel, SecondChatModel.Model]);
    }

    [Fact]
    public async Task A_choice_the_deployment_no_longer_offers_falls_back_to_the_default_and_replies_as_usual()
    {
        var org = await CreateOrganizationWithKnowledgeAsync();
        await using (var dbContext = _host.Postgres.CreateDbContext())
        {
            var organization = await dbContext.Organizations.SingleAsync(candidate => candidate.Id == org.Organization.Id, CancellationToken);
            organization.ChangeChatModel("withdrawn-model", organization.SettingsRevision).ShouldBe(OrganizationSettingsChange.Changed);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var admin = await SignInAsync(org, "admin");
        var assistantId = await CreateAssistantAsync(org, keepConversations: true);

        var run = await RunAsync(admin, assistantId, RunInput(RelatedQuestion));

        run.Types[^1].ShouldBe("RUN_FINISHED", run.Body);
        (await AnswerModelsAsync(org)).ShouldBe([AuthHostFixture.ChatModel]);
    }

    /// <summary>The distinct models of the organization's answer calls, oldest first.</summary>
    private async Task<List<string>> AnswerModelsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var calls = await dbContext.ModelInvocations.AsNoTracking()
            .Where(invocation => invocation.Purpose == ModelInvocationPurpose.GenerateAnswer)
            .OrderBy(invocation => invocation.At)
            .ToListAsync(CancellationToken);
        calls.ShouldAllBe(call => call.Succeeded);
        return [.. calls.Select(call => call.Model).Distinct()];
    }
}
