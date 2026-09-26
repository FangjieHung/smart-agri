using Microsoft.Extensions.Options;

namespace SmartAgri.Api.Jobs;

/// <summary>
/// Runs background jobs inside the Api process (background-jobs-on-postgresql ADR):
/// <see cref="JobOptions.Concurrency"/> loops each claim and process jobs through
/// <see cref="JobRunner"/>, waiting <see cref="JobOptions.PollInterval"/> whenever there is
/// nothing to claim, and one more loop refreshes <see cref="JobQueueMetrics"/>. Does nothing
/// when <c>Jobs:WorkerEnabled</c> is false or no job handler is registered. Errors are
/// logged and never stop the host: a job that could not be finished stays locked until
/// its lease expires and is then claimed again.
/// </summary>
internal sealed class JobWorker : BackgroundService
{
    private static readonly TimeSpan QueueDepthRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly JobRunner _runner;
    private readonly JobQueueMetrics _queueMetrics;
    private readonly JobOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<JobWorker> _logger;

    public JobWorker(
        JobRunner runner,
        JobQueueMetrics queueMetrics,
        IOptions<JobOptions> options,
        TimeProvider clock,
        ILogger<JobWorker> logger)
    {
        _runner = runner;
        _queueMetrics = queueMetrics;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled)
        {
            _logger.LogInformation("The background job worker is disabled (Jobs:WorkerEnabled is false).");
            return Task.CompletedTask;
        }

        if (_runner.Kinds.Count == 0)
        {
            _logger.LogInformation("No background job handlers are registered; the job worker has nothing to run.");
            return Task.CompletedTask;
        }

        var loops = new List<Task> { RefreshQueueDepthAsync(stoppingToken) };
        for (var i = 0; i < _options.Concurrency; i++)
        {
            loops.Add(ProcessJobsAsync(stoppingToken));
        }

        return Task.WhenAll(loops);
    }

    private async Task ProcessJobsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed;
            try
            {
                processed = await _runner.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Claiming or processing a background job failed.");
                processed = false;
            }

            if (!processed && !await WaitAsync(_options.PollInterval, stoppingToken))
            {
                return;
            }
        }
    }

    private async Task RefreshQueueDepthAsync(CancellationToken stoppingToken)
    {
        do
        {
            try
            {
                await _queueMetrics.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Counting queued background jobs failed.");
            }
        }
        while (await WaitAsync(QueueDepthRefreshInterval, stoppingToken));
    }

    /// <summary>False once the host is stopping.</summary>
    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, _clock, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
