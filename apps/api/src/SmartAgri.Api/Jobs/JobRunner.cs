using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Tenancy;
using SmartAgri.Application.Jobs;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Observability;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Jobs;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Jobs;

/// <summary>
/// Claims background jobs one at a time (<see cref="JobClaimer"/>) and runs each through
/// the <see cref="IJobHandler"/> registered for its kind (M2 plan, Slice 4). Singleton and
/// stateless between calls, so several loops (or runners) may call it concurrently.
/// <see cref="JobWorker"/> calls it in production; integration tests turn the worker off
/// and call <see cref="RunUntilIdleAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every step after the claim happens in a fresh dependency-injection scope entered for
/// the job's organization (<see cref="JobOrganizationScope"/>): the handler, the
/// final-failure callback and recording the outcome all read and write through the
/// organization filter and write guard. Outcomes are recorded on a fresh context rather
/// than the handler's, so changes a failed handler left tracked are never saved.
/// </para>
/// <para>
/// Retryable failures (any exception except the permanent ones below) go back to the queue
/// with exponential backoff (<see cref="JobRetryPolicy"/>) until
/// <see cref="BackgroundJob.MaxAttempts"/>; then the job fails. Permanent failures fail it
/// straight away: <see cref="PermanentJobFailure"/>, and
/// <see cref="CrossOrganizationWriteException"/> (a programming error that retrying cannot
/// fix). Recording only succeeds while the job is still this runner's (same attempt, still
/// running); a runner whose lease expired and whose job was claimed again records nothing.
/// </para>
/// </remarks>
public sealed class JobRunner
{
    public const string SpanName = "smartagri.job";

    public const string JobIdTag = "smartagri.job.id";

    public const string KindTag = "smartagri.job.kind";

    public const string AttemptTag = "smartagri.job.attempt";

    public const string OutcomeTag = "smartagri.job.outcome";

    internal const string LeaseExpiredOnLastAttemptError =
        "The job's last allowed attempt did not finish within its lease; the process running it probably stopped.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Dictionary<string, Type> _handlerTypes;
    private readonly string[] _kinds;
    private readonly JobOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<JobRunner> _logger;

    public JobRunner(
        IServiceScopeFactory scopeFactory,
        IEnumerable<JobHandlerRegistration> registrations,
        IOptions<JobOptions> options,
        TimeProvider clock,
        ILogger<JobRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _clock = clock;
        _logger = logger;

        _handlerTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            if (!_handlerTypes.TryAdd(registration.Kind, registration.HandlerType))
            {
                throw new InvalidOperationException($"More than one handler is registered for job kind '{registration.Kind}'.");
            }
        }

        _kinds = [.. _handlerTypes.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>The job kinds this runner claims: those with a registered handler.</summary>
    public IReadOnlyList<string> Kinds => _kinds;

    /// <summary>Claims and processes jobs until none is claimable right now; returns how
    /// many were processed. Jobs scheduled for later (including retries) are left.</summary>
    public async Task<int> RunUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        var processed = 0;
        while (await RunOnceAsync(cancellationToken))
        {
            processed++;
        }

        return processed;
    }

    /// <summary>Claims and processes at most one job; returns whether there was one.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_kinds.Length == 0)
        {
            return false;
        }

        ClaimedJob? claimed;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            claimed = await scope.ServiceProvider.GetRequiredService<JobClaimer>()
                .TryClaimAsync(_kinds, _clock.GetUtcNow(), _options.LeaseDuration, cancellationToken);
        }

        if (claimed is null)
        {
            return false;
        }

        await ProcessAsync(claimed, cancellationToken);
        return true;
    }

    /// <summary>Runs one claimed job and records the outcome, under one
    /// <see cref="SpanName"/> span. Internal so tests can play a runner whose lease expired.</summary>
    internal async Task ProcessAsync(ClaimedJob claimed, CancellationToken cancellationToken)
    {
        var job = new JobContext(
            claimed.Id,
            claimed.OrganizationId,
            claimed.Kind,
            claimed.Payload,
            claimed.Attempts,
            claimed.MaxAttempts);

        using var activity = SmartAgriActivitySource.Instance.StartActivity(SpanName);
        activity?.SetTag(JobIdTag, job.JobId.ToString());
        activity?.SetTag(KindTag, job.Kind);
        activity?.SetTag(AttemptTag, job.Attempt);
        activity?.SetTag(SmartAgriActivitySource.OrganizationIdTag, job.OrganizationId.ToString());

        try
        {
            var outcome = await ExecuteAsync(job, cancellationToken);
            activity?.SetTag(OutcomeTag, OutcomeName(outcome));
            if (outcome is JobOutcome.Failed or JobOutcome.RetryScheduled)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
            }
        }
        catch (Exception exception)
        {
            // The job could not be run or its outcome not recorded (database unreachable,
            // shutdown, misconfiguration). It stays running and is claimed again once its
            // lease expires.
            activity?.SetTag(OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            throw;
        }
    }

    private async Task<JobOutcome> ExecuteAsync(JobContext job, CancellationToken cancellationToken)
    {
        if (job.Attempt > job.MaxAttempts)
        {
            // Claimed again after its last allowed attempt's lease expired: do not run it
            // once more (it may be what keeps stopping the process), fail it.
            _logger.LogError(
                "Background job {JobId} ({Kind}) failed: its last attempt did not finish within the lease.",
                job.JobId,
                job.Kind);
            return await FailAsync(job, LeaseExpiredOnLastAttemptError, cancellationToken);
        }

        Exception? failure = null;
        var cancelled = false;
        await using (var scope = CreateJobScope(job.OrganizationId))
        {
            try
            {
                await ResolveHandler(scope.ServiceProvider, job.Kind).HandleAsync(job, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        if (cancelled)
        {
            // The worker is stopping: hand the job back instead of leaving it locked for
            // the rest of its lease.
            return await TryRecordAsync(job, row => row.Release(_clock.GetUtcNow()), CancellationToken.None)
                ? JobOutcome.Released
                : Superseded(job);
        }

        // Recording is not cancelled by shutdown: the work is done either way.
        if (failure is null)
        {
            return await TryRecordAsync(job, row => row.Succeed(_clock.GetUtcNow()), CancellationToken.None)
                ? JobOutcome.Succeeded
                : Superseded(job);
        }

        var error = failure.Message;
        if (IsPermanent(failure) || job.IsLastAttempt)
        {
            _logger.LogError(
                failure,
                "Background job {JobId} ({Kind}) failed on attempt {Attempt} of {MaxAttempts}.",
                job.JobId,
                job.Kind,
                job.Attempt,
                job.MaxAttempts);
            return await FailAsync(job, error, cancellationToken);
        }

        var runAfter = _clock.GetUtcNow()
            + JobRetryPolicy.Backoff(job.Attempt, _options.RetryBaseDelay, _options.RetryMaxDelay);
        _logger.LogWarning(
            failure,
            "Background job {JobId} ({Kind}) failed on attempt {Attempt} of {MaxAttempts}; retrying after {RunAfter}.",
            job.JobId,
            job.Kind,
            job.Attempt,
            job.MaxAttempts,
            runAfter);
        return await TryRecordAsync(job, row => row.ScheduleRetry(error, runAfter), CancellationToken.None)
            ? JobOutcome.RetryScheduled
            : Superseded(job);
    }

    /// <summary>
    /// Fails the job for good. The handler's <see cref="IJobHandler.OnFinalFailureAsync"/>
    /// and marking the job failed commit in one transaction, and only while the job is
    /// still this runner's; if the callback throws, the job is failed anyway (without the
    /// callback's changes) and the callback's error is appended to the job's last error.
    /// </summary>
    private async Task<JobOutcome> FailAsync(JobContext job, string error, CancellationToken cancellationToken)
    {
        string? callbackError = null;
        await using (var scope = CreateJobScope(job.OrganizationId))
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var row = await FindOwnedAsync(dbContext, job, cancellationToken);
            if (row is null)
            {
                return Superseded(job);
            }

            try
            {
                await ResolveHandler(scope.ServiceProvider, job.Kind).OnFinalFailureAsync(job, error, cancellationToken);
                row.Fail(error, _clock.GetUtcNow());
                await dbContext.SaveChangesAsync(CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
                return JobOutcome.Failed;
            }
            catch (DbUpdateConcurrencyException exception) when (exception.Entries.Any(entry => ReferenceEquals(entry.Entity, row)))
            {
                return Superseded(job);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                _logger.LogError(
                    exception,
                    "The final-failure handling of background job {JobId} ({Kind}) failed.",
                    job.JobId,
                    job.Kind);
                callbackError = exception.Message;
            }
        }

        // Disposing the scope rolled the callback's transaction back; fail the job on its own.
        var combinedError = $"{error} (Final-failure handling also failed: {callbackError})";
        return await TryRecordAsync(job, row => row.Fail(combinedError, _clock.GetUtcNow()), CancellationToken.None)
            ? JobOutcome.Failed
            : Superseded(job);
    }

    /// <summary>Applies <paramref name="change"/> to the job row on a fresh context in the
    /// job's organization; false when the job is no longer this runner's.</summary>
    private async Task<bool> TryRecordAsync(JobContext job, Action<BackgroundJob> change, CancellationToken cancellationToken)
    {
        await using var scope = CreateJobScope(job.OrganizationId);
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await FindOwnedAsync(dbContext, job, cancellationToken);
        if (row is null)
        {
            return false;
        }

        change(row);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>The job row if it is still the attempt this runner claimed.</summary>
    private static async Task<BackgroundJob?> FindOwnedAsync(AppDbContext dbContext, JobContext job, CancellationToken cancellationToken)
    {
        var row = await dbContext.BackgroundJobs.SingleOrDefaultAsync(candidate => candidate.Id == job.JobId, cancellationToken);
        return row is { Status: BackgroundJobStatus.Running } && row.Attempts == job.Attempt ? row : null;
    }

    /// <summary>
    /// A new scope acting for <paramref name="organizationId"/>. Fails closed: if the
    /// scope's <see cref="IOrganizationContext"/> is not that organization (tenancy wiring
    /// changed), nothing runs in it.
    /// </summary>
    private AsyncServiceScope CreateJobScope(Guid organizationId)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            scope.ServiceProvider.GetRequiredService<JobOrganizationScope>().Enter(organizationId);
            if (scope.ServiceProvider.GetRequiredService<IOrganizationContext>().OrganizationId != organizationId)
            {
                throw new InvalidOperationException(
                    "A background job's scope does not act for the job's organization; IOrganizationContext " +
                    "must be resolved through JobOrganizationScope (see AddOrganizationTenancy).");
            }

            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private IJobHandler ResolveHandler(IServiceProvider services, string kind) =>
        _handlerTypes.TryGetValue(kind, out var handlerType)
            ? (IJobHandler)services.GetRequiredService(handlerType)
            : throw new InvalidOperationException($"No handler is registered for job kind '{kind}'.");

    private static bool IsPermanent(Exception exception) =>
        exception is PermanentJobFailure or CrossOrganizationWriteException;

    private JobOutcome Superseded(JobContext job)
    {
        _logger.LogWarning(
            "Background job {JobId} ({Kind}) attempt {Attempt} finished after its lease expired and the job was claimed again; its outcome was discarded.",
            job.JobId,
            job.Kind,
            job.Attempt);
        return JobOutcome.Superseded;
    }

    private static string OutcomeName(JobOutcome outcome) => outcome switch
    {
        JobOutcome.Succeeded => "succeeded",
        JobOutcome.RetryScheduled => "retry-scheduled",
        JobOutcome.Failed => "failed",
        JobOutcome.Released => "released",
        JobOutcome.Superseded => "superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private enum JobOutcome
    {
        Succeeded,
        RetryScheduled,
        Failed,
        Released,

        /// <summary>The job was no longer this runner's; nothing was recorded.</summary>
        Superseded,
    }
}

/// <summary>Which <see cref="IJobHandler"/> type processes jobs of <see cref="Kind"/>; added by
/// <see cref="JobServiceCollectionExtensions.AddJobHandler{THandler}"/>.</summary>
public sealed record JobHandlerRegistration(string Kind, Type HandlerType);
