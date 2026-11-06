using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Notifications;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Notifications;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// Handles <see cref="CoordinatorNotifyReservationJob.Kind"/> (store-reservations design decision R5, §3.1 point 7, Slice
/// R2, #390): pushes the new reservation's card (<see cref="CoordinatorReservationCards.NewReservation"/>, which says
/// 「請來電確認」 when nobody else will tell the customer, #391) to every current member of its shop, 老闆 and 店長 alike,
/// each push counting against the coordinator account's monthly quota.
/// </summary>
/// <remarks>
/// <para>
/// Each (reservation, member) has its own retry key (<see cref="CardRetryKey"/>), so a run again — the queue's retry,
/// or a runner that stopped before recording the job — never pushes a member twice: LINE answers <c>409</c>. One member
/// LINE refuses (blocked the account, gone) does not stop the others. <see cref="CoordinatorReservation.MembersNotified"/>
/// becomes <c>sent</c> as soon as one member has the card, <c>not-sent</c> (「未通知成員」) when nobody got it: no
/// members, every push refused, the 統籌助理 not configured, or the account's monthly limit reached.
/// </para>
/// <para>
/// A failure that may pass (a rate limit, a timeout, LINE's own error) of any member is retried by the queue with its
/// backoff, until the job's last attempt; the monthly limit is not — LINE refuses every push until next month — so it is
/// logged as a warning once and the reservation stays stored, not-sent. Logs carry the reservation and organization ids
/// and counts: never the customer's name, phone, Email or note, never a LINE id.
/// </para>
/// </remarks>
internal sealed class CoordinatorNotifyReservationHandler : IJobHandler
{
    private readonly CoordinatorReservations _reservations;
    private readonly CoordinatorShops _shops;
    private readonly CoordinatorReplySender _sender;
    private readonly IEmailSender _email;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<CoordinatorNotifyReservationHandler> _logger;

    public CoordinatorNotifyReservationHandler(
        CoordinatorReservations reservations,
        CoordinatorShops shops,
        CoordinatorReplySender sender,
        IEmailSender email,
        AppDbContext dbContext,
        ILogger<CoordinatorNotifyReservationHandler> logger)
    {
        _reservations = reservations;
        _shops = shops;
        _sender = sender;
        _email = email;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>The retry key of <paramref name="reservationId"/>'s card to member <paramref name="memberId"/>: the same on
    /// every run, different for every pair (a UUID from SHA-256; LINE takes any).</summary>
    public static Guid CardRetryKey(Guid reservationId, Guid memberId)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes($"coordinator-reservation-card:{reservationId:N}:{memberId:N}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        // RFC 9562 version 8 (custom) and variant 10xx, so it reads as a well-formed UUID.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<CoordinatorNotifyReservationJob>();
        var organizationId = job.OrganizationId;
        var reservation = await _reservations.FindAsync(organizationId, payload.ReservationId, cancellationToken);
        if (reservation is null)
        {
            _logger.LogInformation("Reservation {ReservationId} of organization {OrganizationId} is gone: no card pushed.", payload.ReservationId, organizationId);
            return;
        }

        if (reservation.Status != CoordinatorReservationStatus.Pending)
        {
            // Handled (from 「訂位」) or expired before its card went out: nothing left to ask.
            await _reservations.MarkNotifiedAsync(organizationId, reservation.Id, CoordinatorReservationNotice.NotSent, cancellationToken);
            _logger.LogInformation(
                "Reservation {ReservationId} of organization {OrganizationId} is {Status} already: no card pushed.",
                reservation.Id, organizationId, Domain.WireNames<CoordinatorReservationStatus>.ToWire(reservation.Status));
            return;
        }

        var settings = await _shops.FindAsync(organizationId, cancellationToken);
        var members = await _reservations.MembersAsync(organizationId, cancellationToken);
        if (settings is null || members.Count == 0)
        {
            await _reservations.MarkNotifiedAsync(organizationId, reservation.Id, CoordinatorReservationNotice.NotSent, cancellationToken);
            _logger.LogWarning(
                "Reservation {ReservationId} of organization {OrganizationId}: nobody to notify ({Why}); 未通知成員.",
                reservation.Id, organizationId, settings is null ? "the shop has no settings" : "the shop has no members");
            return;
        }

        // Whether the platform will Email the customer (#391): else the card asks the members to call.
        var card = CoordinatorReservationCards.NewReservation(settings.StoreName, settings.TimeZone, reservation, _email.IsConfigured);
        int delivered = 0, failed = 0, refused = 0;
        var monthlyLimit = false;
        foreach (var member in members)
        {
            var pushed = await _sender.PushAsync(
                organizationId, member.LineUserId, [card], CardRetryKey(reservation.Id, member.Id), CoordinatorPushPurpose.Reservation, cancellationToken);
            switch (pushed.Outcome)
            {
                case CoordinatorPushOutcome.Sent or CoordinatorPushOutcome.AlreadySent:
                    delivered++;
                    break;
                case CoordinatorPushOutcome.MonthlyLimit:
                    monthlyLimit = true;
                    break;
                case CoordinatorPushOutcome.Failed:
                    failed++;
                    break;
                default:
                    refused++;
                    break;
            }

            if (monthlyLimit)
            {
                // LINE refuses every push this month: the others would only be refused too.
                break;
            }
        }

        if (delivered > 0)
        {
            await _reservations.MarkNotifiedAsync(organizationId, reservation.Id, CoordinatorReservationNotice.Sent, cancellationToken);
        }

        if (monthlyLimit)
        {
            if (delivered == 0)
            {
                await _reservations.MarkNotifiedAsync(organizationId, reservation.Id, CoordinatorReservationNotice.NotSent, cancellationToken);
            }

            _logger.LogWarning(
                "Reservation {ReservationId} of organization {OrganizationId}: the 統籌助理's monthly message limit is reached; "
                + "its card reached {Delivered} of {Members} members{NotSent}. The reservation is kept; not retried.",
                reservation.Id, organizationId, delivered, members.Count, delivered == 0 ? " (未通知成員)" : string.Empty);
            return;
        }

        if (failed > 0 && !job.IsLastAttempt)
        {
            _logger.LogWarning(
                "Reservation {ReservationId} of organization {OrganizationId}: its card reached {Delivered} of {Members} members, {Failed} failed for now; retrying.",
                reservation.Id, organizationId, delivered, members.Count, failed);
            throw new CoordinatorPushRetryException($"{failed} of {members.Count} members could not be pushed the reservation card yet.");
        }

        if (delivered == 0)
        {
            await _reservations.MarkNotifiedAsync(organizationId, reservation.Id, CoordinatorReservationNotice.NotSent, cancellationToken);
        }

        var level = delivered == members.Count ? LogLevel.Information : LogLevel.Warning;
        _logger.Log(
            level,
            "Reservation {ReservationId} of organization {OrganizationId}: card pushed to {Delivered} of {Members} members ({Refused} refused, {Failed} failed){NotSent}.",
            reservation.Id, organizationId, delivered, members.Count, refused, failed, delivered == 0 ? "; 未通知成員" : string.Empty);
    }

    public async Task OnFinalFailureAsync(JobContext job, string error, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<CoordinatorNotifyReservationJob>();
        var marked = await _dbContext.CoordinatorReservations
            .Where(reservation => reservation.Id == payload.ReservationId && reservation.MembersNotified == CoordinatorReservationNotice.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(reservation => reservation.MembersNotified, CoordinatorReservationNotice.NotSent), cancellationToken);
        _logger.LogError(
            "The card job of reservation {ReservationId} gave up after {Attempts} attempts{Marked}.",
            payload.ReservationId, job.Attempt, marked == 1 ? "; 未通知成員" : string.Empty);
    }
}

/// <summary>Some members could not be pushed a reservation's card for now: the queue tries the job again (the members
/// who have it get LINE's <c>409</c> for their retry key, not a second card).</summary>
internal sealed class CoordinatorPushRetryException(string message) : Exception(message);

/// <summary>
/// Handles <see cref="CoordinatorExpireReservationJob.Kind"/> (store-reservations design decision R7, §3.1 point 6, Slice
/// R2, #390): a reservation still pending <c>pendingExpiryMinutes</c> before its time becomes <c>expired</c> — a
/// compare-and-set, so a member's press at the same moment and this job never both win; the winner calls
/// <see cref="ICoordinatorReservationOutcomes"/> (the customer's expiry Email, #391), so an expiry job run again adds none.
/// </summary>
/// <remarks>
/// The time is read from the shop's settings when the job runs: when <c>pendingExpiryMinutes</c> was lowered since the
/// reservation was stored, the job finds it not due yet and schedules itself again at the new time (raised: the job runs
/// late, and a member's press after the new time is answered 「已過期」 and expires it then, <see cref="CoordinatorReservationButtons"/>).
/// </remarks>
internal sealed class CoordinatorExpireReservationHandler : IJobHandler
{
    private readonly CoordinatorReservations _reservations;
    private readonly CoordinatorShops _shops;
    private readonly TimeProvider _clock;
    private readonly ILogger<CoordinatorExpireReservationHandler> _logger;

    public CoordinatorExpireReservationHandler(
        CoordinatorReservations reservations, CoordinatorShops shops, TimeProvider clock, ILogger<CoordinatorExpireReservationHandler> logger)
    {
        _reservations = reservations;
        _shops = shops;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<CoordinatorExpireReservationJob>();
        var organizationId = job.OrganizationId;
        var reservation = await _reservations.FindAsync(organizationId, payload.ReservationId, cancellationToken);
        if (reservation is not { Status: CoordinatorReservationStatus.Pending })
        {
            return;
        }

        var minutes = await CoordinatorReservationButtons.PendingExpiryMinutesAsync(_shops, organizationId, cancellationToken);
        var due = reservation.ExpiresAt(minutes);
        var now = _clock.GetUtcNow();
        if (now < due)
        {
            await _reservations.ScheduleExpiryAsync(organizationId, reservation.Id, due, now, cancellationToken);
            _logger.LogInformation(
                "Reservation {ReservationId} of organization {OrganizationId} expires later now ({Minutes} minutes before its time): rescheduled.",
                reservation.Id, organizationId, minutes);
            return;
        }

        var expired = await _reservations.ExpireAsync(organizationId, reservation.Id, reservation.Version, now, cancellationToken);
        _logger.LogInformation(
            "Reservation {ReservationId} of organization {OrganizationId} {Outcome}.",
            reservation.Id, organizationId, expired is null ? "was handled meanwhile: not expired" : $"expired: nobody handled it {minutes} minutes before its time");
    }
}

/// <summary>
/// Handles <see cref="CoordinatorReservationEmailJob.Kind"/> (store-reservations design decisions R2, R3, R6, R7, §3.1 point
/// 9, Slices R3 and R4, #391, #392): one plain-text Email to the customer of a reservation that was accepted, declined,
/// expired or cancelled by a closure (<see cref="CoordinatorReservationEmail.Compose"/>; a cancellation says why, from the
/// cancelling command's scenario and time), from the platform's <c>Smtp:From</c> under the shop's name, with the shop's
/// phone — the reservation, the command and the shop's settings read when the job runs.
/// </summary>
/// <remarks>
/// <para>
/// Enqueued only by the compare-and-set that took the reservation out of pending, or that cancelled it
/// (<see cref="EmailReservationOutcomes"/>), so there is one job per change. Nothing to send completes the job without an
/// Email: the reservation is gone
/// (deleted with its shop, or by the retention sweep), it no longer has the job's status (a closure cancelled it since,
/// Slice R4), it has no address, the shop has no settings, or SMTP is not configured any more. Any SMTP failure is retried
/// by the job queue like the 轉真人 Email (exponential backoff, <c>Jobs:RetryBaseDelay</c>, up to the job's attempts); the
/// member's press was answered long before. There is no "sent" column (#389's decision): a runner that stops between the
/// SMTP server's acceptance and the job's success may send it once more.
/// </para>
/// <para>
/// A failure is rethrown as <see cref="ReservationEmailFailedException"/>, whose message — the job's <c>LastError</c>, and
/// logged — has the exception types and SMTP status only (<see cref="EmailFailures"/>): a server's reply can
/// quote the recipient's address. Logs carry the reservation and organization ids, the outcome and the attempt — never the
/// customer's address, name, phone or note.
/// </para>
/// </remarks>
internal sealed class CoordinatorReservationEmailHandler : IJobHandler
{
    private readonly CoordinatorReservations _reservations;
    private readonly CoordinatorShops _shops;
    private readonly CoordinatorCommands _commands;
    private readonly IEmailSender _sender;
    private readonly ILogger<CoordinatorReservationEmailHandler> _logger;

    public CoordinatorReservationEmailHandler(
        CoordinatorReservations reservations,
        CoordinatorShops shops,
        CoordinatorCommands commands,
        IEmailSender sender,
        ILogger<CoordinatorReservationEmailHandler> logger)
    {
        _reservations = reservations;
        _shops = shops;
        _commands = commands;
        _sender = sender;
        _logger = logger;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<CoordinatorReservationEmailJob>();
        var organizationId = job.OrganizationId;
        var outcome = Domain.WireNames<CoordinatorReservationStatus>.ToWire(payload.Outcome);
        var reservation = await _reservations.FindAsync(organizationId, payload.ReservationId, cancellationToken);
        var why = reservation is null ? "the reservation no longer exists"
            : reservation.Status != payload.Outcome ? $"it is {Domain.WireNames<CoordinatorReservationStatus>.ToWire(reservation.Status)} now"
            : reservation.CustomerEmail is null ? "the customer left no Email address"
            : !_sender.IsConfigured ? "SMTP is not configured"
            : null;
        if (why is not null)
        {
            _logger.LogInformation(
                "Reservation Email ({Outcome}) for reservation {ReservationId} of organization {OrganizationId} not sent: {Why}.",
                outcome, payload.ReservationId, organizationId, why);
            return;
        }

        var settings = await _shops.FindAsync(organizationId, cancellationToken);
        if (settings is null)
        {
            _logger.LogWarning(
                "Reservation Email ({Outcome}) for reservation {ReservationId} of organization {OrganizationId} not sent: the shop has no settings.",
                outcome, payload.ReservationId, organizationId);
            return;
        }

        var message = CoordinatorReservationEmail.Compose(
            settings.StoreName, settings.Reservations?.Phone, settings.TimeZone, reservation!, await CancelReasonAsync(reservation!, cancellationToken));
        try
        {
            await _sender.SendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new ReservationEmailFailedException(
                $"The reservation Email ({outcome}) could not be sent (attempt {job.Attempt} of {job.MaxAttempts}): {EmailFailures.Summary(exception)}");
        }

        _logger.LogInformation(
            "Reservation Email ({Outcome}) for reservation {ReservationId} of organization {OrganizationId} sent (attempt {Attempt}).",
            outcome, payload.ReservationId, organizationId, job.Attempt);
    }

    /// <summary>Why a cancelled reservation was cancelled (「臨時店休」, 「當天提早在 20:00 打烊」, …, #392); <see langword="null"/>
    /// for the other outcomes.</summary>
    private async Task<string?> CancelReasonAsync(CoordinatorReservation reservation, CancellationToken cancellationToken)
    {
        if (reservation.Status != CoordinatorReservationStatus.Cancelled)
        {
            return null;
        }

        var command = reservation.CancelledByCommandId is { } commandId
            ? await _commands.FindAsync(reservation.OrganizationId, commandId, cancellationToken)
            : null;
        return CoordinatorReservationClosures.Reason(reservation.CancelReason ?? ShopScenario.TemporaryClosure, command?.Time);
    }

    public Task OnFinalFailureAsync(JobContext job, string error, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<CoordinatorReservationEmailJob>();
        _logger.LogError(
            "The Email job ({Outcome}) of reservation {ReservationId} of organization {OrganizationId} gave up after {Attempts} attempts: {Error}",
            Domain.WireNames<CoordinatorReservationStatus>.ToWire(payload.Outcome), payload.ReservationId, job.OrganizationId, job.Attempt, error);
        return Task.CompletedTask;
    }
}

/// <summary>A reservation Email the SMTP server did not take; retried by the job queue.</summary>
public sealed class ReservationEmailFailedException(string message) : Exception(message);
