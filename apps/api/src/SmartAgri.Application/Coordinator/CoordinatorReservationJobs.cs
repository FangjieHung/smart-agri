using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// The payload of a <see cref="Kind"/> background job (store-reservations design §3.1 point 7, Slice R2, #390): push the
/// new reservation's card to every member of its shop, enqueued with the reservation in one transaction. Only the id:
/// the job reads the row (and the shop's members) when it runs.
/// </summary>
public sealed record CoordinatorNotifyReservationJob(Guid ReservationId)
{
    public const string Kind = "coordinator.notify-reservation";
}

/// <summary>
/// The payload of a <see cref="Kind"/> background job (store-reservations design §3.1 point 6, Slice R2, #390): a pending
/// reservation nobody handled expires <c>pendingExpiryMinutes</c> before its time. Enqueued with the reservation, to run
/// then (<c>RunAfter</c>); the job reads the row and the shop's settings when it runs and changes nothing unless the
/// reservation is still pending and due.
/// </summary>
public sealed record CoordinatorExpireReservationJob(Guid ReservationId)
{
    public const string Kind = "coordinator.expire-reservation";
}

/// <summary>
/// The payload of a <see cref="Kind"/> background job (store-reservations design decisions R2, R3, R7, §3.1 point 9, Slice
/// R3, #391): Email the customer that their reservation was accepted, declined or expired (<paramref name="Outcome"/>),
/// enqueued by the compare-and-set that made it so, in its transaction — so exactly once per reservation. Only the id and
/// the outcome: the job reads the reservation (its address) and the shop's settings (name, phone, zone) when it runs, and
/// sends nothing unless the reservation still has that status.
/// </summary>
public sealed record CoordinatorReservationEmailJob(Guid ReservationId, CoordinatorReservationStatus Outcome)
{
    public const string Kind = "coordinator.reservation-email";
}
