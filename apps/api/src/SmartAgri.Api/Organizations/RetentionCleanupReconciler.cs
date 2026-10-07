using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Jobs;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Jobs;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Organizations;

/// <summary>
/// The startup safety net of the retention cleanup chains (M6 plan §3 G, technical risk 3): a chain
/// breaks when its job fails for good (retries used up), so at startup every organization with a
/// retention in days (current or pending) but no queued or running <see cref="RetentionCleanupJob"/>
/// gets one again, for the next local 03:00. The chain's compare-and-set
/// (<see cref="RetentionCleanupChain.AdvanceAsync"/>) makes it safe to run twice or next to a job.
/// </summary>
/// <remarks>
/// Runs only where the job worker runs (<c>Jobs:WorkerEnabled</c>): a host that processes no jobs has
/// no business repairing their chains — test hosts and one-shot subcommands included. A failure is
/// logged and never stops the host.
/// </remarks>
public sealed class RetentionCleanupReconciler : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly JobOptions _jobOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<RetentionCleanupReconciler> _logger;

    public RetentionCleanupReconciler(
        IServiceProvider services, IOptions<JobOptions> jobOptions, TimeProvider clock, ILogger<RetentionCleanupReconciler> logger)
    {
        _services = services;
        _jobOptions = jobOptions.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_jobOptions.WorkerEnabled)
        {
            return;
        }

        try
        {
            var queued = await ReconcileAsync(cancellationToken);
            if (queued > 0)
            {
                _logger.LogWarning("Queued the retention cleanup again for {Count} organizations whose chain had stopped.", queued);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not check the retention cleanup chains at startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Re-queues every broken chain; returns how many it queued.</summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
        var timeZone = scope.ServiceProvider.GetRequiredService<IOptions<StatisticsOptions>>().Value.TryResolve()
            ?? throw new InvalidOperationException("Statistics:TimeZone was validated at startup.");

        // The Organizations table is not organization scoped; each organization's jobs are then read
        // in a context acting for it (the organization filter stays on).
        List<Guid> limited;
        await using (var all = new AppDbContext(options, FixedOrganizationContext.None))
        {
            limited = await all.Organizations.AsNoTracking()
                .Where(organization => organization.RetentionDays != null || organization.PendingRetentionDays != null)
                .Select(organization => organization.Id)
                .ToListAsync(cancellationToken);
        }

        var queued = 0;
        foreach (var organizationId in limited)
        {
            await using var context = new AppDbContext(options, new FixedOrganizationContext(organizationId));
            var running = await context.BackgroundJobs.AnyAsync(
                job => job.Kind == RetentionCleanupJob.Kind
                    && (job.Status == BackgroundJobStatus.Queued || job.Status == BackgroundJobStatus.Running),
                cancellationToken);
            if (running)
            {
                continue;
            }

            var nextRunAt = await context.Organizations.AsNoTracking()
                .Where(organization => organization.Id == organizationId)
                .Select(organization => organization.RetentionCleanupNextRunAt)
                .SingleAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var now = _clock.GetUtcNow();
            if (await RetentionCleanupChain.AdvanceAsync(context, organizationId, nextRunAt, now, now, timeZone, cancellationToken))
            {
                queued += 1;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return queued;
    }
}
