namespace SmartAgri.Api.Jobs;

/// <summary>Configuration section <c>Jobs</c> (see apps/api/README.md, "Background jobs").</summary>
public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Whether this process runs <see cref="JobWorker"/>. Integration tests turn
    /// it off and drive <see cref="JobRunner.RunUntilIdleAsync"/> themselves.</summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>How long the worker waits after finding nothing to claim.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How many jobs this process runs at the same time.</summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>
    /// How long a claimed job stays locked to its runner. A job still running after that
    /// (its process stopped) is claimed again, so this must be longer than any handler
    /// takes; a runner that overruns it cannot record its outcome.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Delay before the second attempt; doubles after each further failure.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Why these options are unusable, or <see langword="null"/>.</summary>
    public string? Validate() =>
        PollInterval <= TimeSpan.Zero ? $"{SectionName}:{nameof(PollInterval)} must be positive."
        : Concurrency is < 1 or > 64 ? $"{SectionName}:{nameof(Concurrency)} must be 1-64."
        : LeaseDuration <= TimeSpan.Zero ? $"{SectionName}:{nameof(LeaseDuration)} must be positive."
        : RetryBaseDelay <= TimeSpan.Zero ? $"{SectionName}:{nameof(RetryBaseDelay)} must be positive."
        : RetryMaxDelay < RetryBaseDelay ? $"{SectionName}:{nameof(RetryMaxDelay)} must not be less than {nameof(RetryBaseDelay)}."
        : null;
}
