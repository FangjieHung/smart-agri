using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Tests.Ai;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Reports;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Reports;

public sealed partial class PeriodicReportEndpointsTests
{
    // --- M6-1 (#238): the organization's chat model ---------------------------------------------

    [Fact]
    public async Task A_summary_is_written_by_and_recorded_against_the_model_the_organization_resolves_to()
    {
        // The second model's responses carry no model name, so the saved summary's model can only
        // come from what the organization resolved to.
        const string secondModel = "scripted-second";
        await using var factory = _host.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(provider =>
            {
                var deploymentDefault = provider.GetRequiredService<ChatClientProvider>();
                var second = new ChatClientProvider(new UnnamedResponses(new FakeChatClient(secondModel)), "fake", "smartagri.fake", secondModel, endpoint: null);
                return new ChatModelCatalog(
                    new ChatModelEntry(deploymentDefault.Model, deploymentDefault.Model, deploymentDefault, isDeploymentDefault: true),
                    [new ChatModelEntry(SecondChatModel.Id, "第二個模型", second, isDeploymentDefault: false)]);
            });
            SecondChatModel.Choose(services);
        }));
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var databaseId = await CreateDatabaseAsync(admin);
        await ScheduleAsync(org, admin, databaseId, "weekly");
        await SeedTwoWeeksAsync(org, databaseId);

        await MakeJobsDueAsync(org);
        await RunJobsAsync(factory);

        var (_, report) = await SavedReportAsync(org);
        (report.SummaryStatus, report.SummaryModel).ShouldBe((ReportSummaryStatus.Ready, (string?)secondModel));
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var call = await dbContext.ModelInvocations.AsNoTracking()
            .SingleAsync(invocation => invocation.Purpose == ModelInvocationPurpose.GenerateReportSummary, CancellationToken);
        (call.Provider, call.Model, call.Succeeded).ShouldBe(("fake", secondModel, true));
    }

    /// <summary>A model whose responses do not say which model wrote them.</summary>
    private sealed class UnnamedResponses(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            response.ModelId = null;
            return response;
        }
    }
}
