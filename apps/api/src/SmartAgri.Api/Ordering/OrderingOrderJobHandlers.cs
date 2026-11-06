using Microsoft.Extensions.Options;
using SmartAgri.Api.Notifications;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Notifications;
using SmartAgri.Application.Ordering;
using SmartAgri.Domain;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Api.Ordering;

/// <summary>
/// Handles <see cref="OrderingExpireOrderJob.Kind"/> (online-ordering design D5, Slice O4, #400): an order still pending at
/// its <see cref="OrderingOrder.ExpiresAt"/> becomes <c>expired</c> — a compare-and-set
/// (<see cref="OrderingOrders.ExpireAsync"/>), so a member's press at the same moment and this job never both win; the
/// winner calls <see cref="IOrderingOrderOutcomes"/> (the customer's Email), so an expiry job run again adds none.
/// </summary>
/// <remarks>
/// The expiry instant is stored with the order when it is sent (<see cref="OrderingOrder.ExpiryFor"/>), so a later change
/// of the shop's <c>pendingTimeoutMinutes</c> applies to new orders only. A job that finds its order not due yet (it can
/// only run early by a clock change) schedules itself again at <see cref="OrderingOrder.ExpiresAt"/>. Logs carry the order
/// and organization ids and the order's number, never the customer's details.
/// </remarks>
internal sealed class OrderingExpireOrderHandler : IJobHandler
{
    private readonly OrderingOrders _orders;
    private readonly TimeProvider _clock;
    private readonly ILogger<OrderingExpireOrderHandler> _logger;

    public OrderingExpireOrderHandler(OrderingOrders orders, TimeProvider clock, ILogger<OrderingExpireOrderHandler> logger)
    {
        _orders = orders;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<OrderingExpireOrderJob>();
        var now = _clock.GetUtcNow();
        var result = await _orders.ExpireAsync(job.OrganizationId, payload.OrderId, now, cancellationToken);
        if (result.Outcome == OrderingExpireOutcome.NotDue)
        {
            await _orders.ScheduleExpiryAsync(result.Order!, now, cancellationToken);
        }

        _logger.LogInformation(
            "Order {OrderId} of organization {OrganizationId}: {Outcome}.",
            payload.OrderId,
            job.OrganizationId,
            result.Outcome switch
            {
                OrderingExpireOutcome.Expired => "expired, nobody answered it in time",
                OrderingExpireOutcome.Answered => "answered meanwhile, not expired",
                OrderingExpireOutcome.NotDue => "not due yet, rescheduled",
                _ => "gone",
            });
    }
}

/// <summary>
/// Handles <see cref="OrderingOrderEmailJob.Kind"/> (online-ordering design D5, Slice O4, #400): one plain-text Email to the
/// customer of an order that was accepted, declined, cancelled or expired (<see cref="OrderingOrderEmail.Compose"/>), from
/// the platform's <c>Smtp:From</c> under the shop's name, with the shop's phone and — when
/// <c>PublicChannels:PublicBaseUrl</c> is set — its ordering page; the order and the shop read when the job runs.
/// </summary>
/// <remarks>
/// <para>
/// Enqueued only by the compare-and-set that made the change (<see cref="EmailOrderingOrderOutcomes"/>), so there is one
/// job per change. Nothing to send completes the job without an Email: the order is gone (with its shop), it no longer has
/// the job's status (the shop cancelled an accepted order since: that change has its own Email), it has no address (none
/// left, or cleared by the retention, design D12), or SMTP is not configured any more. Any SMTP failure is retried by the
/// job queue (exponential backoff, up to the job's attempts) and never touches the order. There is no "sent" column (as
/// the reservations): a runner that stops between the SMTP server's acceptance and the job's success may send it once more.
/// </para>
/// <para>
/// A failure is rethrown as <see cref="OrderingEmailFailedException"/>, whose message — the job's <c>LastError</c>, and
/// logged — has the exception types and SMTP status only (<see cref="EmailFailures"/>): a server's reply can quote the
/// recipient's address. Logs carry the order and organization ids, the outcome and the attempt — never the customer's
/// address, name, phone or comments.
/// </para>
/// </remarks>
internal sealed class OrderingOrderEmailHandler : IJobHandler
{
    private readonly OrderingOrders _orders;
    private readonly IEmailSender _sender;
    private readonly PublicChannelsOptions _publicChannels;
    private readonly ILogger<OrderingOrderEmailHandler> _logger;

    public OrderingOrderEmailHandler(
        OrderingOrders orders, IEmailSender sender, IOptions<PublicChannelsOptions> publicChannels, ILogger<OrderingOrderEmailHandler> logger)
    {
        _orders = orders;
        _sender = sender;
        _publicChannels = publicChannels.Value;
        _logger = logger;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<OrderingOrderEmailJob>();
        var organizationId = job.OrganizationId;
        var outcome = WireNames<OrderingOrderStatus>.ToWire(payload.Outcome);
        var (order, shop) = await _orders.FindWithShopAsync(organizationId, payload.OrderId, cancellationToken);
        var why = order is null || shop is null ? "the order no longer exists"
            : order.Status != payload.Outcome ? $"it is {WireNames<OrderingOrderStatus>.ToWire(order.Status)} now"
            : order.CustomerEmail is null ? "the customer left no Email address"
            : !_sender.IsConfigured ? "SMTP is not configured"
            : null;
        if (why is not null)
        {
            _logger.LogInformation(
                "Order Email ({Outcome}) for order {OrderId} of organization {OrganizationId} not sent: {Why}.",
                outcome, payload.OrderId, organizationId, why);
            return;
        }

        var page = _publicChannels.ResolvedPublicBaseUrl is { } baseUrl ? $"{baseUrl}/order/{shop!.Slug}" : null;
        var message = OrderingOrderEmail.Compose(shop!.Name, shop.Phone, shop.Hours.Zone, page, order!);
        try
        {
            await _sender.SendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new OrderingEmailFailedException(
                $"The order Email ({outcome}) could not be sent (attempt {job.Attempt} of {job.MaxAttempts}): {EmailFailures.Summary(exception)}");
        }

        _logger.LogInformation(
            "Order Email ({Outcome}) for order {OrderId} of organization {OrganizationId} sent (attempt {Attempt}).",
            outcome, payload.OrderId, organizationId, job.Attempt);
    }

    public Task OnFinalFailureAsync(JobContext job, string error, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<OrderingOrderEmailJob>();
        _logger.LogError(
            "The Email job ({Outcome}) of order {OrderId} of organization {OrganizationId} gave up after {Attempts} attempts: {Error}",
            WireNames<OrderingOrderStatus>.ToWire(payload.Outcome), payload.OrderId, job.OrganizationId, job.Attempt, error);
        return Task.CompletedTask;
    }
}

/// <summary>An order Email the SMTP server did not take; retried by the job queue.</summary>
public sealed class OrderingEmailFailedException(string message) : Exception(message);
