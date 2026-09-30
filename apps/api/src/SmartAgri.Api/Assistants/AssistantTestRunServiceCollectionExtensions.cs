using SmartAgri.Api.Jobs;
using SmartAgri.Application.Assistants;

namespace SmartAgri.Api.Assistants;

public static class AssistantTestRunServiceCollectionExtensions
{
    /// <summary>
    /// Registers the handler of <see cref="RunAssistantTestSetJob.Kind"/> jobs (M3.5 Slice 2,
    /// #124). Requires <c>AddBackgroundJobs</c>, <c>AddKnowledge</c> (retrieval settings),
    /// <c>AddChat</c> and <c>AddGroundedAnswers</c>.
    /// </summary>
    public static IServiceCollection AddAssistantTestRuns(this IServiceCollection services)
    {
        services.AddJobHandler<RunAssistantTestSetHandler>(RunAssistantTestSetJob.Kind);
        return services;
    }
}
