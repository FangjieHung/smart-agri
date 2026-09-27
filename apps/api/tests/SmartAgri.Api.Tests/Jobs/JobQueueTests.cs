using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Observability;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Jobs;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Jobs;

/// <summary>
/// The PostgreSQL job queue against real PostgreSQL (M2 plan, Slice 4 acceptance): claiming
/// under concurrency, retries and backoff, permanent failures, leases, and that handlers
/// run inside their job's organization.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class JobQueueTests : IClassFixture<JobHostFixture>
{
    private readonly JobHostFixture _host;

    public JobQueueTests(JobHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private JobProbe Probe => _host.Probe;

    [Fact]
    public async Task Two_concurrent_runners_process_each_of_100_jobs_exactly_once()
    {
        var organization = await _host.CreateOrganizationAsync();
        var jobs = new List<BackgroundJob>();
        for (var index = 0; index < 100; index++)
        {
            jobs.Add(await _host.EnqueueAsync(organization, TestJobKinds.Concurrent, new { index }));
        }

        var runners = new[] { _host.Runner, _host.CreateRunner() };
        var processed = await Task.WhenAll(runners.Select(runner => Task.Run(() => runner.RunUntilIdleAsync(CancellationToken))));

        var ids = jobs.Select(job => job.Id).ToHashSet();
        var handled = Probe.Handled.Where(entry => ids.Contains(entry.JobId)).ToList();
        handled.Count.ShouldBe(100);
        handled.Select(entry => entry.JobId).Distinct().Count().ShouldBe(100);
        handled.ShouldAllBe(entry => entry.Attempt == 1 && entry.ScopeOrganizationId == organization.Id);
        processed.ShouldAllBe(count => count > 0, "both runners should have claimed jobs");

        foreach (var job in jobs)
        {
            var stored = await _host.ReloadAsync(job);
            stored.Status.ShouldBe(BackgroundJobStatus.Succeeded);
            stored.Attempts.ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_job_failing_twice_with_a_retryable_error_succeeds_on_attempt_three_and_keeps_the_last_error()
    {
        var organization = await _host.CreateOrganizationAsync();
        var job = await _host.EnqueueAsync(organization, TestJobKinds.Flaky, new FlakyPayload(Failures: 2));

        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        var afterFirst = await _host.ReloadAsync(job);
        afterFirst.Status.ShouldBe(BackgroundJobStatus.Queued);
        afterFirst.Attempts.ShouldBe(1);
        afterFirst.LastError.ShouldBe("transient failure on attempt 1");
        afterFirst.LockedUntil.ShouldBeNull();
        afterFirst.RunAfter.ShouldBe(_host.Clock.GetUtcNow() + JobHostFixture.RetryBaseDelay, TimeSpan.FromSeconds(1));

        // Not due yet: nothing happens.
        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        (await _host.ReloadAsync(job)).Attempts.ShouldBe(1);

        _host.Clock.Advance(JobHostFixture.RetryBaseDelay);
        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        var afterSecond = await _host.ReloadAsync(job);
        afterSecond.Status.ShouldBe(BackgroundJobStatus.Queued);
        afterSecond.Attempts.ShouldBe(2);
        afterSecond.RunAfter.ShouldBe(
            _host.Clock.GetUtcNow() + (2 * JobHostFixture.RetryBaseDelay),
            TimeSpan.FromSeconds(1),
            "the backoff doubles");

        _host.Clock.Advance(2 * JobHostFixture.RetryBaseDelay);
        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        var final = await _host.ReloadAsync(job);
        final.Status.ShouldBe(BackgroundJobStatus.Succeeded);
        final.Attempts.ShouldBe(3);
        final.LastError.ShouldBe("transient failure on attempt 2");
        final.CompletedAt.ShouldNotBeNull();
        Probe.HandledFor(job.Id).Select(entry => entry.Attempt).ShouldBe([1, 2, 3]);
        Probe.FinalFailuresFor(job.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_job_whose_last_attempt_fails_is_failed_and_its_handler_notified_once()
    {
        var organization = await _host.CreateOrganizationAsync();
        var job = await _host.EnqueueAsync(organization, TestJobKinds.Flaky, new FlakyPayload(Failures: 99), maxAttempts: 2);

        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        _host.Clock.Advance(JobHostFixture.RetryBaseDelay);
        await _host.Runner.RunUntilIdleAsync(CancellationToken);

        var stored = await _host.ReloadAsync(job);
        stored.Status.ShouldBe(BackgroundJobStatus.Failed);
        stored.Attempts.ShouldBe(2);
        stored.LastError.ShouldBe("transient failure on attempt 2");
        stored.CompletedAt.ShouldNotBeNull();
        Probe.FinalFailuresFor(job.Id).ShouldHaveSingleItem()
            .ShouldBe(new FinalFailure(job.Id, 2, "transient failure on attempt 2", organization.Id));

        _host.Clock.Advance(TimeSpan.FromHours(1));
        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        Probe.HandledFor(job.Id).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_permanent_failure_fails_the_job_after_exactly_one_attempt()
    {
        var organization = await _host.CreateOrganizationAsync();
        var job = await _host.EnqueueAsync(organization, TestJobKinds.Permanent, new { }, maxAttempts: 5);

        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        _host.Clock.Advance(TimeSpan.FromHours(1));
        await _host.Runner.RunUntilIdleAsync(CancellationToken);

        var stored = await _host.ReloadAsync(job);
        stored.Status.ShouldBe(BackgroundJobStatus.Failed);
        stored.Attempts.ShouldBe(1);
        stored.LastError.ShouldBe("the document is encrypted");
        Probe.HandledFor(job.Id).ShouldHaveSingleItem();
        Probe.FinalFailuresFor(job.Id).ShouldHaveSingleItem().Error.ShouldBe("the document is encrypted");
    }

    [Fact]
    public async Task A_handler_reads_and_writes_only_within_its_jobs_organization()
    {
        var organizationA = await _host.CreateOrganizationAsync("組織 A");
        var organizationB = await _host.CreateOrganizationAsync("組織 B");
        await _host.EnqueueAsync(organizationB, TestJobKinds.Record, new { }, runAfter: _host.Clock.GetUtcNow().AddYears(10));
        var job = await _host.EnqueueAsync(organizationA, TestJobKinds.WriteOwnOrganization, new { });

        await _host.Runner.RunUntilIdleAsync(CancellationToken);

        (await _host.ReloadAsync(job)).Status.ShouldBe(BackgroundJobStatus.Succeeded);
        Probe.HandledFor(job.Id).ShouldHaveSingleItem().ScopeOrganizationId.ShouldBe(organizationA.Id);

        // Other organizations' jobs (B's, and every other test's) are in the table but not visible.
        Probe.VisibleJobOrganizations[job.Id].ShouldBe([organizationA.Id]);

        await using var dbContext = _host.Postgres.CreateDbContext(organizationA.Id);
        (await dbContext.KnowledgeActivities.CountAsync(activity => activity.Id == Probe.WrittenActivityIds[job.Id], CancellationToken))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Writing_another_organizations_data_throws_CrossOrganizationWriteException_and_fails_the_job_without_retry()
    {
        var organizationA = await _host.CreateOrganizationAsync("組織 A");
        var organizationB = await _host.CreateOrganizationAsync("組織 B");
        var job = await _host.EnqueueAsync(
            organizationA,
            TestJobKinds.WriteOtherOrganization,
            new OrganizationPayload(organizationB.Id),
            maxAttempts: 5);

        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        _host.Clock.Advance(TimeSpan.FromHours(1));
        await _host.Runner.RunUntilIdleAsync(CancellationToken);

        var thrown = Probe.Thrown[job.Id].ShouldBeOfType<CrossOrganizationWriteException>();
        var stored = await _host.ReloadAsync(job);
        stored.Status.ShouldBe(BackgroundJobStatus.Failed, "a programming error is not retried");
        stored.Attempts.ShouldBe(1);
        stored.LastError.ShouldBe(thrown.Message);
        Probe.FinalFailuresFor(job.Id).ShouldHaveSingleItem().ScopeOrganizationId.ShouldBe(organizationA.Id);

        await using var dbContextB = _host.Postgres.CreateDbContext(organizationB.Id);
        (await dbContextB.KnowledgeActivities.CountAsync(activity => activity.Id == Probe.WrittenActivityIds[job.Id], CancellationToken))
            .ShouldBe(0);
    }

    [Fact]
    public async Task A_running_job_whose_lease_expired_is_claimed_by_another_runner_and_the_first_runners_outcome_is_discarded()
    {
        var organization = await _host.CreateOrganizationAsync();
        var job = await _host.EnqueueAsync(organization, TestJobKinds.FirstAttemptFails, new { });

        // Runner 1 claims the job, then its process "stops" before finishing.
        var staleClaim = await ClaimDirectlyAsync(TestJobKinds.FirstAttemptFails);
        staleClaim.ShouldNotBeNull().Id.ShouldBe(job.Id);

        // While the lease holds, nobody else takes it.
        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        var locked = await _host.ReloadAsync(job);
        locked.Status.ShouldBe(BackgroundJobStatus.Running);
        locked.Attempts.ShouldBe(1);
        Probe.HandledFor(job.Id).ShouldBeEmpty();

        _host.Clock.Advance(JobHostFixture.Lease + TimeSpan.FromSeconds(1));
        await _host.CreateRunner().RunUntilIdleAsync(CancellationToken);

        var reclaimed = await _host.ReloadAsync(job);
        reclaimed.Status.ShouldBe(BackgroundJobStatus.Succeeded);
        reclaimed.Attempts.ShouldBe(2);
        Probe.HandledFor(job.Id).Select(entry => entry.Attempt).ShouldBe([2]);

        // Runner 1 comes back and finishes its (failing) attempt 1: nothing is recorded,
        // and the handler is not told the job failed.
        await _host.Runner.ProcessAsync(staleClaim, CancellationToken);
        var after = await _host.ReloadAsync(job);
        after.Status.ShouldBe(BackgroundJobStatus.Succeeded);
        after.Attempts.ShouldBe(2);
        after.LastError.ShouldBeNull();
        Probe.FinalFailuresFor(job.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_job_whose_last_attempts_lease_expired_is_failed_without_running_it_again()
    {
        var organization = await _host.CreateOrganizationAsync();
        var job = await _host.EnqueueAsync(organization, TestJobKinds.Lease, new { }, maxAttempts: 1);
        (await ClaimDirectlyAsync(TestJobKinds.Lease)).ShouldNotBeNull().Id.ShouldBe(job.Id);

        _host.Clock.Advance(JobHostFixture.Lease + TimeSpan.FromSeconds(1));
        await _host.Runner.RunUntilIdleAsync(CancellationToken);

        var stored = await _host.ReloadAsync(job);
        stored.Status.ShouldBe(BackgroundJobStatus.Failed);
        stored.LastError.ShouldBe(JobRunner.LeaseExpiredOnLastAttemptError);
        Probe.HandledFor(job.Id).ShouldBeEmpty();
        Probe.FinalFailuresFor(job.Id).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_job_locked_by_another_transaction_is_skipped_rather_than_waited_for()
    {
        var organization = await _host.CreateOrganizationAsync();
        var now = _host.Clock.GetUtcNow();
        var first = await _host.EnqueueAsync(organization, TestJobKinds.SkipLocked, new { }, runAfter: now.AddMinutes(-2));
        var second = await _host.EnqueueAsync(organization, TestJobKinds.SkipLocked, new { }, runAfter: now.AddMinutes(-1));

        ClaimedJob? claimed;
        await using (var lockingContext = _host.Postgres.CreateDbContext(organization.Id))
        await using (var transaction = await lockingContext.Database.BeginTransactionAsync(CancellationToken))
        {
            await lockingContext.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM "BackgroundJobs" WHERE "Id" = {first.Id} FOR UPDATE""",
                CancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            claimed = await ClaimDirectlyAsync(TestJobKinds.SkipLocked, timeout.Token);
        }

        claimed.ShouldNotBeNull().Id.ShouldBe(second.Id);

        // Finish both so nothing is left running.
        await _host.Runner.ProcessAsync(claimed, CancellationToken);
        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        (await _host.ReloadAsync(first)).Status.ShouldBe(BackgroundJobStatus.Succeeded);
        (await _host.ReloadAsync(second)).Status.ShouldBe(BackgroundJobStatus.Succeeded);
    }

    [Fact]
    public async Task Jobs_are_not_claimed_before_their_run_after_or_without_a_handler_for_their_kind()
    {
        var organization = await _host.CreateOrganizationAsync();
        var later = await _host.EnqueueAsync(organization, TestJobKinds.Record, new { }, runAfter: _host.Clock.GetUtcNow().AddYears(10));
        var unhandled = await _host.EnqueueAsync(organization, TestJobKinds.Unhandled, new { });

        await _host.Runner.RunUntilIdleAsync(CancellationToken);

        _host.Runner.Kinds.ShouldNotContain(TestJobKinds.Unhandled);
        foreach (var job in new[] { later, unhandled })
        {
            var stored = await _host.ReloadAsync(job);
            stored.Status.ShouldBe(BackgroundJobStatus.Queued);
            stored.Attempts.ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_job_interrupted_by_shutdown_goes_back_to_the_queue_without_using_an_attempt()
    {
        var organization = await _host.CreateOrganizationAsync();
        var job = await _host.EnqueueAsync(organization, TestJobKinds.BlockOnce, new { });
        Probe.BlockUntilCancelled[job.Id] = true;

        using var stopping = new CancellationTokenSource();
        var run = _host.Runner.RunOnceAsync(stopping.Token);
        await WaitUntilAsync(() => Probe.HandledFor(job.Id).Count == 1);
        await stopping.CancelAsync();
        (await run).ShouldBeTrue();

        var released = await _host.ReloadAsync(job);
        released.Status.ShouldBe(BackgroundJobStatus.Queued);
        released.Attempts.ShouldBe(0);
        released.LockedUntil.ShouldBeNull();

        await _host.Runner.RunUntilIdleAsync(CancellationToken);
        var stored = await _host.ReloadAsync(job);
        stored.Status.ShouldBe(BackgroundJobStatus.Succeeded);
        stored.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Each_job_run_produces_a_smartagri_job_span_tagged_with_its_organization()
    {
        var organization = await _host.CreateOrganizationAsync();
        var job = await _host.EnqueueAsync(organization, TestJobKinds.Span, new { });

        await _host.Runner.RunUntilIdleAsync(CancellationToken);

        await WaitUntilAsync(() => SpansFor(job).Count > 0);
        var span = SpansFor(job).ShouldHaveSingleItem();
        span.SourceName.ShouldBe(SmartAgriActivitySource.Name);
        span.Name.ShouldBe(JobRunner.SpanName);
        span.Tags[SmartAgriActivitySource.OrganizationIdTag].ShouldBe(organization.Id.ToString());
        span.Tags[JobRunner.KindTag].ShouldBe(TestJobKinds.Span);
        span.Tags[JobRunner.AttemptTag].ShouldBe(1);
        span.Tags[JobRunner.OutcomeTag].ShouldBe("succeeded");
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task The_queue_depth_gauge_reports_queued_jobs_per_kind_without_organization_details()
    {
        var organizationA = await _host.CreateOrganizationAsync("組織 A");
        var organizationB = await _host.CreateOrganizationAsync("組織 B");
        var farFuture = _host.Clock.GetUtcNow().AddYears(10);
        await _host.EnqueueAsync(organizationA, TestJobKinds.QueueDepth, new { }, runAfter: farFuture);
        await _host.EnqueueAsync(organizationA, TestJobKinds.QueueDepth, new { }, runAfter: farFuture);
        await _host.EnqueueAsync(organizationB, TestJobKinds.QueueDepth, new { }, runAfter: farFuture);

        var metrics = _host.Services.GetRequiredService<JobQueueMetrics>();
        await metrics.RefreshAsync(CancellationToken);

        var meterFactory = _host.Services.GetRequiredService<IMeterFactory>();
        var measurements = new List<(int Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == JobQueueMetrics.QueuedJobsInstrument && ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) => measurements.Add((value, tags.ToArray())));
        listener.Start();
        listener.RecordObservableInstruments();

        measurements.ShouldAllBe(measurement => measurement.Tags.Length == 1 && measurement.Tags[0].Key == JobRunner.KindTag);
        measurements.Single(measurement => (string?)measurement.Tags[0].Value == TestJobKinds.QueueDepth).Value.ShouldBe(3);
        _host.Runner.Kinds.ShouldAllBe(kind => measurements.Any(measurement => (string?)measurement.Tags[0].Value == kind));
    }

    [Fact]
    public void The_worker_is_off_in_the_integration_test_host()
    {
        _host.Services.GetRequiredService<IOptions<JobOptions>>().Value.WorkerEnabled.ShouldBeFalse();
    }

    private IReadOnlyList<EndedSpan> SpansFor(BackgroundJob job) =>
        [.. _host.Spans.Where(span => span.Tags.TryGetValue(JobRunner.JobIdTag, out var id) && (string?)id == job.Id.ToString())];

    private async Task<ClaimedJob?> ClaimDirectlyAsync(string kind, CancellationToken? cancellationToken = null)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobClaimer>()
            .TryClaimAsync([kind], _host.Clock.GetUtcNow(), JobHostFixture.Lease, cancellationToken ?? CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, CancellationToken);
        }

        condition().ShouldBeTrue();
    }
}
