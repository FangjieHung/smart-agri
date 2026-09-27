using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Application.Jobs;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Jobs;

/// <summary>
/// The sign-in test host (one Postgres container, migrated; the job worker off) plus
/// <see cref="ScriptedJobHandler"/> registered for every <see cref="TestJobKinds"/> kind
/// and a span collector. Tests drive <see cref="JobRunner"/> themselves and move
/// <see cref="AuthHostFixture.Clock"/> forward for backoff and leases.
/// </summary>
/// <remarks>
/// Tests in a class run one after another but share the database and the clock, so each
/// test uses its own organizations, asserts on its own jobs only, and leaves every job it
/// created in a final state (or scheduled far in the future): a later test advancing the
/// clock must not find it claimable.
/// </remarks>
public sealed class JobHostFixture : AuthHostFixture
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(10);

    private WebApplicationFactory<Program>? _jobs;

    public JobProbe Probe { get; } = new();

    public ConcurrentQueue<EndedSpan> Spans { get; } = new();

    public IServiceProvider Services => (_jobs ?? throw new InvalidOperationException("Not initialized.")).Services;

    public JobRunner Runner => Services.GetRequiredService<JobRunner>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        _jobs = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Jobs:LeaseDuration", Lease.ToString());
            builder.UseSetting("Jobs:RetryBaseDelay", RetryBaseDelay.ToString());
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Probe);
                foreach (var kind in TestJobKinds.Registered)
                {
                    services.AddJobHandler<ScriptedJobHandler>(kind);
                }

                services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new SpanCollector(Spans)));
            });
        });

        _ = Services;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_jobs is not null)
        {
            await _jobs.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    /// <summary>A second, independent runner on the same host (its own claims and scopes).</summary>
    public JobRunner CreateRunner() => ActivatorUtilities.CreateInstance<JobRunner>(Services);

    public async Task<BackgroundJob> EnqueueAsync<TPayload>(
        Organization organization,
        string kind,
        TPayload payload,
        DateTimeOffset? runAfter = null,
        int maxAttempts = BackgroundJob.DefaultMaxAttempts)
        where TPayload : notnull
    {
        var job = BackgroundJob.Create(organization.Id, kind, payload, Clock.GetUtcNow(), runAfter, maxAttempts);
        await using var dbContext = Postgres.CreateDbContext(organization.Id);
        dbContext.BackgroundJobs.Add(job);
        await dbContext.SaveChangesAsync();
        return job;
    }

    /// <summary>The job as stored now, read in its own organization.</summary>
    public async Task<BackgroundJob> ReloadAsync(BackgroundJob job)
    {
        await using var dbContext = Postgres.CreateDbContext(job.OrganizationId);
        return await dbContext.BackgroundJobs.AsNoTracking().SingleAsync(candidate => candidate.Id == job.Id);
    }

    private sealed class SpanCollector : BaseProcessor<Activity>
    {
        private readonly ConcurrentQueue<EndedSpan> _sink;

        public SpanCollector(ConcurrentQueue<EndedSpan> sink)
        {
            _sink = sink;
        }

        // Spans end on whichever thread finishes them, so the sink must be thread-safe
        // (the in-memory exporter's plain list is not).
        public override void OnEnd(Activity data) =>
            _sink.Enqueue(new EndedSpan(
                data.Source.Name,
                data.DisplayName,
                data.Status,
                data.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value)));
    }
}

public sealed record EndedSpan(string SourceName, string Name, ActivityStatusCode Status, IReadOnlyDictionary<string, object?> Tags);

/// <summary>Job kinds the test host has a handler for, plus one it has not.</summary>
public static class TestJobKinds
{
    /// <summary>Succeeds.</summary>
    public const string Record = "test.record";

    /// <summary>Succeeds; only the concurrency test uses it.</summary>
    public const string Concurrent = "test.concurrent";

    /// <summary>Throws a retryable error while the attempt is at most the payload's
    /// <see cref="FlakyPayload.Failures"/>.</summary>
    public const string Flaky = "test.flaky";

    /// <summary>Throws <see cref="PermanentJobFailure"/>.</summary>
    public const string Permanent = "test.permanent";

    /// <summary>Records which jobs' organizations it can see, then writes a row of its
    /// job's organization.</summary>
    public const string WriteOwnOrganization = "test.write-own-organization";

    /// <summary>Writes a row of the payload's <see cref="OrganizationPayload.OrganizationId"/>.</summary>
    public const string WriteOtherOrganization = "test.write-other-organization";

    /// <summary>Throws <see cref="PermanentJobFailure"/> on attempt 1 only.</summary>
    public const string FirstAttemptFails = "test.first-attempt-fails";

    /// <summary>Succeeds; only the lease tests use it.</summary>
    public const string Lease = "test.lease";

    /// <summary>Succeeds; only the SKIP LOCKED test uses it.</summary>
    public const string SkipLocked = "test.skip-locked";

    /// <summary>Waits for cancellation if <see cref="JobProbe.BlockUntilCancelled"/> lists
    /// the job (once), otherwise succeeds.</summary>
    public const string BlockOnce = "test.block-once";

    public const string Span = "test.span";

    public const string QueueDepth = "test.queue-depth";

    /// <summary>No handler is registered for this kind.</summary>
    public const string Unhandled = "test.unhandled";

    public static readonly string[] Registered =
    [
        Record, Concurrent, Flaky, Permanent, WriteOwnOrganization, WriteOtherOrganization,
        FirstAttemptFails, Lease, SkipLocked, BlockOnce, Span, QueueDepth,
    ];
}

public sealed record FlakyPayload(int Failures);

public sealed record OrganizationPayload(Guid OrganizationId);

/// <summary>What the handlers saw and did; thread-safe, shared by the whole fixture.</summary>
public sealed class JobProbe
{
    public ConcurrentQueue<HandledJob> Handled { get; } = new();

    public ConcurrentQueue<FinalFailure> FinalFailures { get; } = new();

    public ConcurrentDictionary<Guid, Exception> Thrown { get; } = new();

    public ConcurrentDictionary<Guid, Guid> WrittenActivityIds { get; } = new();

    public ConcurrentDictionary<Guid, IReadOnlyList<Guid>> VisibleJobOrganizations { get; } = new();

    public ConcurrentDictionary<Guid, bool> BlockUntilCancelled { get; } = new();

    public IReadOnlyList<HandledJob> HandledFor(Guid jobId) => [.. Handled.Where(handled => handled.JobId == jobId)];

    public IReadOnlyList<FinalFailure> FinalFailuresFor(Guid jobId) => [.. FinalFailures.Where(failure => failure.JobId == jobId)];
}

/// <param name="ScopeOrganizationId">The organization of the handler's own
/// <see cref="AppDbContext"/>.</param>
public sealed record HandledJob(Guid JobId, int Attempt, Guid? ScopeOrganizationId);

public sealed record FinalFailure(Guid JobId, int Attempt, string Error, Guid? ScopeOrganizationId);

/// <summary>One handler for every test kind; what it does depends on the kind.</summary>
public sealed class ScriptedJobHandler : IJobHandler
{
    private readonly JobProbe _probe;
    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _clock;

    public ScriptedJobHandler(JobProbe probe, AppDbContext dbContext, TimeProvider clock)
    {
        _probe = probe;
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        _probe.Handled.Enqueue(new HandledJob(job.JobId, job.Attempt, _dbContext.OrganizationContext.OrganizationId));

        switch (job.Kind)
        {
            case TestJobKinds.Flaky:
                if (job.Attempt <= job.ReadPayload<FlakyPayload>().Failures)
                {
                    throw new InvalidOperationException($"transient failure on attempt {job.Attempt}");
                }

                break;

            case TestJobKinds.Permanent:
                throw new PermanentJobFailure("the document is encrypted");

            case TestJobKinds.FirstAttemptFails when job.Attempt == 1:
                throw new PermanentJobFailure("first attempt failed");

            case TestJobKinds.WriteOwnOrganization:
                _probe.VisibleJobOrganizations[job.JobId] =
                    await _dbContext.BackgroundJobs.Select(row => row.OrganizationId).Distinct().ToListAsync(cancellationToken);
                await WriteActivityAsync(job, job.OrganizationId, cancellationToken);
                break;

            case TestJobKinds.WriteOtherOrganization:
                await WriteActivityAsync(job, job.ReadPayload<OrganizationPayload>().OrganizationId, cancellationToken);
                break;

            case TestJobKinds.BlockOnce when _probe.BlockUntilCancelled.TryRemove(job.JobId, out _):
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break;

            default:
                await Task.Yield();
                break;
        }
    }

    public Task OnFinalFailureAsync(JobContext job, string error, CancellationToken cancellationToken)
    {
        _probe.FinalFailures.Enqueue(new FinalFailure(job.JobId, job.Attempt, error, _dbContext.OrganizationContext.OrganizationId));
        return Task.CompletedTask;
    }

    /// <summary>Adds a knowledge activity row of <paramref name="organizationId"/> (it only
    /// needs the organization to exist).</summary>
    private async Task WriteActivityAsync(JobContext job, Guid organizationId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var knowledgeBase = KnowledgeBase.Create(organizationId, Guid.NewGuid(), "job test", string.Empty, now);
        var activity = KnowledgeActivity.KnowledgeBaseCreated(knowledgeBase, Guid.NewGuid(), now);
        _probe.WrittenActivityIds[job.JobId] = activity.Id;

        _dbContext.KnowledgeActivities.Add(activity);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _probe.Thrown[job.JobId] = exception;
            throw;
        }
    }
}
