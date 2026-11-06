using SmartAgri.Application.Notifications;
using SmartAgri.Application.Ordering;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Ordering;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Ordering;

/// <summary>
/// What follows once an order is answered (online-ordering design D5, Slices O3 and O4, #399, #400): accepted, declined,
/// cancelled by its customer or the shop, or expired unanswered — the seam where the customer is told by Email
/// (<see cref="EmailOrderingOrderOutcomes"/>, the same way as the reservations' <c>ICoordinatorReservationOutcomes</c>).
/// </summary>
/// <remarks>
/// <see cref="Decided"/> runs exactly once per change — by the compare-and-set that won (<see cref="OrderingStaffOrders.ActAsync"/>,
/// <see cref="OrderingOrders.CancelAsync"/>, <see cref="OrderingOrders.ExpireAsync"/>) — inside its transaction, so work
/// added there (a <c>BackgroundJob</c> row) commits with the new status or not at all: a press that lost the race, a
/// repeated press or an expiry job run again adds nothing. Not called for ready and completed (the status page shows them;
/// nothing is sent).
/// </remarks>
public interface IOrderingOrderOutcomes
{
    /// <summary>
    /// <paramref name="order"/> (as it is now: accepted with its promised time, declined with its reason, cancelled with
    /// who and why, or expired) just changed in <paramref name="dbContext"/>'s transaction, which acts for its shop: add
    /// what must follow exactly once to <paramref name="dbContext"/> — it is saved and committed with the change. Never
    /// send anything here.
    /// </summary>
    void Decided(AppDbContext dbContext, OrderingOrder order, DateTimeOffset now);
}

/// <summary>
/// Slice O4's <see cref="IOrderingOrderOutcomes"/> (#400): an order accepted, declined, cancelled or expired whose customer
/// left an Email address gets one <see cref="OrderingOrderEmailJob"/> — when the platform can send Email
/// (<see cref="IEmailSender.IsConfigured"/>, <see cref="OrderingOrderEmail.Sends"/>); otherwise nothing is enqueued and
/// the status page is the customer's only news. The job sends later, with the queue's retries: the press is answered
/// before any Email is tried, and an Email that fails never changes the order.
/// </summary>
public sealed class EmailOrderingOrderOutcomes : IOrderingOrderOutcomes
{
    private readonly IEmailSender _email;

    public EmailOrderingOrderOutcomes(IEmailSender email)
    {
        _email = email;
    }

    public void Decided(AppDbContext dbContext, OrderingOrder order, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(order);
        if (!OrderingOrderEmail.Sends(order, _email.IsConfigured))
        {
            return;
        }

        dbContext.BackgroundJobs.Add(BackgroundJob.Create(
            order.OrganizationId,
            OrderingOrderEmailJob.Kind,
            new OrderingOrderEmailJob(order.Id, order.Status),
            // Npgsql writes timestamptz only from UTC.
            now.ToUniversalTime()));
    }
}
