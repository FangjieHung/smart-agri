using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Ai;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Api.Tests.Assistants;

public sealed partial class AssistantTestRunEndpointsTests
{
    // --- M6-1 (#238): the organization's chat model ---------------------------------------------

    [Fact]
    public async Task A_run_records_and_is_answered_by_the_model_the_organization_resolves_to()
    {
        await using var factory = SecondChatModel.Host(_host.Factory);
        var owner = await CreateOwnerWithAssistantAsync();
        var returnDocument = await UploadAndApproveAsync(owner, "退貨政策.md", ReturnClause);
        await CreateTestCaseAsync(owner, ReturnQuestion, "company-data", [returnDocument]);

        var queued = await RequestRunAsync(owner);
        queued.StatusCode.ShouldBe(HttpStatusCode.Accepted, await queued.Content.ReadAsStringAsync(CancellationToken));
        // The background job runs in the host whose resolver picks the second model.
        await factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var run = await dbContext.AssistantTestRuns.AsNoTracking().SingleAsync(candidate => candidate.AssistantId == owner.AssistantId, CancellationToken);
        (run.Status, run.Model).ShouldBe((AssistantTestRunStatus.Completed, (string?)SecondChatModel.Model));
        var call = await dbContext.ModelInvocations.AsNoTracking()
            .SingleAsync(invocation => invocation.Purpose == ModelInvocationPurpose.AssistantTest, CancellationToken);
        (call.Provider, call.Model, call.Succeeded).ShouldBe(("fake", SecondChatModel.Model, true));
    }

    // --- M6-2 (#239): the manager chooses the organization's chat model ---------------------------

    [Fact]
    public async Task After_the_manager_changes_the_model_a_rerun_uses_and_records_the_new_one()
    {
        await using var factory = SecondChatModel.Offered(_host.Factory);
        var owner = await CreateOwnerWithAssistantAsync();
        var returnDocument = await UploadAndApproveAsync(owner, "退貨政策.md", ReturnClause);
        await CreateTestCaseAsync(owner, ReturnQuestion, "company-data", [returnDocument]);

        // The manager (this owner is smb-admin) chooses through the host that offers the model.
        using var spa = new SpaClient(factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var token = (await spa.SignInAsync(owner.Organization.Code, "admin", Password)).AccessToken;
        var changed = await spa.PutAsync("/api/v1/organization/chat-model", token, new { modelId = SecondChatModel.Id, revision = 0 });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(CancellationToken));

        var queued = await RequestRunAsync(owner);
        queued.StatusCode.ShouldBe(HttpStatusCode.Accepted, await queued.Content.ReadAsStringAsync(CancellationToken));
        await factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var run = await dbContext.AssistantTestRuns.AsNoTracking().SingleAsync(candidate => candidate.AssistantId == owner.AssistantId, CancellationToken);
        (run.Status, run.Model).ShouldBe((AssistantTestRunStatus.Completed, (string?)SecondChatModel.Model));
        var call = await dbContext.ModelInvocations.AsNoTracking()
            .SingleAsync(invocation => invocation.Purpose == ModelInvocationPurpose.AssistantTest, CancellationToken);
        (call.Provider, call.Model, call.Succeeded).ShouldBe(("fake", SecondChatModel.Model, true));
    }
}
