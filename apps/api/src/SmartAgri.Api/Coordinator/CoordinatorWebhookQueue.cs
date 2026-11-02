using System.Threading.Channels;
using SmartAgri.Application.Line;

namespace SmartAgri.Api.Coordinator;

/// <summary>The one-to-one events of one verified 統籌助理 webhook request, not seen before, in LINE's order.</summary>
/// <param name="ReceivedAt">When the webhook request arrived.</param>
public sealed record CoordinatorWebhookDelivery(IReadOnlyList<LineWebhookEvent> Events, DateTimeOffset ReceivedAt);

/// <summary>
/// The in-process, bounded queue between <see cref="CoordinatorWebhookEndpoints"/> and
/// <see cref="CoordinatorWebhookProcessor"/> (#372), as <c>LineWebhookQueue</c> is for assistants'
/// channels: lost on restart, which is accepted for the same reason (the queued events' reply tokens
/// would have expired; one API container per deployment). A singleton.
/// </summary>
public sealed class CoordinatorWebhookQueue
{
    /// <summary>Deliveries waiting at most; when full, a new delivery is dropped (and logged).</summary>
    public const int Capacity = 1000;

    private readonly Channel<CoordinatorWebhookDelivery> _channel = Channel.CreateBounded<CoordinatorWebhookDelivery>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Queues <paramref name="delivery"/> without waiting; <see langword="false"/> when full.</summary>
    public bool TryEnqueue(CoordinatorWebhookDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return _channel.Writer.TryWrite(delivery);
    }

    internal ChannelReader<CoordinatorWebhookDelivery> Reader => _channel.Reader;
}

/// <summary>
/// The 統籌助理's own memory of accepted <c>webhookEventId</c>s (#372): the platform-level counterpart of
/// the per-assistant one, with the same rules (<see cref="LineWebhookDeduplicator"/>: 10 minutes, in this
/// process's memory). A singleton separate from the assistants' so neither can crowd out the other.
/// </summary>
public sealed class CoordinatorWebhookDeduplicator
{
    /// <summary>The key the coordinator's ids are kept under (no assistant has it).</summary>
    private static readonly Guid Key = Guid.Empty;

    private readonly LineWebhookDeduplicator _inner;

    public CoordinatorWebhookDeduplicator(TimeProvider clock)
    {
        _inner = new LineWebhookDeduplicator(clock);
    }

    /// <inheritdoc cref="LineWebhookDeduplicator.TryAccept"/>
    public bool TryAccept(string webhookEventId) => _inner.TryAccept(Key, webhookEventId);

    /// <inheritdoc cref="LineWebhookDeduplicator.Release"/>
    public void Release(string webhookEventId) => _inner.Release(Key, webhookEventId);
}
