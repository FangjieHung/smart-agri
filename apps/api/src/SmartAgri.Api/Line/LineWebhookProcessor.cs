using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Tenancy;
using SmartAgri.Application.Observability;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Line;

/// <summary>
/// Takes webhook deliveries off <see cref="LineWebhookQueue"/> and handles each in its own
/// dependency-injection scope acting for the assistant's organization (M5b plan §3 D), with
/// <see cref="LineWebhookEventHandler"/>. At most
/// <see cref="PublicRateLimitOptions.LineMaxConcurrentWebhooksPerAssistant"/> deliveries of one
/// assistant, and <see cref="MaxInFlight"/> in all, are handled at the same time; the events of one
/// delivery are handled one after another, in LINE's order.
/// </summary>
/// <remarks>
/// <para>
/// The organization was established in the webhook request — after the signature check, from the
/// server's own row (<c>PublicAssistantLookup</c>) — and travels with the delivery; the scope pins it
/// exactly as <c>VisitorSessionEndpoints</c> pins it for a request
/// (<see cref="ClaimsOrganizationContext.Pin"/>), so everything is read under the usual organization
/// filter. It fails closed: if the scope does not act for that organization, nothing runs.
/// </para>
/// <para>
/// A failing delivery is logged (assistant id and exception only — never a token, an id of LINE's or
/// a message) and dropped; it never stops the processor.
/// </para>
/// </remarks>
internal sealed class LineWebhookProcessor : BackgroundService
{
    /// <summary>Deliveries handled at once, all assistants together.</summary>
    internal const int MaxInFlight = 64;

    private readonly LineWebhookQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LineWebhookProcessor> _logger;
    private readonly int _perAssistant;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _assistantSlots = new();

    public LineWebhookProcessor(
        LineWebhookQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<PublicChannelsOptions> options,
        ILogger<LineWebhookProcessor> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _perAssistant = options.Value.RateLimits.LineMaxConcurrentWebhooksPerAssistant;
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

    private async Task ProcessAsync(LineWebhookDelivery delivery, CancellationToken stoppingToken)
    {
        var slot = _assistantSlots.GetOrAdd(delivery.AssistantId, _ => new SemaphoreSlim(_perAssistant));
        try
        {
            await slot.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ClaimsOrganizationContext>().Pin(delivery.OrganizationId);
            if (scope.ServiceProvider.GetRequiredService<IOrganizationContext>().OrganizationId != delivery.OrganizationId)
            {
                throw new InvalidOperationException(
                    "A LINE webhook delivery's scope does not act for the assistant's organization.");
            }

            await scope.ServiceProvider.GetRequiredService<LineWebhookEventHandler>().HandleAsync(delivery, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Only types: the delivery holds LINE users' messages, and an exception message can quote them.
            _logger.LogError(
                "A LINE webhook delivery for assistant {AssistantId} failed; its events are dropped: {Failure}",
                delivery.AssistantId, ExceptionSummary.Of(exception));
        }
        finally
        {
            slot.Release();
        }
    }
}
