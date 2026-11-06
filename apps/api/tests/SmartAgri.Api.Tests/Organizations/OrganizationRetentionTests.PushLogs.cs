using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Organizations;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary>
/// The 統籌助理's push log keeps 400 days (#390): rows of pushes sent before the cutoff are deleted by the daily sweep for
/// every organization and by the <c>retention-cleanup</c> subcommand for one — never another organization's, never a
/// newer one.
/// </summary>
public sealed partial class OrganizationRetentionTests
{
    [Fact]
    public async Task Push_log_rows_older_than_400_days_go_and_newer_ones_stay()
    {
        var org = await CreateOrganizationAsync();
        var asOf = _host.Clock.GetUtcNow();
        var cutoff = RetentionCleanupRules.Cutoff(asOf, CoordinatorPushLog.RetentionDays, Taipei);
        var old = await SeedPushAsync(org, asOf.AddDays(-500));
        var atBoundary = await SeedPushAsync(org, cutoff.AddSeconds(-1));
        var kept = await SeedPushAsync(org, cutoff);
        var recent = await SeedPushAsync(org, asOf.AddDays(-30));

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var result = await NewService(dbContext, asOf, batchSize: 1).DeleteOldPushLogsAsync(asOf, CancellationToken);
            (result.Cutoff, result.Count, result.Batches).ShouldBe((cutoff, 2, 3));
        }

        (await PushIdsAsync(org)).ShouldBe([kept, recent], ignoreOrder: true);
        (await PushIdsAsync(org)).ShouldNotContain(old);
        (await PushIdsAsync(org)).ShouldNotContain(atBoundary);
        (await ActivitiesAsync(org)).ShouldBeEmpty("counts only; no activity");
    }

    [Fact]
    public async Task The_daily_sweep_and_the_subcommand_delete_old_push_log_rows_and_never_another_organizations()
    {
        var asOf = _host.Clock.GetUtcNow();
        var swept = await CreateOrganizationAsync();
        var sweptRecent = await SeedPushAsync(swept, asOf.AddDays(-10));
        await SeedPushAsync(swept, asOf.AddDays(-450));

        var sweep = _host.Factory.Services.GetRequiredService<HandoffRetentionSweep>();
        (await sweep.SweepAsync(asOf, CancellationToken)).ShouldBeGreaterThanOrEqualTo(1);
        (await PushIdsAsync(swept)).ShouldBe([sweptRecent]);

        var cleaned = await CreateOrganizationAsync();
        var neighbour = await CreateOrganizationAsync();
        var keptRecent = await SeedPushAsync(cleaned, asOf.AddDays(-1));
        await SeedPushAsync(cleaned, asOf.AddDays(-401));
        var neighbours = new[] { await SeedPushAsync(neighbour, asOf.AddDays(-401)), await SeedPushAsync(neighbour, asOf.AddDays(-1)) };

        var command = await RunCommandAsync(_host.Factory.Services, "--organization", cleaned.Organization.Code);

        command.Exit.ShouldBe(RetentionCleanupCommand.ExitSuccess, command.Error);
        command.Output.ShouldContain("統籌助理推播紀錄：刪除 1 筆");
        (await PushIdsAsync(cleaned)).ShouldBe([keptRecent]);
        (await PushIdsAsync(neighbour)).ShouldBe(neighbours, ignoreOrder: true);
    }

    private async Task<Guid> SeedPushAsync(TestOrganization org, DateTimeOffset sentAt)
    {
        var row = CoordinatorPushLog.Record(org.Organization.Id, CoordinatorPushPurpose.Reservation, 1, sentAt);
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        dbContext.CoordinatorPushLogs.Add(row);
        await dbContext.SaveChangesAsync(CancellationToken);
        return row.Id;
    }

    private async Task<List<Guid>> PushIdsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.CoordinatorPushLogs.AsNoTracking().Select(row => row.Id).ToListAsync(CancellationToken);
    }
}
