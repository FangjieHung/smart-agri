using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Ai;
using SmartAgri.Domain.Ai;

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
}
