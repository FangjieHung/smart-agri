using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Organizations;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Organizations;
using SmartAgri.Domain.Reports;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary>The daily cleanup itself: what it deletes and keeps, its calendar, its batches, its activity,
/// the job chain, the reconciler and the <c>retention-cleanup</c> subcommand (issue #241).</summary>
public sealed partial class OrganizationRetentionTests
{
    /// <summary>2026-03-01 17:00 UTC, already 2026-03-02 01:00 in Taipei.</summary>
    private static readonly DateTimeOffset AcrossTheDateLine = new(2026, 3, 1, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Expired_threads_go_whole_with_their_messages_and_citations_and_the_rest_stay_whole()
    {
        var org = await CreateOrganizationAsync();
        await MakeCurrentAsync(org, 30);
        var now = AcrossTheDateLine;
        var expired = await SeedThreadAsync(org, now.AddDays(-45));
        // Started long ago, but its last message is recent: kept with every message.
        var ongoing = await SeedThreadAsync(org, now.AddDays(-2), firstMessageAt: now.AddDays(-100));

        var result = await RunCleanupAsync(org, now);

        (result.Days, result.ThreadCount).ShouldBe(((int?)30, 1));
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.ChatThreads.AnyAsync(thread => thread.Id == expired.ThreadId, CancellationToken)).ShouldBeFalse();
        (await dbContext.ChatMessages.CountAsync(message => message.ThreadId == expired.ThreadId, CancellationToken)).ShouldBe(0);
        (await dbContext.ChatMessageCitations.CountAsync(citation => expired.MessageIds.Contains(citation.MessageId), CancellationToken)).ShouldBe(0);

        (await dbContext.ChatThreads.AnyAsync(thread => thread.Id == ongoing.ThreadId, CancellationToken)).ShouldBeTrue();
        (await dbContext.ChatMessages.CountAsync(message => message.ThreadId == ongoing.ThreadId, CancellationToken)).ShouldBe(2);
        (await dbContext.ChatMessageCitations.CountAsync(citation => ongoing.MessageIds.Contains(citation.MessageId), CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Answer_outcomes_go_by_their_own_time_on_every_channel_including_the_website()
    {
        var org = await CreateOrganizationAsync();
        await MakeCurrentAsync(org, 30);
        var now = AcrossTheDateLine;
        var old = now.AddDays(-45);
        var expired = new List<Guid>();
        foreach (var channel in Enum.GetValues<AnswerOutcomeChannel>())
        {
            expired.Add(await SeedOutcomeAsync(org, channel, old));
        }

        var recentWebsite = await SeedOutcomeAsync(org, AnswerOutcomeChannel.Website, now.AddDays(-3));
        // A thread that is kept does not keep its old outcomes: they record no thread.
        await SeedThreadAsync(org, now.AddDays(-1), firstMessageAt: old);

        var result = await RunCleanupAsync(org, now);

        (result.ThreadCount, result.AnswerOutcomeCount).ShouldBe((0, expired.Count));
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await dbContext.AnswerOutcomes.Select(outcome => outcome.Id).ToListAsync(CancellationToken)).ShouldBe([recentWebsite]);
        var activity = (await ActivitiesAsync(org)).ShouldHaveSingleItem();
        using var detail = JsonDocument.Parse(activity.Detail!);
        (detail.RootElement.GetProperty("threadCount").GetInt32(), detail.RootElement.GetProperty("answerOutcomeCount").GetInt32())
            .ShouldBe((0, expired.Count));
    }

    [Fact]
    public async Task Usage_records_handoff_copies_reports_database_records_and_test_runs_are_untouched()
    {
        var org = await CreateOrganizationAsync();
        await MakeCurrentAsync(org, 30);
        var now = AcrossTheDateLine;
        var old = now.AddDays(-400);
        await SeedThreadAsync(org, old);

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var assistant = await dbContext.Assistants.SingleAsync(candidate => candidate.Id == org.AssistantId, CancellationToken);
            dbContext.ModelInvocations.Add(ModelInvocation.Record(
                org.Organization.Id, org.Internal.Id, org.AssistantId, ModelInvocationPurpose.GenerateAnswer, "Fake", AuthHostFixture.ChatModel, 10, 20, 5, true, old));
            var (issue, created) = AssistantIssue.OpenFromHandoff(assistant, org.Internal.Id, "退貨要多久？", "七天內可以退貨。", false, null, old);
            dbContext.AssistantIssues.Add(issue);
            dbContext.AssistantIssueEvents.Add(created);
            dbContext.AssistantTestRuns.Add(AssistantTestRun.Queue(org.Organization.Id, org.AssistantId, AssistantTestRunTrigger.Manual, old));

            var database = Database.Create(org.Organization.Id, org.Admin.Id, "回報資料庫", "測試", DatabaseTemplateId.Blank, old);
            var version = DatabaseFormVersion.Create(
                database, 1, [new DatabaseFormField("field-note", "備註", DatabaseFieldType.Text, false, [], null, string.Empty)], org.Admin.Id, old);
            dbContext.Databases.Add(database);
            dbContext.DatabaseFormVersions.Add(version);
            dbContext.DatabaseSubmissions.Add(DatabaseSubmission.Create(
                database, version, org.Internal.Id, Guid.NewGuid(), DatabaseSubmissionSource.FormLink,
                new DatabaseConsentTerms("回報資料庫", "測試", "保存商行（回報資料庫）", [], "請勿填寫敏感資料。"), old));
            var day = DateOnly.FromDateTime(old.UtcDateTime);
            dbContext.DatabaseReports.Add(DatabaseReport.Skipped(
                org.Organization.Id, database.Id, org.AssistantId, assistant.Name, ReportFrequency.Weekly, day, day.AddDays(6),
                ReportSkipReason.NotConnected, old));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var before = await CountOthersAsync(org);
        before.ShouldAllBe(count => count > 0);

        (await RunCleanupAsync(org, now)).ThreadCount.ShouldBe(1);

        (await CountOthersAsync(org)).ShouldBe(before);
    }

    [Fact]
    public async Task The_day_boundary_is_the_statistics_time_zones_not_utcs()
    {
        // As of 2026-03-02 01:00 in Taipei (still 03-01 in UTC), 30 days: the cutoff is 01-31 00:00
        // +08 = 01-30 16:00 UTC. Counted in UTC days it would be 01-30 00:00 UTC.
        var now = AcrossTheDateLine;
        var taipeiCutoff = new DateTimeOffset(2026, 1, 30, 16, 0, 0, TimeSpan.Zero);

        foreach (var (zone, expected) in new[] { ("Asia/Taipei", new[] { true, true, false, false }), ("UTC", new[] { false, false, false, false }) })
        {
            var org = await CreateOrganizationAsync();
            await MakeCurrentAsync(org, 30);
            // 18:00 on 01-30 in Taipei: expired there, kept by UTC days (after 01-30 00:00 UTC).
            var evening = await SeedThreadAsync(org, new DateTimeOffset(2026, 1, 30, 10, 0, 0, TimeSpan.Zero));
            var lastSecond = await SeedThreadAsync(org, taipeiCutoff.AddSeconds(-1));
            var atCutoff = await SeedThreadAsync(org, taipeiCutoff);
            var nextMorning = await SeedThreadAsync(org, new DateTimeOffset(2026, 1, 31, 2, 0, 0, TimeSpan.Zero));
            var outcomeEvening = await SeedOutcomeAsync(org, AnswerOutcomeChannel.Website, new DateTimeOffset(2026, 1, 30, 10, 0, 0, TimeSpan.Zero));
            var outcomeAtCutoff = await SeedOutcomeAsync(org, AnswerOutcomeChannel.Chat, taipeiCutoff);

            var result = await RunCleanupAsync(org, now, zone);

            result.Cutoff.ShouldBe(zone == "UTC" ? new DateTimeOffset(2026, 1, 30, 0, 0, 0, TimeSpan.Zero) : taipeiCutoff, zone);
            var deleted = new List<bool>();
            foreach (var thread in new[] { evening, lastSecond, atCutoff, nextMorning })
            {
                deleted.Add(!await ThreadExistsAsync(org, thread.ThreadId));
            }

            string.Join(",", deleted).ShouldBe(string.Join(",", expected), zone);
            await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
            (await dbContext.AnswerOutcomes.AnyAsync(outcome => outcome.Id == outcomeEvening, CancellationToken)).ShouldBe(zone == "UTC", zone);
            (await dbContext.AnswerOutcomes.AnyAsync(outcome => outcome.Id == outcomeAtCutoff, CancellationToken)).ShouldBeTrue(zone);
        }
    }

    [Fact]
    public async Task It_deletes_in_batches_writes_one_summary_and_nothing_when_there_is_nothing_to_delete()
    {
        var org = await CreateOrganizationAsync();
        await MakeCurrentAsync(org, 30);
        var now = AcrossTheDateLine;
        for (var i = 0; i < 5; i++)
        {
            await SeedThreadAsync(org, now.AddDays(-40 - i));
        }

        for (var i = 0; i < 3; i++)
        {
            await SeedOutcomeAsync(org, AnswerOutcomeChannel.Chat, now.AddDays(-50 - i));
        }

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            NewService(dbContext, now).BatchSize.ShouldBe(1000);
        }

        // Batches of 2: threads 2 + 2 + 1, outcomes 2 + 1.
        var result = await RunCleanupAsync(org, now, batchSize: 2);

        (result.ThreadCount, result.AnswerOutcomeCount, result.Batches).ShouldBe((5, 3, 5));
        var summary = (await ActivitiesAsync(org)).ShouldHaveSingleItem();
        (summary.Action, summary.ActorAccountId, summary.At).ShouldBe((OrganizationActivityAction.RetentionCleanup, (Guid?)null, now));
        using (var detail = JsonDocument.Parse(summary.Detail!))
        {
            detail.RootElement.GetProperty("days").GetInt32().ShouldBe(30);
            detail.RootElement.GetProperty("cutoff").GetDateTimeOffset().ShouldBe(RetentionCleanupRules.Cutoff(now, 30, Taipei));
            (detail.RootElement.GetProperty("threadCount").GetInt32(), detail.RootElement.GetProperty("answerOutcomeCount").GetInt32()).ShouldBe((5, 3));
        }

        // Nothing left: no second activity.
        var again = await RunCleanupAsync(org, now.AddDays(1));
        (again.ThreadCount, again.AnswerOutcomeCount).ShouldBe((0, 0));
        (await ActivitiesAsync(org)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_job_delivered_twice_deletes_once_and_queues_one_next_job()
    {
        var org = await CreateOrganizationAsync();
        await MakeCurrentAsync(org, 30);
        var job = await StartChainAsync(org);
        var runAt = RunAtOf(job);
        var runTime = runAt.AddMinutes(1);
        var first = await SeedThreadAsync(org, runTime.AddDays(-40));

        await HandleAsync(org, job, runTime);

        (await ThreadExistsAsync(org, first.ThreadId)).ShouldBeFalse();
        var next = runAt.AddDays(1);
        (await LoadAsync(org)).RetentionCleanupNextRunAt.ShouldBe(next);
        (await RetentionJobsAsync(org)).Select(RunAtOf).ShouldBe([runAt, next]);

        // The same job again (its runner lost the lease, say): nothing deleted, nothing queued.
        var second = await SeedThreadAsync(org, runTime.AddDays(-40));
        await HandleAsync(org, job, runTime.AddMinutes(5));

        (await ThreadExistsAsync(org, second.ThreadId)).ShouldBeTrue();
        (await LoadAsync(org)).RetentionCleanupNextRunAt.ShouldBe(next);
        (await RetentionJobsAsync(org)).Count.ShouldBe(2);
        (await ActivitiesAsync(org)).Count(activity => activity.Action == OrganizationActivityAction.RetentionCleanup).ShouldBe(1);
    }

    [Fact]
    public async Task A_late_job_queues_the_three_oclock_after_now_and_forever_ends_the_chain()
    {
        var org = await CreateOrganizationAsync();
        await MakeCurrentAsync(org, 30);
        var job = await StartChainAsync(org);
        var runAt = RunAtOf(job);

        // Three days late: one cleanup, the next run is the coming 03:00, not the missed ones.
        var late = runAt.AddDays(3).AddHours(5);
        await HandleAsync(org, job, late);
        var next = (await LoadAsync(org)).RetentionCleanupNextRunAt.ShouldNotBeNull();
        next.ShouldBe(RetentionCleanupRules.NextRunAfter(late, Taipei));
        next.ShouldBe(runAt.AddDays(4));

        // Back to forever: the next run ends the chain and queues nothing; a later number starts a new one.
        await SetForeverAsync(org);
        var nextJob = (await RetentionJobsAsync(org)).Last();
        await HandleAsync(org, nextJob, next.AddMinutes(1));
        (await LoadAsync(org)).RetentionCleanupNextRunAt.ShouldBeNull();
        (await RetentionJobsAsync(org)).Count.ShouldBe(2);

        await MakeCurrentAsync(org, 90);
        await StartChainAsync(org);
        (await RetentionJobsAsync(org)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_job_runs_through_the_queue_as_the_registered_kind()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        (await admin.Spa.PutAsync(Path, admin.Token, new { days = 30, revision = 0 })).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var job = (await RetentionJobsAsync(org)).ShouldHaveSingleItem();
        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            await dbContext.BackgroundJobs.Where(candidate => candidate.Id == job.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.RunAfter, DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken);
        }

        await _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

        var jobs = await RetentionJobsAsync(org);
        jobs.Select(candidate => candidate.Status).ShouldBe([BackgroundJobStatus.Succeeded, BackgroundJobStatus.Queued]);
        RunAtOf(jobs[1]).ShouldBe(RunAtOf(job).AddDays(1));
        (await LoadAsync(org)).RetentionCleanupNextRunAt.ShouldBe(RunAtOf(job).AddDays(1));
    }

    [Fact]
    public async Task The_reconciler_requeues_broken_chains_once()
    {
        var broken = await CreateOrganizationAsync();
        await MakeCurrentAsync(broken, 30);
        var failed = await StartChainAsync(broken);
        await using (var dbContext = _host.Postgres.CreateDbContext(broken.Organization.Id))
        {
            await dbContext.BackgroundJobs.Where(candidate => candidate.Id == failed.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, BackgroundJobStatus.Failed), CancellationToken);
        }

        // Pending only, and no chain at all (as if the save that should have started it was lost).
        var neverStarted = await CreateOrganizationAsync();
        await using (var dbContext = _host.Postgres.CreateDbContext())
        {
            var organization = await dbContext.Organizations.SingleAsync(candidate => candidate.Id == neverStarted.Organization.Id, CancellationToken);
            organization.ChangeRetention(90, 0, DateTimeOffset.UtcNow).ShouldBe(OrganizationSettingsChange.Changed);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var healthy = await CreateOrganizationAsync();
        await MakeCurrentAsync(healthy, 30);
        await StartChainAsync(healthy);
        var forever = await CreateOrganizationAsync();

        var reconciler = _host.Factory.Services.GetRequiredService<RetentionCleanupReconciler>();
        (await reconciler.ReconcileAsync(CancellationToken)).ShouldBeGreaterThanOrEqualTo(2);
        var hostNow = _host.Clock.GetUtcNow();

        foreach (var org in new[] { broken, neverStarted })
        {
            var queued = (await RetentionJobsAsync(org)).Where(candidate => candidate.Status == BackgroundJobStatus.Queued).ShouldHaveSingleItem();
            RunAtOf(queued).ShouldBe(RetentionCleanupRules.NextRunAfter(hostNow, Taipei), TimeSpan.FromMinutes(1));
            (await LoadAsync(org)).RetentionCleanupNextRunAt.ShouldBe(RunAtOf(queued));
        }

        (await RetentionJobsAsync(healthy)).Count.ShouldBe(1);
        (await RetentionJobsAsync(forever)).ShouldBeEmpty();

        // Again: nothing more for any of them.
        await reconciler.ReconcileAsync(CancellationToken);
        (await RetentionJobsAsync(broken)).Count.ShouldBe(2);
        (await RetentionJobsAsync(neverStarted)).Count.ShouldBe(1);
        (await RetentionJobsAsync(healthy)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_subcommand_cleans_up_now_or_as_of_a_given_time_in_development()
    {
        var org = await CreateOrganizationAsync();
        var admin = await SignInAsync(org, "admin");
        var now = _host.Clock.GetUtcNow();
        var thread = await SeedThreadAsync(org, now.AddHours(-1));
        (await admin.Spa.PutAsync(Path, admin.Token, new { days = 30, revision = 0 })).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        // Now: 30 days is still pending, the retention forever.
        var today = await RunCommandAsync(_host.Factory.Services, "--organization", org.Organization.Code);
        today.Exit.ShouldBe(RetentionCleanupCommand.ExitSuccess, today.Error);
        today.Output.ShouldContain("永久保存");
        (await ThreadExistsAsync(org, thread.ThreadId)).ShouldBeTrue();

        // As if 38 days later (the API-mode E2E): the buffer is over, 30 days applies, today's thread is gone.
        var later = await RunCommandAsync(
            _host.Factory.Services, "--organization", org.Organization.Code.ToUpperInvariant(), "--as-of", now.AddDays(38).ToString("o"));
        later.Exit.ShouldBe(RetentionCleanupCommand.ExitSuccess, later.Error);
        later.Output.ShouldContain("永久 → 30 天");
        later.Output.ShouldContain("刪除 1 串對話");
        (await ThreadExistsAsync(org, thread.ThreadId)).ShouldBeFalse();
        // It left the chain alone.
        (await RetentionJobsAsync(org)).ShouldHaveSingleItem().Status.ShouldBe(BackgroundJobStatus.Queued);

        (await RunCommandAsync(_host.Factory.Services, "--organization", "no-such-org")).Exit.ShouldBe(RetentionCleanupCommand.ExitFailed);
        foreach (var args in new[] { Array.Empty<string>(), ["--organization"], ["--organization", "x", "--as-of", "tomorrow"], ["--days", "3"] })
        {
            (await RunCommandAsync(_host.Factory.Services, args)).Exit.ShouldBe(RetentionCleanupCommand.ExitUsage, string.Join(' ', args));
        }

        var help = await RunCommandAsync(_host.Factory.Services, "--help");
        (help.Exit, help.Output.StartsWith("用法：retention-cleanup", StringComparison.Ordinal)).ShouldBe((RetentionCleanupCommand.ExitSuccess, true));
    }

    [Fact]
    public async Task The_subcommand_refuses_as_of_outside_development_and_testing()
    {
        foreach (var environment in new[] { "Production", "Staging" })
        {
            var services = new ServiceCollection()
                .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment })
                .BuildServiceProvider();

            var result = await RunCommandAsync(services, "--organization", "any", "--as-of", "2026-11-13T03:00:00+08:00");

            result.Exit.ShouldBe(RetentionCleanupCommand.ExitUsage, environment);
            result.Error.ShouldContain("--as-of 只能在 Development 或 Testing 使用");
        }
    }

    // --- Cleanup helpers -------------------------------------------------------------------------

    /// <summary>Starts the organization's chain as a settings save would; returns its first job.</summary>
    private async Task<BackgroundJob> StartChainAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(CancellationToken);
        (await RetentionCleanupChain.EnsureStartedAsync(dbContext, org.Organization.Id, DateTimeOffset.UtcNow, Taipei, CancellationToken)).ShouldBeTrue();
        await transaction.CommitAsync(CancellationToken);
        return (await RetentionJobsAsync(org)).Last();
    }

    private async Task HandleAsync(TestOrganization org, BackgroundJob job, DateTimeOffset now)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        var handler = new RetentionCleanupHandler(dbContext, NewService(dbContext, now), new FixedClock(now));
        await handler.HandleAsync(new JobContext(job.Id, org.Organization.Id, job.Kind, job.Payload, 1, job.MaxAttempts), CancellationToken);
    }

    private async Task SetForeverAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        var organization = await dbContext.Organizations.SingleAsync(candidate => candidate.Id == org.Organization.Id, CancellationToken);
        organization.ChangeRetention(null, organization.SettingsRevision, DateTimeOffset.UtcNow).ShouldBe(OrganizationSettingsChange.Changed);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task<int[]> CountOthersAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return
        [
            await dbContext.ModelInvocations.CountAsync(CancellationToken),
            await dbContext.AssistantIssues.CountAsync(CancellationToken),
            await dbContext.AssistantIssueEvents.CountAsync(CancellationToken),
            await dbContext.AssistantTestRuns.CountAsync(CancellationToken),
            await dbContext.DatabaseSubmissions.CountAsync(CancellationToken),
            await dbContext.DatabaseReports.CountAsync(CancellationToken),
        ];
    }

    private static async Task<(int Exit, string Output, string Error)> RunCommandAsync(IServiceProvider services, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await RetentionCleanupCommand.RunAsync(services, args, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }
}
