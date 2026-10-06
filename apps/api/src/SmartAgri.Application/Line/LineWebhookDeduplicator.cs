namespace SmartAgri.Application.Line;

/// <summary>
/// Remembers which webhook events were already accepted, by <c>webhookEventId</c>, for
/// <see cref="Retention"/> (M5b plan §3 C step 5): LINE delivers at least once — a redelivery
/// (<c>deliveryContext.isRedelivery</c>) or a network retry carries the same id — and each event must
/// be handled once. In this process's memory only (one API container per deployment), at most
/// <see cref="Capacity"/> ids; beyond that the oldest is forgotten early. Thread-safe; a singleton.
/// </summary>
public sealed class LineWebhookDeduplicator
{
    /// <summary>How long an id is remembered (plan §3 C: 10 minutes).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    /// <summary>The most ids remembered at once.</summary>
    public const int Capacity = 100_000;

    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid AssistantId, string EventId), DateTimeOffset> _seen = [];

    /// <summary>In the order they were first seen (oldest first), for expiry.</summary>
    private readonly Queue<((Guid AssistantId, string EventId) Key, DateTimeOffset SeenAt)> _order = new();

    public LineWebhookDeduplicator(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// Records <paramref name="webhookEventId"/> of <paramref name="assistantId"/> as accepted now and
    /// returns <see langword="true"/>, or returns <see langword="false"/> when it was already accepted
    /// within <see cref="Retention"/>.
    /// </summary>
    public bool TryAccept(Guid assistantId, string webhookEventId)
    {
        ArgumentException.ThrowIfNullOrEmpty(webhookEventId);
        var key = (assistantId, webhookEventId);
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            Expire(now);
            if (_seen.TryGetValue(key, out var seenAt) && now - seenAt < Retention)
            {
                return false;
            }

            while (_seen.Count >= Capacity && _order.TryDequeue(out var oldest))
            {
                Forget(oldest.Key, oldest.SeenAt);
            }

            _seen[key] = now;
            _order.Enqueue((key, now));
            return true;
        }
    }

    /// <summary>Forgets an id accepted by <see cref="TryAccept"/> whose event could not be queued after
    /// all, so a redelivery of it is handled.</summary>
    public void Release(Guid assistantId, string webhookEventId)
    {
        ArgumentException.ThrowIfNullOrEmpty(webhookEventId);
        lock (_lock)
        {
            _seen.Remove((assistantId, webhookEventId));
        }
    }

    private void Expire(DateTimeOffset now)
    {
        while (_order.TryPeek(out var oldest) && now - oldest.SeenAt >= Retention)
        {
            _order.Dequeue();
            Forget(oldest.Key, oldest.SeenAt);
        }
    }

    /// <summary>Removes the id only if it is still the entry of <paramref name="seenAt"/> (a released
    /// and accepted-again id has a newer one).</summary>
    private void Forget((Guid AssistantId, string EventId) key, DateTimeOffset seenAt)
    {
        if (_seen.TryGetValue(key, out var current) && current == seenAt)
        {
            _seen.Remove(key);
        }
    }
}
