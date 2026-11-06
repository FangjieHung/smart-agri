using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// What happens once a reservation leaves <c>pending</c> (store-reservations design decisions R2, R7, §3.1 point 9): the
/// seam where Slice R3 (#391) tells the customer by Email that it was accepted, declined or expired. Slice R2 (#390)
/// tells nobody (<see cref="NoReservationOutcomes"/>).
/// </summary>
/// <remarks>
/// <see cref="Decided"/> runs exactly once per reservation — by the compare-and-set that won
/// (<see cref="CoordinatorReservations.DecideAsync"/>, <see cref="CoordinatorReservations.ExpireAsync"/>) — inside its
/// transaction, so work added there (a <c>BackgroundJob</c> row that sends the Email) commits with the new status or not
/// at all: a press that lost the race, a repeated press or a retried expiry job adds nothing.
/// </remarks>
public interface ICoordinatorReservationOutcomes
{
    /// <summary>
    /// <paramref name="reservation"/> (as it is now: accepted or declined with its member and time, or expired) just
    /// left pending in <paramref name="dbContext"/>'s transaction, which acts for its shop: add what must follow exactly
    /// once to <paramref name="dbContext"/> — it is saved and committed with the change. Never send anything here.
    /// </summary>
    void Decided(AppDbContext dbContext, CoordinatorReservation reservation, DateTimeOffset now);
}

/// <summary>Slice R2's <see cref="ICoordinatorReservationOutcomes"/>: the customer is not told (Email is #391).</summary>
public sealed class NoReservationOutcomes : ICoordinatorReservationOutcomes
{
    public void Decided(AppDbContext dbContext, CoordinatorReservation reservation, DateTimeOffset now)
    {
    }
}
