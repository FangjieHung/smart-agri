using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Jobs;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Notifications;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Organizations;

/// <summary>
/// The daily sweep of handled 轉真人 notifications (#362): at the same local 03:00 as the conversation
/// cleanup (<see cref="RetentionCleanupRules.NextRunAfter"/>), every organization — whatever its
/// conversation retention, forever included, so not part of the per-organization
/// <see cref="RetentionCleanupJob"/> chain, which only organizations with a retention in days have —
/// deletes the ones handled more than <see cref="VisitorHandoff.HandledRetentionDays"/> days ago
/// (<see cref="RetentionCleanupService.DeleteHandledHandoffsAsync"/>). Pending ones are never deleted.
/// </summary>
/// <remarks>
/// <para>
/// Each organization is swept in a context acting for it (the organization filter stays on), like
/// <see cref="RetentionCleanupReconciler"/>. The deletion is idempotent, so a second process or a run
/// next to the <c>retention-cleanup</c> subcommand deletes nothing twice; a run missed while the api
/// was down is simply the next day's (the cutoff moves with the calendar).
/// </para>
/// <para>
/// Runs only where the job worker runs (<c>Jobs:WorkerEnabled</c>): test hosts and one-shot
/// subcommands never sweep. One organization's failure is logged and the others still run; a failed
/// day is logged and the next day runs again. Nothing runs at startup.
/// </para>
/// </remarks>
public sealed class HandoffRetentionSweep : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly JobOptions _jobOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<HandoffRetentionSweep> _logger;

    public HandoffRetentionSweep(
        IServiceProvider services, IOptions<JobOptions> jobOptions, TimeProvider clock, ILogger<HandoffRetentionSweep> logger)
    {
        _services = services;
        _jobOptions = jobOptions.Value;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_jobOptions.WorkerEnabled)
        {
            return;
        }

        var timeZone = _services.GetRequiredService<IOptions<StatisticsOptions>>().Value.TryResolve()
            ?? throw new InvalidOperationException("Statistics:TimeZone was validated at startup.");
        while (!stoppingToken.IsCancellationRequested)
        {
            var runAt = RetentionCleanupRules.NextRunAfter(_clock.GetUtcNow(), timeZone);
            var delay = runAt - _clock.GetUtcNow();
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _clock, stoppingToken);
                }

                await SweepAsync(_clock.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not sweep the handled visitor handoffs; the next day's sweep runs again.");
            }
        }
    }

    /// <summary>Sweeps every organization as of <paramref name="asOf"/>; returns how many handoffs it
    /// deleted in all.</summary>
    public async Task<int> SweepAsync(DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        // The Organizations table is not organization scoped; each organization's handoffs are then
        // deleted in a context acting for it.
        List<Guid> organizations;
        await using (var all = new AppDbContext(options, FixedOrganizationContext.None))
        {
            organizations = await all.Organizations.AsNoTracking()
                .OrderBy(organization => organization.Id)
                .Select(organization => organization.Id)
                .ToListAsync(cancellationToken);
        }

        var total = 0;
        foreach (var organizationId in organizations)
        {
            try
            {
                await using var context = new AppDbContext(options, new FixedOrganizationContext(organizationId));
                var cleanup = ActivatorUtilities.CreateInstance<RetentionCleanupService>(scope.ServiceProvider, context);
                total += (await cleanup.DeleteHandledHandoffsAsync(asOf, cancellationToken)).Count;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Could not sweep the handled visitor handoffs of organization {OrganizationId}.", organizationId);
            }
        }

        return total;
    }
}
