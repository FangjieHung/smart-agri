using SmartAgri.Domain.Ordering;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Ordering;

/// <summary>
/// What follows once an order is answered (online-ordering design D5, Slice O3, #399): accepted, declined, or cancelled by
/// its customer or the shop — the seam where #400 tells the customer by Email (the same way as the reservations'
/// <c>ICoordinatorReservationOutcomes</c>). <see cref="NoOrderingOrderOutcomes"/> until then.
/// </summary>
/// <remarks>
/// <see cref="Decided"/> runs exactly once per change — by the compare-and-set that won (<see cref="OrderingStaffOrders.ActAsync"/>,
/// <see cref="OrderingOrders.CancelAsync"/>) — inside its transaction, so work added there (a <c>BackgroundJob</c> row)
/// commits with the new status or not at all: a press that lost the race or a repeated press adds nothing. Not called for
/// ready and completed (the status page shows them; nothing is sent).
/// </remarks>
public interface IOrderingOrderOutcomes
{
    /// <summary>
    /// <paramref name="order"/> (as it is now: accepted with its promised time, declined with its reason, or cancelled with
    /// who and why) just changed in <paramref name="dbContext"/>'s transaction, which acts for its shop: add what must follow
    /// exactly once to <paramref name="dbContext"/> — it is saved and committed with the change. Never send anything here.
    /// </summary>
    void Decided(AppDbContext dbContext, OrderingOrder order, DateTimeOffset now);
}

/// <summary>Slice O3's <see cref="IOrderingOrderOutcomes"/>: nothing follows yet (#400 adds the Email).</summary>
public sealed class NoOrderingOrderOutcomes : IOrderingOrderOutcomes
{
    public void Decided(AppDbContext dbContext, OrderingOrder order, DateTimeOffset now)
    {
    }
}
