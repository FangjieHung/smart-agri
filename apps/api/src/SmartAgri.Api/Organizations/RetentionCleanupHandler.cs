using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Organizations;

/// <summary>
/// Handles <see cref="RetentionCleanupJob.Kind"/> (M6 plan §3 G): first claims the run with the
/// chain's compare-and-set — the next job is queued, or the chain ends when the retention is forever
/// with nothing pending — and only then cleans up (<see cref="RetentionCleanupService"/>).
/// </summary>
/// <remarks>
/// A job whose <see cref="RetentionCleanupJob.RunAt"/> is no longer the organization's
/// <c>RetentionCleanupNextRunAt</c> is a duplicate delivery (or was superseded by the reconciler):
/// it does nothing, so a duplicate deletes nothing and queues nothing. A job that runs late (the
/// worker was down) cleans up once and queues the 03:00 after now: the cutoff moves with the
/// calendar, so missed days need no catching up. Should the run fail after its claim, the next
/// day's run deletes what it missed.
/// </remarks>
internal sealed class RetentionCleanupHandler : IJobHandler
{
    private readonly AppDbContext _dbContext;
    private readonly RetentionCleanupService _cleanup;
    private readonly TimeProvider _clock;

    public RetentionCleanupHandler(AppDbContext dbContext, RetentionCleanupService cleanup, TimeProvider clock)
    {
        _dbContext = dbContext;
        _cleanup = cleanup;
        _clock = clock;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<RetentionCleanupJob>();
        var organization = await _dbContext.Organizations.AsNoTracking()
            .Where(candidate => candidate.Id == job.OrganizationId)
            .Select(candidate => new { candidate.RetentionCleanupNextRunAt, HasLimit = candidate.RetentionDays != null || candidate.PendingRetentionDays != null })
            .SingleOrDefaultAsync(cancellationToken);
        if (organization is null || organization.RetentionCleanupNextRunAt != payload.RunAt)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        bool cleanUp;
        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var stopped = !organization.HasLimit
                && await RetentionCleanupChain.StopAsync(_dbContext, job.OrganizationId, payload.RunAt, cancellationToken);
            cleanUp = !stopped && await RetentionCleanupChain.AdvanceAsync(
                _dbContext,
                job.OrganizationId,
                payload.RunAt,
                payload.RunAt > now ? payload.RunAt : now,
                now,
                _cleanup.TimeZone,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (cleanUp)
        {
            await _cleanup.RunAsync(now, cancellationToken);
        }
    }
}
