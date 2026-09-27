using System.Text.Json;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Jobs;

/// <summary>
/// One unit of background work (table <c>BackgroundJobs</c>; M2 plan §3 and §4, and the
/// background-jobs-on-postgresql ADR): the table is the queue. A job belongs to one
/// organization and is always processed on its behalf, so the organization filter and the
/// write guard apply to the handler exactly as to a request.
/// </summary>
/// <remarks>
/// <para>
/// To enqueue, add a job in the same <c>SaveChanges</c> as the business rows it is about,
/// e.g. <c>dbContext.BackgroundJobs.Add(BackgroundJob.Create(organizationId, kind, payload, now))</c>:
/// the row and its job then commit (or roll back) together.
/// </para>
/// <para>
/// Claiming (<see cref="BackgroundJobStatus.Queued"/> → <see cref="BackgroundJobStatus.Running"/>,
/// <see cref="Attempts"/> + 1, <see cref="LockedUntil"/> set) is a single SQL statement in
/// <c>SmartAgri.Infrastructure.Jobs.JobClaimer</c>, not a method here, because it has to
/// pick a row across organizations atomically. Everything after the claim goes through
/// the methods below, in the job's own organization.
/// </para>
/// </remarks>
public sealed class BackgroundJob : IOrganizationScoped
{
    public const int KindMaxLength = 64;

    public const int LastErrorMaxLength = 2000;

    public const int DefaultMaxAttempts = 5;

    /// <summary>Upper bound for <see cref="MaxAttempts"/>, so a typo cannot make a failing
    /// job retry practically forever.</summary>
    public const int MaxAttemptsLimit = 20;

    /// <summary>For EF Core materialization.</summary>
    private BackgroundJob()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>Which handler processes the job, e.g. <c>knowledge.process-document</c>:
    /// lower-case letters and digits, in words separated by <c>.</c> or <c>-</c>.</summary>
    public string Kind { get; private set; } = string.Empty;

    /// <summary>
    /// The handler's input as JSON (<c>jsonb</c>), serialized with
    /// <see cref="JsonSerializerOptions.Web"/>. Keep it to ids and small settings: it must
    /// never contain document content (the row is kept after the job ends).
    /// </summary>
    public string Payload { get; private set; } = "{}";

    public BackgroundJobStatus Status { get; private set; }

    /// <summary>How many times the job has been claimed, the current run included. Also
    /// what tells a runner whose lease expired that another runner has taken the job over
    /// (the column is a concurrency token).</summary>
    public int Attempts { get; private set; }

    public int MaxAttempts { get; private set; }

    /// <summary>Not claimed before this time: the enqueue time, a later schedule, or the
    /// backoff after a retryable failure.</summary>
    public DateTimeOffset RunAfter { get; private set; }

    /// <summary>While <see cref="BackgroundJobStatus.Running"/>: the end of the current
    /// runner's lease, after which the job may be claimed again. Otherwise
    /// <see langword="null"/>.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>The most recent failure's message. Kept after a later attempt succeeds.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the job succeeded or finally failed.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>A new <see cref="BackgroundJobStatus.Queued"/> job.</summary>
    /// <param name="runAfter">Not before this time; defaults to <paramref name="now"/>.</param>
    public static BackgroundJob Create<TPayload>(
        Guid organizationId,
        string kind,
        TPayload payload,
        DateTimeOffset now,
        DateTimeOffset? runAfter = null,
        int maxAttempts = DefaultMaxAttempts)
        where TPayload : notnull
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization id must not be empty.", nameof(organizationId));
        }

        RequireValidKind(kind);

        if (maxAttempts is < 1 or > MaxAttemptsLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, $"Must be 1-{MaxAttemptsLimit}.");
        }

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        if (json == "null")
        {
            throw new ArgumentException("A job payload must not serialize to null.", nameof(payload));
        }

        return new BackgroundJob
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Kind = kind,
            Payload = json,
            Status = BackgroundJobStatus.Queued,
            Attempts = 0,
            MaxAttempts = maxAttempts,
            RunAfter = runAfter ?? now,
            CreatedAt = now,
        };
    }

    /// <summary>Throws unless <paramref name="kind"/> is a valid <see cref="Kind"/>.</summary>
    public static void RequireValidKind(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!IsValidKind(kind))
        {
            throw new ArgumentException(
                $"A job kind must be 1-{KindMaxLength} characters of a-z and 0-9, in words separated by '.' or '-'.",
                nameof(kind));
        }
    }

    public void Succeed(DateTimeOffset now)
    {
        RequireRunning();
        Status = BackgroundJobStatus.Succeeded;
        LockedUntil = null;
        CompletedAt = now;
    }

    /// <summary>A retryable failure: back to the queue until <paramref name="runAfter"/>.</summary>
    public void ScheduleRetry(string error, DateTimeOffset runAfter)
    {
        RequireRunning();
        Status = BackgroundJobStatus.Queued;
        LockedUntil = null;
        RunAfter = runAfter;
        LastError = Truncate(error);
    }

    /// <summary>A permanent failure, or the last allowed attempt failed.</summary>
    public void Fail(string error, DateTimeOffset now)
    {
        RequireRunning();
        Status = BackgroundJobStatus.Failed;
        LockedUntil = null;
        LastError = Truncate(error);
        CompletedAt = now;
    }

    /// <summary>
    /// Gives the job back without it counting as an attempt (its runner is shutting down),
    /// so another runner can take it straight away instead of waiting for the lease.
    /// </summary>
    public void Release(DateTimeOffset now)
    {
        RequireRunning();
        Status = BackgroundJobStatus.Queued;
        LockedUntil = null;
        RunAfter = now;
        Attempts = Math.Max(0, Attempts - 1);
    }

    private static bool IsValidKind(string kind)
    {
        if (kind.Length is 0 or > KindMaxLength)
        {
            return false;
        }

        var previousWasSeparator = true;
        foreach (var character in kind)
        {
            var isSeparator = character is '.' or '-';
            if (isSeparator ? previousWasSeparator : character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }

            previousWasSeparator = isSeparator;
        }

        return !previousWasSeparator;
    }

    private void RequireRunning()
    {
        if (Status != BackgroundJobStatus.Running)
        {
            throw new InvalidOperationException($"Only a running job can change state; this one is {Status}.");
        }
    }

    private static string Truncate(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Length <= LastErrorMaxLength ? error : string.Concat(error.AsSpan(0, LastErrorMaxLength - 1), "…");
    }
}
