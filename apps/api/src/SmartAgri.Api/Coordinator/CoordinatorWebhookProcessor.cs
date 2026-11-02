using SmartAgri.Application.Observability;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// Takes 統籌助理 deliveries off <see cref="CoordinatorWebhookQueue"/> and handles each in its own
/// dependency-injection scope with <see cref="CoordinatorEventHandler"/> (#372). At most
/// <see cref="MaxInFlight"/> deliveries are handled at the same time; the events of one delivery one
/// after another, in LINE's order.
/// </summary>
/// <remarks>
/// The scope acts for no organization: the handler finds the sender's shops through
/// <c>CoordinatorLookup</c> and writes into a shop through a context acting for that shop only. A
/// failing delivery is logged (exception type only — never a LINE id, token, code or message) and
/// dropped; it never stops the processor.
/// </remarks>
internal sealed class CoordinatorWebhookProcessor : BackgroundService
{
    /// <summary>Deliveries handled at once.</summary>
    internal const int MaxInFlight = 16;

    private readonly CoordinatorWebhookQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CoordinatorWebhookProcessor> _logger;

    public CoordinatorWebhookProcessor(
        CoordinatorWebhookQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<CoordinatorWebhookProcessor> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var inFlight = new SemaphoreSlim(MaxInFlight);
        try
        {
            await foreach (var delivery in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await inFlight.WaitAsync(stoppingToken);
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await ProcessAsync(delivery, stoppingToken);
                        }
                        finally
                        {
                            inFlight.Release();
                        }
                    },
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: queued deliveries are dropped (their reply tokens would expire anyway).
        }

        // Let the deliveries being handled (cancelled by now) finish before the services go away.
        var deadline = TimeSpan.FromSeconds(10);
        for (var permit = 0; permit < MaxInFlight; permit++)
        {
            if (!await inFlight.WaitAsync(deadline, CancellationToken.None))
            {
                break;
            }
        }
    }

    private async Task ProcessAsync(CoordinatorWebhookDelivery delivery, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CoordinatorEventHandler>().HandleAsync(delivery, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "A 統籌助理 webhook delivery failed; its {EventCount} events are dropped: {Failure}",
                delivery.Events.Count, ExceptionSummary.Of(exception));
        }
    }
}
