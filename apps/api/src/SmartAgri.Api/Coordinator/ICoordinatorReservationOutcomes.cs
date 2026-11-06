using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Notifications;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Jobs;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// What happens once a reservation leaves <c>pending</c> (store-reservations design decisions R2, R7, §3.1 point 9): the
/// seam where the customer is told by Email that it was accepted, declined or expired (<see cref="EmailReservationOutcomes"/>,
/// Slice R3, #391).
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

/// <summary>
/// Slice R3's <see cref="ICoordinatorReservationOutcomes"/> (#391): a reservation accepted, declined or expired whose
/// customer left an Email address gets one <see cref="CoordinatorReservationEmailJob"/> — when the platform can send Email
/// (<see cref="IEmailSender.IsConfigured"/>); otherwise nothing is enqueued, and the members are told to call
/// (<see cref="CoordinatorReservationCards.DecidedText"/>). The job sends later, with the queue's retries: the press is
/// answered before any Email is tried.
/// </summary>
public sealed class EmailReservationOutcomes : ICoordinatorReservationOutcomes
{
    private readonly IEmailSender _email;

    public EmailReservationOutcomes(IEmailSender email)
    {
        _email = email;
    }

    public void Decided(AppDbContext dbContext, CoordinatorReservation reservation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.CustomerEmail is null || !CoordinatorReservationEmail.Covers(reservation.Status) || !_email.IsConfigured)
        {
            return;
        }

        dbContext.BackgroundJobs.Add(BackgroundJob.Create(
            reservation.OrganizationId,
            CoordinatorReservationEmailJob.Kind,
            new CoordinatorReservationEmailJob(reservation.Id, reservation.Status),
            // Npgsql writes timestamptz only from UTC.
            now.ToUniversalTime()));
    }
}
