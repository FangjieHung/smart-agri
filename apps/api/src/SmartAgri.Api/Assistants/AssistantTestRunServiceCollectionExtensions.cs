using SmartAgri.Api.Jobs;
using SmartAgri.Application.Assistants;

namespace SmartAgri.Api.Assistants;

public static class AssistantTestRunServiceCollectionExtensions
{
    /// <summary>
    /// Registers the handlers of <see cref="RunAssistantTestSetJob.Kind"/> (M3.5 Slice 2, #124)
    /// and <see cref="RequestAssistantTestRunsJob.Kind"/> (Slice 3, #125) jobs. Requires <c>AddBackgroundJobs</c>, <c>AddKnowledge</c> (retrieval settings),
    /// <c>AddChat</c> and <c>AddGroundedAnswers</c>.
    /// </summary>
    public static IServiceCollection AddAssistantTestRuns(this IServiceCollection services)
    {
        services.AddJobHandler<RunAssistantTestSetHandler>(RunAssistantTestSetJob.Kind);
        services.AddJobHandler<RequestAssistantTestRunsHandler>(RequestAssistantTestRunsJob.Kind);
        return services;
    }
}
