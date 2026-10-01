namespace SmartAgri.Application.Assistants;

/// <summary>
/// The payload of a <see cref="Kind"/> background job (M3.5 plan §3, issue #125): a knowledge
/// base's approved version takes effect at a future time, so the reruns of the assistants
/// connected to it are asked for then — the job's <c>RunAfter</c> is the version's
/// <c>EffectiveFrom</c> — rather than at approval.
/// </summary>
/// <remarks>
/// Enqueued in the same save as the approval. Which assistants are affected (connected, with at
/// least one test case) is decided when the job runs, as for an immediate change. Delivery is at
/// least once; running it twice only sets <c>RerunRequested</c> on the runs it queued.
/// </remarks>
public sealed record RequestAssistantTestRunsJob(Guid KnowledgeBaseId)
{
    public const string Kind = "assistants.request-test-runs";
}
