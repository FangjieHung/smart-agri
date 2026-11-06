using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Organizations;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary>
/// The shops' online reservations are deleted 90 days after their time (store-reservations design R8, #389), name,
/// phone, Email and note with the row: by the daily sweep for every organization whatever its conversation retention,
/// and by the <c>retention-cleanup</c> subcommand for one — whatever the reservation's status, never another
/// organization's, never one whose time is less than 90 days ago.
/// </summary>
public sealed partial class OrganizationRetentionTests
{
    [Fact]
    public async Task Reservations_whose_time_is_over_90_days_ago_go_whatever_their_status()
    {
        var org = await CreateOrganizationAsync();
        var asOf = _host.Clock.GetUtcNow();
        // 00:00 of the Taipei day 90 days before today: a time strictly before it has expired.
        var cutoff = RetentionCleanupRules.Cutoff(asOf, CoordinatorReservation.RetentionDays, Taipei);
        var expiredLongAgo = await SeedReservationAsync(org, asOf.AddDays(-200));
        var expiredAtBoundary = await SeedReservationAsync(org, cutoff.AddSeconds(-1), expired: true);
        var keptAtBoundary = await SeedReservationAsync(org, cutoff);
        var kept89 = await SeedReservationAsync(org, asOf.AddDays(-89));
        var upcoming = await SeedReservationAsync(org, asOf.AddDays(5));

        await using (var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id))
        {
            var result = await NewService(dbContext, asOf, batchSize: 1).DeleteExpiredReservationsAsync(asOf, CancellationToken);
            (result.Cutoff, result.Count, result.Batches).ShouldBe((cutoff, 2, 3));
        }

        (await ReservationIdsAsync(org)).ShouldBe([keptAtBoundary, kept89, upcoming], ignoreOrder: true);
        (await ReservationIdsAsync(org)).ShouldNotContain(expiredLongAgo);
        (await ReservationIdsAsync(org)).ShouldNotContain(expiredAtBoundary);
        (await ActivitiesAsync(org)).ShouldBeEmpty("the rows are the customers'; no activity");

        await using var again = _host.Postgres.CreateDbContext(org.Organization.Id);
        (await NewService(again, asOf).DeleteExpiredReservationsAsync(asOf, CancellationToken)).Count.ShouldBe(0);
    }

    [Fact]
    public async Task The_daily_sweep_and_the_subcommand_delete_old_reservations_and_never_another_organizations()
    {
        var asOf = _host.Clock.GetUtcNow();
        var forever = await CreateOrganizationAsync();
        var thirtyDays = await CreateOrganizationAsync();
        await MakeCurrentAsync(thirtyDays, 30);
        var seeded = new Dictionary<TestOrganization, (Guid Old, Guid Recent)>();
        foreach (var org in new[] { forever, thirtyDays })
        {
            seeded[org] = (await SeedReservationAsync(org, asOf.AddDays(-120)), await SeedReservationAsync(org, asOf.AddDays(-30)));
        }

        var sweep = _host.Factory.Services.GetRequiredService<HandoffRetentionSweep>();
        (await sweep.SweepAsync(asOf, CancellationToken)).ShouldBeGreaterThanOrEqualTo(2);

        foreach (var (org, (_, recent)) in seeded)
        {
            (await ReservationIdsAsync(org)).ShouldBe([recent], org.Organization.Code);
        }

        var cleaned = await CreateOrganizationAsync();
        var neighbour = await CreateOrganizationAsync();
        var kept = await SeedReservationAsync(cleaned, asOf.AddDays(-10));
        await SeedReservationAsync(cleaned, asOf.AddDays(-100));
        var neighbours = new[] { await SeedReservationAsync(neighbour, asOf.AddDays(-100)), await SeedReservationAsync(neighbour, asOf.AddDays(-10)) };

        var command = await RunCommandAsync(_host.Factory.Services, "--organization", cleaned.Organization.Code);

        command.Exit.ShouldBe(RetentionCleanupCommand.ExitSuccess, command.Error);
        command.Output.ShouldContain("線上訂位：刪除 1 筆");
        (await ReservationIdsAsync(cleaned)).ShouldBe([kept]);
        (await ReservationIdsAsync(neighbour)).ShouldBe(neighbours, ignoreOrder: true);
    }

    // --- Reservation helpers ---------------------------------------------------------------------

    /// <summary>A reservation of the organization for <paramref name="at"/> — pending, or expired when
    /// <paramref name="expired"/>.</summary>
    private async Task<Guid> SeedReservationAsync(TestOrganization org, DateTimeOffset at, bool expired = false)
    {
        var local = TimeZoneInfo.ConvertTime(at, Taipei);
        var slot = new ReservationSlot(DateOnly.FromDateTime(local.DateTime), local, local.DateTime);
        var reservation = CoordinatorReservation.Submit(
            org.Organization.Id, slot, 2, "陳小姐", "0912345678", "chen@example.com", "需要兒童椅", CoordinatorReservation.NewSubmissionKey(), at.AddDays(-1));
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        dbContext.CoordinatorReservations.Add(reservation);
        await dbContext.SaveChangesAsync(CancellationToken);
        if (expired)
        {
            await dbContext.CoordinatorReservations.Where(row => row.Id == reservation.Id).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, CoordinatorReservationStatus.Expired)
                    .SetProperty(row => row.StatusChangedAt, at.AddHours(-1))
                    .SetProperty(row => row.Version, 2),
                CancellationToken);
        }

        return reservation.Id;
    }

    private async Task<List<Guid>> ReservationIdsAsync(TestOrganization org)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(org.Organization.Id);
        return await dbContext.CoordinatorReservations.AsNoTracking().Select(reservation => reservation.Id).ToListAsync(CancellationToken);
    }
}
