using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Organizations;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Notifications;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary>
/// Handled 轉真人 notifications are deleted a year after they were handled (#362): by the daily
/// <see cref="HandoffRetentionSweep"/> for every organization whatever its conversation retention, and by
/// the <c>retention-cleanup</c> subcommand for one. Pending ones are never deleted, and another
/// organization's are never touched.
/// </summary>
public sealed partial class OrganizationRetentionTests
{
    [Fact]
    public async Task Handoffs_handled_over_365_days_ago_go_and_recent_or_pending_ones_stay_however_old()
    {
        var org = await CreateOrganizationAsync();
        var asOf = _host.Clock.GetUtcNow();
        // 00:00 of the Taipei day 365 days before today: handled strictly before it has expired.
        var cutoff = RetentionCleanupRules.Cutoff(asOf, VisitorHandoff.HandledRetentionDays, Taipei);
        var expired366 = await SeedHandoffAsync(org, createdAt: asOf.AddDays(-367), handledAt: asOf.AddDays(-366));
        var expiredAtBoundary = await SeedHandoffAsync(org, createdAt: cutoff.AddDays(-1), handledAt: cutoff.AddSeconds(-1));
        var keptAtBoundary = await SeedHandoffAsync(org, createdAt: cutoff.AddDays(-1), handledAt: cutoff);
        var kept364 = await SeedHandoffAsync(org, createdAt: asOf.AddDays(-400), handledAt: asOf.AddDays(-364));
        var pendingForYears = await SeedHandoffAsync(org, createdAt: asOf.AddDays(-1000), handledAt: null);

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var result = await NewService(dbContext, asOf, batchSize: 1).DeleteHandledHandoffsAsync(asOf, CancellationToken);
            (result.Cutoff, result.Count, result.Batches).ShouldBe((cutoff, 2, 3));
        }

        (await HandoffIdsAsync(org)).ShouldBe([keptAtBoundary, kept364, pendingForYears], ignoreOrder: true);
        (await HandoffIdsAsync(org)).ShouldNotContain(expired366);
        (await HandoffIdsAsync(org)).ShouldNotContain(expiredAtBoundary);
        // Content-free rows: no activity, and the conversation retention is still forever (untouched).
        (await ActivitiesAsync(org)).ShouldBeEmpty();
        (await LoadAsync(org)).RetentionDays.ShouldBeNull();

        // Again: nothing more.
        await using (var again = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            (await NewService(again, asOf).DeleteHandledHandoffsAsync(asOf, CancellationToken)).Count.ShouldBe(0);
        }
    }

    [Fact]
    public async Task The_daily_sweep_cleans_every_organization_in_its_own_scope_whatever_its_retention()
    {
        var asOf = _host.Clock.GetUtcNow();
        var forever = await CreateOrganizationAsync();
        var thirtyDays = await CreateOrganizationAsync();
        await MakeCurrentAsync(thirtyDays, 30);
        var seeded = new Dictionary<TestOrganization, (Guid Old, Guid Recent, Guid Pending)>();
        foreach (var org in new[] { forever, thirtyDays })
        {
            seeded[org] = (
                await SeedHandoffAsync(org, asOf.AddDays(-500), asOf.AddDays(-400)),
                await SeedHandoffAsync(org, asOf.AddDays(-500), asOf.AddDays(-10)),
                await SeedHandoffAsync(org, asOf.AddDays(-500), null));
        }

        var sweep = _host.Factory.Services.GetRequiredService<HandoffRetentionSweep>();
        (await sweep.SweepAsync(asOf, CancellationToken)).ShouldBeGreaterThanOrEqualTo(2);

        foreach (var (org, (_, recent, pending)) in seeded)
        {
            (await HandoffIdsAsync(org)).ShouldBe([recent, pending], ignoreOrder: true, org.Organization.Code);
        }

        // Counted with the filter off: nothing of these organizations was moved or lost elsewhere.
        await using var unfiltered = _host.Postgres.CreateDbContext();
        var ids = seeded.Keys.Select(org => org.Organization.Id).ToList();
        var byOrganization = await unfiltered.VisitorHandoffs.IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .Where(handoff => ids.Contains(handoff.OrganizationId))
            .GroupBy(handoff => handoff.OrganizationId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, CancellationToken);
        byOrganization.ShouldBe(new Dictionary<Guid, int> { [forever.Organization.Id] = 2, [thirtyDays.Organization.Id] = 2 }, ignoreOrder: true);
    }

    [Fact]
    public async Task The_subcommand_deletes_its_organizations_old_handled_handoffs_and_never_another_ones()
    {
        var now = _host.Clock.GetUtcNow();
        var cleaned = await CreateOrganizationAsync();
        var neighbour = await CreateOrganizationAsync();
        var old = await SeedHandoffAsync(cleaned, now.AddDays(-400), now.AddDays(-380));
        var pending = await SeedHandoffAsync(cleaned, now.AddDays(-400), null);
        var neighbours = new[]
        {
            await SeedHandoffAsync(neighbour, now.AddDays(-400), now.AddDays(-380)),
            await SeedHandoffAsync(neighbour, now.AddDays(-400), null),
        };

        var command = await RunCommandAsync(_host.Factory.Services, "--organization", cleaned.Organization.Code);

        command.Exit.ShouldBe(RetentionCleanupCommand.ExitSuccess, command.Error);
        command.Output.ShouldContain("永久保存");
        command.Output.ShouldContain("轉真人通知：刪除 1 筆");
        (await HandoffIdsAsync(cleaned)).ShouldBe([pending]);
        (await HandoffIdsAsync(cleaned)).ShouldNotContain(old);
        (await HandoffIdsAsync(neighbour)).ShouldBe(neighbours, ignoreOrder: true);
    }

    [Fact]
    public async Task The_sweep_waits_for_the_next_local_three_oclock_and_then_sweeps()
    {
        var now = _host.Clock.GetUtcNow();
        var org = await CreateOrganizationAsync();
        var old = await SeedHandoffAsync(org, now.AddDays(-400), now.AddDays(-380));
        var kept = await SeedHandoffAsync(org, now.AddDays(-400), now.AddDays(-30));
        var clock = new FirstTimerFiresClock(now);
        var sweep = new HandoffRetentionSweep(
            _host.Factory.Services, Options.Create(new JobOptions { WorkerEnabled = true }), clock, NullLogger<HandoffRetentionSweep>.Instance);

        await sweep.StartAsync(CancellationToken);
        try
        {
            // The second wait starts only after the first sweep finished.
            await clock.SecondTimer.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken);
        }
        finally
        {
            await sweep.StopAsync(CancellationToken);
        }

        // Task.Delay hands the timer whole milliseconds.
        clock.DueTimes[0].ShouldBe(RetentionCleanupRules.NextRunAfter(now, Taipei) - now, TimeSpan.FromMilliseconds(1));
        (await HandoffIdsAsync(org)).ShouldBe([kept]);
        (await HandoffIdsAsync(org)).ShouldNotContain(old);
    }

    [Fact]
    public async Task The_sweep_does_nothing_where_the_worker_does_not_run()
    {
        var now = _host.Clock.GetUtcNow();
        var org = await CreateOrganizationAsync();
        var old = await SeedHandoffAsync(org, now.AddDays(-400), now.AddDays(-380));
        var clock = new FirstTimerFiresClock(now);
        var sweep = new HandoffRetentionSweep(
            _host.Factory.Services, Options.Create(new JobOptions { WorkerEnabled = false }), clock, NullLogger<HandoffRetentionSweep>.Instance);

        await sweep.StartAsync(CancellationToken);
        await (sweep.ExecuteTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken);
        await sweep.StopAsync(CancellationToken);

        clock.DueTimes.ShouldBeEmpty();
        (await HandoffIdsAsync(org)).ShouldBe([old]);
    }

    // --- Handoff helpers -------------------------------------------------------------------------

    /// <summary>A website handoff of the organization's assistant, created at <paramref name="createdAt"/>
    /// and, unless <paramref name="handledAt"/> is <see langword="null"/>, handled by the admin then.</summary>
    private async Task<Guid> SeedHandoffAsync(TestOrganization org, DateTimeOffset createdAt, DateTimeOffset? handledAt)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var handoff = VisitorHandoff.Open(org.Organization.Id, org.AssistantId, VisitorHandoffChannel.Website, createdAt);
        if (handledAt is { } at)
        {
            handoff.MarkHandled(org.Admin.Id, at).ShouldBeTrue();
        }

        dbContext.VisitorHandoffs.Add(handoff);
        await dbContext.SaveChangesAsync(CancellationToken);
        return handoff.Id;
    }

    private async Task<List<Guid>> HandoffIdsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.VisitorHandoffs.AsNoTracking().Select(handoff => handoff.Id).ToListAsync(CancellationToken);
    }

    /// <summary>A fixed "now" whose first timer fires at once (the wait for 03:00 is over) and whose
    /// later timers never fire; records every timer's due time.</summary>
    private sealed class FirstTimerFiresClock(DateTimeOffset now) : TimeProvider
    {
        private readonly Lock _lock = new();

        public List<TimeSpan> DueTimes { get; } = [];

        public TaskCompletionSource SecondTimer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            int count;
            lock (_lock)
            {
                DueTimes.Add(dueTime);
                count = DueTimes.Count;
            }

            if (count == 1)
            {
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
            else
            {
                SecondTimer.TrySetResult();
            }

            return new NeverTimer();
        }

        private sealed class NeverTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
