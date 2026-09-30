namespace SmartAgri.Application.Assistants;

/// <summary>
/// The payload of a <see cref="Kind"/> background job (M3.5 plan §3, issue #124): answer and
/// judge every test case of one <c>AssistantTestRun</c>. Only the run's id — the assistant and
/// its test set are read when the job runs.
/// </summary>
/// <remarks>
/// Enqueued in the same save as the queued run it names. Delivery is at least once and the run
/// may be deleted (with its assistant, or by retention) while the job waits, so a handler treats
/// a missing or already finished run as nothing to do.
/// </remarks>
public sealed record RunAssistantTestSetJob(Guid RunId)
{
    public const string Kind = "assistants.run-test-set";

    /// <summary>Attempts for infrastructure failures (e.g. the database). A model or embedding
    /// failure fails the run at once instead — retrying up to 50 questions is exactly the cost
    /// the plan caps (§7 技術風險 1); the owner can press 「全部重跑」 again.</summary>
    public const int MaxAttempts = 3;
}
