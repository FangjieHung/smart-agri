using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Organizations;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Jobs;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary>
/// The cleanup and the preview are bulk statements (<c>ExecuteDeleteAsync</c>, <c>CountAsync</c>) scoped
/// only by the organization filter: irreversible, so pinned here. Another organization's old threads,
/// messages, citations and answer outcomes survive every way a cleanup runs, are never counted, and
/// the reconciler's job belongs to the organization it repairs (issue #241).
/// </summary>
public sealed partial class OrganizationRetentionTests
{
    [Fact]
    public async Task A_cleanup_through_the_job_or_the_subcommand_never_touches_another_organization()
    {
        var now = _host.Clock.GetUtcNow();
        var cleaned = await CreateOrganizationAsync();
        await MakeCurrentAsync(cleaned, 30);
        // Neighbours whose own rule would delete their old rows too, if anything ran for them.
        var keepsAYear = await CreateOrganizationAsync();
        await MakeCurrentAsync(keepsAYear, 365);
        var forever = await CreateOrganizationAsync();
        var neighbours = new[] { keepsAYear, forever };
        foreach (var org in neighbours)
        {
            await SeedOldRowsAsync(org, now.AddDays(-400));
        }

        var before = new List<int[]>();
        foreach (var org in neighbours)
        {
            before.Add(await CountConversationRowsAsync(org));
        }

        before.ShouldAllBe(counts => counts.SequenceEqual(new[] { 2, 4, 2, 2 }));

        // 1. Through the queue: the job's handler, in the job's organization scope.
        var viaJob = await SeedOldRowsAsync(cleaned, now.AddDays(-400));
        var job = await StartChainAsync(cleaned);
        await using (var dbContext = _host.Postgres.CreateDbContext(cleaned.Organization.Id))
        {
            await dbContext.BackgroundJobs.Where(candidate => candidate.Id == job.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.RunAfter, DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken);
        }

        await _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

        (await RetentionJobsAsync(cleaned)).First().Status.ShouldBe(BackgroundJobStatus.Succeeded);
        (await CountConversationRowsAsync(cleaned)).ShouldBe([0, 0, 0, 0]);
        foreach (var thread in viaJob)
        {
            (await ThreadExistsAsync(cleaned, thread)).ShouldBeFalse();
        }

        for (var i = 0; i < neighbours.Length; i++)
        {
            (await CountConversationRowsAsync(neighbours[i])).ShouldBe(before[i], neighbours[i].Organization.Code);
        }

        // 2. Through the subcommand, by the cleaned organization's code.
        await SeedOldRowsAsync(cleaned, now.AddDays(-400));
        var command = await RunCommandAsync(_host.Factory.Services, "--organization", cleaned.Organization.Code);
        command.Exit.ShouldBe(RetentionCleanupCommand.ExitSuccess, command.Error);
        command.Output.ShouldContain("刪除 2 串對話、2 筆回答紀錄");
        (await CountConversationRowsAsync(cleaned)).ShouldBe([0, 0, 0, 0]);

        for (var i = 0; i < neighbours.Length; i++)
        {
            (await CountConversationRowsAsync(neighbours[i])).ShouldBe(before[i], neighbours[i].Organization.Code);
        }

        // The neighbours' activity logs say nothing happened to them.
        foreach (var org in neighbours)
        {
            (await ActivitiesAsync(org)).ShouldBeEmpty();
        }

        // And counted across every organization, only the cleaned one's rows are gone.
        await using var unfiltered = _host.Postgres.CreateDbContext();
        var ids = neighbours.Select(org => org.Organization.Id).Append(cleaned.Organization.Id).ToList();
        var threadsByOrganization = await unfiltered.ChatThreads.IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .Where(thread => ids.Contains(thread.OrganizationId))
            .GroupBy(thread => thread.OrganizationId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, CancellationToken);
        threadsByOrganization.ShouldBe(new Dictionary<Guid, int> { [keepsAYear.Organization.Id] = 2, [forever.Organization.Id] = 2 }, ignoreOrder: true);
    }

    [Fact]
    public async Task The_preview_counts_only_the_callers_organization()
    {
        var now = _host.Clock.GetUtcNow();
        var mine = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        await SeedThreadAsync(mine, now.AddDays(-100));
        for (var i = 0; i < 3; i++)
        {
            await SeedThreadAsync(other, now.AddDays(-100 - i));
        }

        foreach (var (org, expected) in new[] { (mine, 1), (other, 3) })
        {
            var admin = await SignInAsync(org, "admin");
            var response = await admin.Spa.GetAsync($"{PreviewPath}?days=30", admin.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await BodyJsonAsync(response)).GetProperty("threadCount").GetInt32().ShouldBe(expected, org.Organization.Code);
        }
    }

    [Fact]
    public async Task The_reconcilers_job_belongs_to_the_organization_it_repairs()
    {
        var repaired = await CreateOrganizationAsync();
        await MakeCurrentAsync(repaired, 30);
        var forever = await CreateOrganizationAsync();

        await _host.Factory.Services.GetRequiredService<RetentionCleanupReconciler>().ReconcileAsync(CancellationToken);

        // Read with the filter off, so a job filed under the wrong organization would show up too.
        await using var unfiltered = _host.Postgres.CreateDbContext();
        var jobs = await unfiltered.BackgroundJobs.IgnoreQueryFilters([AppDbContext.OrganizationFilter]).AsNoTracking()
            .Where(job => job.Kind == RetentionCleanupJob.Kind
                && (job.OrganizationId == repaired.Organization.Id || job.OrganizationId == forever.Organization.Id))
            .ToListAsync(CancellationToken);
        var job = jobs.ShouldHaveSingleItem();
        job.OrganizationId.ShouldBe(repaired.Organization.Id);
        (await LoadAsync(repaired)).RetentionCleanupNextRunAt.ShouldBe(RunAtOf(job));
        (await LoadAsync(forever)).RetentionCleanupNextRunAt.ShouldBeNull();

        // Its handler acts for that organization: the runner enters the job's organization.
        (await RetentionJobsAsync(repaired)).ShouldHaveSingleItem().Id.ShouldBe(job.Id);
        (await RetentionJobsAsync(forever)).ShouldBeEmpty();
    }

    /// <summary>Two threads (each with two messages and a citation) and two answer outcomes at
    /// <paramref name="at"/>; returns the thread ids.</summary>
    private async Task<Guid[]> SeedOldRowsAsync(TestOrganization org, DateTimeOffset at)
    {
        var first = await SeedThreadAsync(org, at);
        var second = await SeedThreadAsync(org, at.AddHours(1));
        await SeedOutcomeAsync(org, AnswerOutcomeChannel.Chat, at);
        await SeedOutcomeAsync(org, AnswerOutcomeChannel.Website, at);
        return [first.ThreadId, second.ThreadId];
    }

    /// <summary>The organization's threads, messages, citations and answer outcomes.</summary>
    private async Task<int[]> CountConversationRowsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return
        [
            await dbContext.ChatThreads.CountAsync(CancellationToken),
            await dbContext.ChatMessages.CountAsync(CancellationToken),
            await dbContext.ChatMessageCitations.CountAsync(CancellationToken),
            await dbContext.AnswerOutcomes.CountAsync(CancellationToken),
        ];
    }
}
