using SmartAgri.Domain.Ordering;

namespace SmartAgri.Application.Ordering;

/// <summary>
/// The payload of a <see cref="Kind"/> background job (online-ordering design D5, Slice O4, #400): an order nobody answered
/// expires at its <see cref="OrderingOrder.ExpiresAt"/>. Enqueued with the order, to run then (<c>RunAfter</c>); the job
/// reads the row when it runs and changes nothing unless the order is still pending and due — a compare-and-set, so the
/// staff's press at the same moment and the expiry never both win.
/// </summary>
public sealed record OrderingExpireOrderJob(Guid OrderId)
{
    public const string Kind = "ordering.expire-order";
}

/// <summary>
/// The payload of a <see cref="Kind"/> background job (online-ordering design D5, Slice O4, #400): Email the customer that
/// their order was accepted, declined, cancelled (by the shop or by themselves) or expired (<paramref name="Outcome"/>),
/// enqueued by the compare-and-set that made it so, in its transaction — so exactly once per change. Only the id and the
/// outcome: the job reads the order (its address, lines and times) and the shop (name, phone, slug, zone) when it runs, and
/// sends nothing unless the order still has that status.
/// </summary>
public sealed record OrderingOrderEmailJob(Guid OrderId, OrderingOrderStatus Outcome)
{
    public const string Kind = "ordering.order-email";
}
