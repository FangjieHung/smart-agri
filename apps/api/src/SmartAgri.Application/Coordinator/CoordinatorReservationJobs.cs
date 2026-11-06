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
