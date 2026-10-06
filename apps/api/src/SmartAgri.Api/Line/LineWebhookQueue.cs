using System.Threading.Channels;
using SmartAgri.Application.Line;

namespace SmartAgri.Api.Line;

/// <summary>
/// The events of one verified webhook request, accepted for processing (M5b plan §3 C step 6).
/// </summary>
/// <param name="OrganizationId">The assistant's organization, found by <c>PublicAssistantLookup</c>
/// from the server's own row in the webhook request; the processor acts for it.</param>
/// <param name="Events">The events not seen before (deduplicated by <c>webhookEventId</c>), in the
/// order LINE sent them.</param>
/// <param name="ReceivedAt">When the webhook request arrived.</param>
public sealed record LineWebhookDelivery(
    Guid AssistantId,
    Guid OrganizationId,
    IReadOnlyList<LineWebhookEvent> Events,
    DateTimeOffset ReceivedAt);

/// <summary>
/// The in-process, bounded queue between the webhook endpoint and <see cref="LineWebhookProcessor"/>
/// (M5b plan §3 D: not the PostgreSQL job queue, whose 2-second polling and 30-second retries do not
/// fit a reply token that lasts about a minute). A singleton. Lost on restart, which the plan accepts:
/// the queued events' reply tokens would have expired anyway; one API container per deployment.
/// </summary>
public sealed class LineWebhookQueue
{
    /// <summary>Deliveries waiting at most; when full, a new delivery is dropped (and logged).</summary>
    public const int Capacity = 1000;

    private readonly Channel<LineWebhookDelivery> _channel = Channel.CreateBounded<LineWebhookDelivery>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Queues <paramref name="delivery"/> without waiting; <see langword="false"/> when the
    /// queue is full.</summary>
    public bool TryEnqueue(LineWebhookDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return _channel.Writer.TryWrite(delivery);
    }

    internal ChannelReader<LineWebhookDelivery> Reader => _channel.Reader;
}
