using SmartAgri.Application.Assistants;
using SmartAgri.Application.Jobs;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

/// <summary>
/// Handles <see cref="RequestAssistantTestRunsJob.Kind"/> (issue #125): at a future-dated
/// version's <c>EffectiveFrom</c>, asks for the <see cref="AssistantTestRunTrigger.KnowledgeChanged"/>
/// reruns of the assistants then connected to its knowledge base, exactly as an immediate
/// approval does (<see cref="AssistantTestRunQueue.RequestForKnowledgeBaseAsync"/>). A deleted
/// knowledge base simply has no connected assistant any more.
/// </summary>
internal sealed class RequestAssistantTestRunsHandler : IJobHandler
{
    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _clock;

    public RequestAssistantTestRunsHandler(AppDbContext dbContext, TimeProvider clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var knowledgeBaseId = job.ReadPayload<RequestAssistantTestRunsJob>().KnowledgeBaseId;
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await AssistantTestRunQueue.RequestForKnowledgeBaseAsync(
            _dbContext, job.OrganizationId, knowledgeBaseId, AssistantTestRunTrigger.KnowledgeChanged, _clock.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
