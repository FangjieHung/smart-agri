using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Jobs;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Organizations;

/// <summary>
/// The per-organization chain of daily <see cref="RetentionCleanupJob"/>s (M6 plan §3 G). Every move of
/// <c>Organization.RetentionCleanupNextRunAt</c> is a compare-and-set from the value last seen, and a
/// job is queued only by the call whose compare-and-set changed the row, so the chain never forks:
/// a job delivered twice, two reconcilers, or a settings save racing the job all queue at most one.
/// </summary>
/// <remarks>
/// Call these inside a transaction (with the job they queue saved in it): <c>context</c> must act for
/// <paramref name="organizationId"/>'s organization, so the job passes the write guard.
/// </remarks>
internal static class RetentionCleanupChain
{
    /// <summary>
    /// Starts the chain when the organization has a retention in days (current or pending) and no
    /// chain yet: the first run is the next local 03:00 after <paramref name="now"/>. Returns whether
    /// it started one.
    /// </summary>
    public static async Task<bool> EnsureStartedAsync(
        AppDbContext context, Guid organizationId, DateTimeOffset now, TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        var runAt = RetentionCleanupRules.NextRunAfter(now, timeZone);
        var started = await context.Organizations
            .Where(organization => organization.Id == organizationId
                && organization.RetentionCleanupNextRunAt == null
                && (organization.RetentionDays != null || organization.PendingRetentionDays != null))
            .ExecuteUpdateAsync(setters => setters.SetProperty(organization => organization.RetentionCleanupNextRunAt, runAt), cancellationToken);
        if (started == 1)
        {
            await QueueAsync(context, organizationId, runAt, now, cancellationToken);
        }

        return started == 1;
    }

    /// <summary>
    /// Moves the chain from <paramref name="seen"/> to the next local 03:00 after
    /// <paramref name="after"/> and queues that job. Returns whether this call moved it (false: another
    /// delivery or reconciler already did).
    /// </summary>
    public static async Task<bool> AdvanceAsync(
        AppDbContext context,
        Guid organizationId,
        DateTimeOffset? seen,
        DateTimeOffset after,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var runAt = RetentionCleanupRules.NextRunAfter(after, timeZone);
        var advanced = await context.Organizations
            .Where(organization => organization.Id == organizationId && organization.RetentionCleanupNextRunAt == seen)
            .ExecuteUpdateAsync(setters => setters.SetProperty(organization => organization.RetentionCleanupNextRunAt, runAt), cancellationToken);
        if (advanced == 1)
        {
            await QueueAsync(context, organizationId, runAt, now, cancellationToken);
        }

        return advanced == 1;
    }

    /// <summary>Ends the chain at <paramref name="seen"/> when the retention is forever with nothing
    /// pending (re-checked in the same statement, so a manager's new number saved first keeps it
    /// going). Returns whether this call ended it.</summary>
    public static async Task<bool> StopAsync(
        AppDbContext context, Guid organizationId, DateTimeOffset seen, CancellationToken cancellationToken) =>
        await context.Organizations
            .Where(organization => organization.Id == organizationId
                && organization.RetentionCleanupNextRunAt == seen
                && organization.RetentionDays == null
                && organization.PendingRetentionDays == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(organization => organization.RetentionCleanupNextRunAt, (DateTimeOffset?)null),
                cancellationToken) == 1;

    private static async Task QueueAsync(
        AppDbContext context, Guid organizationId, DateTimeOffset runAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        context.BackgroundJobs.Add(BackgroundJob.Create(
            organizationId, RetentionCleanupJob.Kind, new RetentionCleanupJob(runAt), now, runAfter: runAt));
        await context.SaveChangesAsync(cancellationToken);
    }
}
