using System.Diagnostics.Metrics;
using SmartAgri.Domain.Observability;
using SmartAgri.Infrastructure.Jobs;

namespace SmartAgri.Api.Jobs;

/// <summary>
/// The queue-depth metric: an observable gauge <see cref="QueuedJobsInstrument"/> of
/// queued jobs per kind (tag <see cref="JobRunner.KindTag"/>), across organizations and
/// without any organization detail. Collecting a metric must not query the database, so
/// the gauge reports the latest <see cref="RefreshAsync"/> (which <see cref="JobWorker"/>
/// calls periodically); every kind with a registered handler is reported, 0 when none
/// is queued.
/// </summary>
public sealed class JobQueueMetrics
{
    public const string QueuedJobsInstrument = "smartagri.jobs.queued";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<string> _handledKinds;
    private volatile IReadOnlyDictionary<string, int> _latest = new Dictionary<string, int>();

    public JobQueueMetrics(IMeterFactory meterFactory, IServiceScopeFactory scopeFactory, JobRunner runner)
    {
        _scopeFactory = scopeFactory;
        _handledKinds = runner.Kinds;

        meterFactory.Create(SmartAgriMeter.Name).CreateObservableGauge(
            QueuedJobsInstrument,
            Observe,
            unit: "{job}",
            description: "Background jobs waiting in the queue (due or scheduled for later), per kind.");
    }

    /// <summary>Re-counts the queue (<see cref="JobClaimer.CountQueuedAsync"/>).</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var counts = await scope.ServiceProvider.GetRequiredService<JobClaimer>().CountQueuedAsync(cancellationToken);

        var latest = _handledKinds.ToDictionary(kind => kind, _ => 0, StringComparer.Ordinal);
        foreach (var count in counts)
        {
            latest[count.Kind] = count.Count;
        }

        _latest = latest;
    }

    private IEnumerable<Measurement<int>> Observe() =>
        _latest.Select(pair => new Measurement<int>(pair.Value, new KeyValuePair<string, object?>(JobRunner.KindTag, pair.Key)));
}
