using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// One execution of an assistant's whole test set (「全部重跑」; M3.5 plan §3/§4, issue #124),
/// processed by the <c>assistants.run-test-set</c> background job. Its per-question outcomes are
/// <see cref="AssistantTestResult"/>s, written all at once when the run completes.
/// </summary>
/// <remarks>
/// <para>
/// At most one run per assistant is active (<see cref="AssistantTestRunStatus.Queued"/> or
/// <see cref="AssistantTestRunStatus.Running"/>; a partial unique index enforces it). Asking for
/// another while one is active only sets <see cref="RerunRequested"/> (plan §3: 「同一個助理若已有
/// 排隊中或執行中的重跑，就不再排入新的，只把那一次標成『需要再跑一次』」): a queued run clears the
/// flag when it starts, since it then reads the assistant's current state anyway; a run that
/// finishes (completed or failed) with the flag set is followed by a fresh queued run.
/// </para>
/// <para>
/// <see cref="Status"/> and <see cref="RerunRequested"/> are concurrency tokens, so a rerun
/// request and the job finishing the run can never silently overwrite each other.
/// </para>
/// </remarks>
public sealed class AssistantTestRun : IOrganizationScoped
{
    /// <summary>How many runs per assistant are kept (plan §4: 「保留最近 N 次，例如 20 次」); older
    /// ones are deleted, with their results, whenever a new run is queued.</summary>
    public const int KeptPerAssistant = 20;

    public const int PromptVersionMaxLength = 64;

    public const int ModelMaxLength = Ai.ModelInvocation.ModelMaxLength;

    /// <summary>For EF Core materialization.</summary>
    private AssistantTestRun()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid AssistantId { get; private set; }

    public AssistantTestRunTrigger Trigger { get; private set; }

    public AssistantTestRunStatus Status { get; private set; }

    /// <summary>Another run was asked for while this one was active: once it finishes, a fresh
    /// run is queued.</summary>
    public bool RerunRequested { get; private set; }

    public DateTimeOffset QueuedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>When it completed or failed.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    public int PassedCount { get; private set; }

    public int FailedCount { get; private set; }

    /// <summary>The answer prompt's version it ran with; set when it starts.</summary>
    public string? PromptVersion { get; private set; }

    /// <summary>The chat model it ran with; set when it starts.</summary>
    public string? Model { get; private set; }

    /// <summary>The relevance threshold it ran with (the assistant's own, or the deployment's);
    /// set when it starts.</summary>
    public double? MinScore { get; private set; }

    public bool IsActive => Status is AssistantTestRunStatus.Queued or AssistantTestRunStatus.Running;

    public static AssistantTestRun Queue(Guid organizationId, Guid assistantId, AssistantTestRunTrigger trigger, DateTimeOffset now)
    {
        RequireId(organizationId, nameof(organizationId));
        RequireId(assistantId, nameof(assistantId));
        if (!Enum.IsDefined(trigger))
        {
            throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Not a declared trigger.");
        }

        return new AssistantTestRun
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AssistantId = assistantId,
            Trigger = trigger,
            Status = AssistantTestRunStatus.Queued,
            QueuedAt = now,
        };
    }

    /// <summary>Asks for one more run after this active one.</summary>
    public void RequestRerun()
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("Only a queued or running test run can be asked to run again.");
        }

        RerunRequested = true;
    }

    /// <summary><see cref="AssistantTestRunStatus.Queued"/> → <see cref="AssistantTestRunStatus.Running"/>,
    /// recording what it runs with. Clears <see cref="RerunRequested"/>: a rerun asked for before
    /// the start is satisfied by this run.</summary>
    public void Start(string promptVersion, string model, double minScore, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        ArgumentNullException.ThrowIfNull(model);
        if (Status != AssistantTestRunStatus.Queued)
        {
            throw new InvalidOperationException("Only a queued test run can start.");
        }

        Status = AssistantTestRunStatus.Running;
        StartedAt = now;
        RerunRequested = false;
        PromptVersion = Truncate(promptVersion, PromptVersionMaxLength);
        Model = Truncate(model, ModelMaxLength);
        MinScore = minScore;
    }

    public void Complete(int passedCount, int failedCount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(passedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(failedCount);
        if (Status != AssistantTestRunStatus.Running)
        {
            throw new InvalidOperationException("Only a running test run can complete.");
        }

        Status = AssistantTestRunStatus.Completed;
        PassedCount = passedCount;
        FailedCount = failedCount;
        CompletedAt = now;
    }

    /// <summary>Marks an active run failed; it keeps no results.</summary>
    public void Fail(DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("Only a queued or running test run can fail.");
        }

        Status = AssistantTestRunStatus.Failed;
        PassedCount = 0;
        FailedCount = 0;
        CompletedAt = now;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", parameterName);
        }
    }
}
