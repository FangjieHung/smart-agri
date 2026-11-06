using System.Text.Json.Nodes;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Application.Notifications;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// A member's reservation buttons and 「訂位」 (store-reservations design decisions R2, R5, §3.1 points 6 and 8, Slice R2,
/// #390), for <see cref="CoordinatorEventHandler"/>: each method returns the messages to reply with — a reply, free.
/// </summary>
/// <remarks>
/// <para>
/// 〔接受〕／〔婉拒〕: the postback's organization is checked against the sender's memberships, the reservation is read in
/// that shop's own context (another shop's id is 「找不到」), and the change is a compare-and-set on
/// (<c>pending</c>, <c>Version</c>) with the member still in the shop (<see cref="CoordinatorReservations.DecideAsync"/>):
/// of two members pressing at the same moment exactly one wins; the other — and every later press — is told who did
/// what when (「這筆訂位已由{誰}在 {時間} {接受／婉拒}。」: the member's LINE display name from the coordinator
/// account's profile API, free, else 「老闆」／「店長」; 「你」 for the member's own press; 「其他成員」 for one who left).
/// A pending reservation pressed at or after its expiry (the job not run yet) expires right then and says so. The winner
/// is also told what happens to the customer (#391): 「會寄確認信給顧客。」／「會寄婉拒信給顧客。」, or — no address, or no
/// SMTP — 「…請來電 {電話}。」 (<see cref="CoordinatorReservationCards.DecidedText"/>); the Email itself is a job the
/// compare-and-set enqueued (<see cref="EmailReservationOutcomes"/>), sent after this reply.
/// </para>
/// <para>
/// 「訂位」: the shop's pending and accepted reservations of the business days from today (the shop's zone) for
/// <see cref="CoordinatorReservationCards.ListDays"/> days, earliest first (also the small hours of yesterday's overnight
/// day still ahead today), as a carousel of at most 12; of several shops, a quick-reply choice of the shop first, as for
/// a 店長 binding code. Logs carry ids and the decision, never the customer's details or a LINE id.
/// </para>
/// </remarks>
public sealed class CoordinatorReservationButtons
{
    private readonly CoordinatorReservations _reservations;
    private readonly CoordinatorShops _shops;
    private readonly CoordinatorReplySender _sender;
    private readonly IEmailSender _email;
    private readonly TimeProvider _clock;
    private readonly ILogger<CoordinatorReservationButtons> _logger;

    public CoordinatorReservationButtons(
        CoordinatorReservations reservations,
        CoordinatorShops shops,
        CoordinatorReplySender sender,
        IEmailSender email,
        TimeProvider clock,
        ILogger<CoordinatorReservationButtons> logger)
    {
        _reservations = reservations;
        _shops = shops;
        _sender = sender;
        _email = email;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The shop's <see cref="ShopReservationSettings.PendingExpiryMinutes"/> now (the default when it has no
    /// reservation settings any more).</summary>
    public static async Task<int> PendingExpiryMinutesAsync(CoordinatorShops shops, Guid organizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shops);
        return (await shops.FindAsync(organizationId, cancellationToken))?.Reservations?.PendingExpiryMinutes
            ?? ShopReservationSettings.DefaultPendingExpiryMinutes;
    }

    /// <summary>「訂位」: the list of the one shop, or which shop of several.</summary>
    public Task<IReadOnlyList<JsonObject>> ListAsync(IReadOnlyList<CoordinatorMembership> memberships, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        return memberships.Count > 1
            ? Task.FromResult<IReadOnlyList<JsonObject>>(
                [CoordinatorReservationCards.ShopChoice([.. memberships.Select(membership => (membership.OrganizationId, membership.StoreName))])])
            : ListOfAsync(memberships[0], cancellationToken);
    }

    /// <summary>The list of the shop chosen from <see cref="CoordinatorReservationCards.ShopChoice"/>.</summary>
    public async Task<IReadOnlyList<JsonObject>> ListForAsync(
        IReadOnlyList<CoordinatorMembership> memberships, Guid organizationId, CancellationToken cancellationToken) =>
        memberships.FirstOrDefault(membership => membership.OrganizationId == organizationId) is { } membership
            ? await ListOfAsync(membership, cancellationToken)
            : Say(CoordinatorReservationCards.NotAMember);

    /// <summary>〔接受〕 (<paramref name="decision"/> <see cref="CoordinatorReservationStatus.Accepted"/>) or 〔婉拒〕.</summary>
    public async Task<IReadOnlyList<JsonObject>> DecideAsync(
        string lineUserId,
        IReadOnlyList<CoordinatorMembership> memberships,
        Guid organizationId,
        Guid reservationId,
        CoordinatorReservationStatus decision,
        CancellationToken cancellationToken)
    {
        if (memberships.All(membership => membership.OrganizationId != organizationId))
        {
            return Say(CoordinatorReservationCards.NotAMember);
        }

        var members = await _reservations.MembersAsync(organizationId, cancellationToken);
        if (members.FirstOrDefault(member => member.LineUserId == lineUserId) is not { } presser)
        {
            return Say(CoordinatorReservationCards.NotAMember);
        }

        var settings = await _shops.FindAsync(organizationId, cancellationToken);
        var zone = settings?.TimeZone ?? TimeZoneInfo.FindSystemTimeZoneById(ShopSettings.DefaultTimeZoneId);
        var minutes = settings?.Reservations?.PendingExpiryMinutes ?? ShopReservationSettings.DefaultPendingExpiryMinutes;

        // A compare-and-set that lost reads the row again: at most a few rounds (another press, the expiry job).
        for (var round = 0; round < 3; round++)
        {
            var reservation = await _reservations.FindAsync(organizationId, reservationId, cancellationToken);
            if (reservation is null)
            {
                return Say(CoordinatorReservationCards.Unknown);
            }

            if (reservation.Status != CoordinatorReservationStatus.Pending)
            {
                return Say(await WhyNotAsync(reservation, presser, members, zone, minutes, cancellationToken));
            }

            var now = _clock.GetUtcNow();
            if (!reservation.IsOpenAt(now, minutes))
            {
                if (await _reservations.ExpireAsync(organizationId, reservation.Id, reservation.Version, now, cancellationToken) is not null)
                {
                    _logger.LogInformation(
                        "Reservation {ReservationId} of organization {OrganizationId} expired when a member pressed it: nobody handled it {Minutes} minutes before its time.",
                        reservation.Id, organizationId, minutes);
                    return Say(CoordinatorReservationCards.Expired(minutes));
                }

                continue;
            }

            if (await _reservations.DecideAsync(organizationId, reservation.Id, reservation.Version, decision, presser.Id, now, cancellationToken) is { } decided)
            {
                _logger.LogInformation(
                    "Reservation {ReservationId} of organization {OrganizationId} {Decision} by member {MemberId}.",
                    decided.Id, organizationId, Domain.WireNames<CoordinatorReservationStatus>.ToWire(decision), presser.Id);
                // The same check as the Email's (EmailReservationOutcomes): the reply says whether the customer will
                // get it or must be called (#391).
                return [CoordinatorReservationCards.Decided(decided, zone, _email.IsConfigured)];
            }
        }

        return Say(CoordinatorReservationCards.Unknown);
    }

    private async Task<IReadOnlyList<JsonObject>> ListOfAsync(CoordinatorMembership membership, CancellationToken cancellationToken)
    {
        var settings = await _shops.FindAsync(membership.OrganizationId, cancellationToken);
        if (settings is null)
        {
            return Say($"{membership.StoreName}：{CoordinatorReservationCards.NoReservations}");
        }

        var hours = settings.Hours;
        var today = hours.DateAt(_clock.GetUtcNow());
        var reservations = await _reservations.OpenBetweenAsync(
            membership.OrganizationId, today, today.AddDays(CoordinatorReservationCards.ListDays), hours.At(today, TimeOnly.MinValue), cancellationToken);
        return CoordinatorReservationCards.List(membership.StoreName, settings.TimeZone, reservations);
    }

    /// <summary>Why a press changed nothing: who handled it and when, it expired, or it was cancelled.</summary>
    private async Task<string> WhyNotAsync(
        CoordinatorReservation reservation,
        CoordinatorMember presser,
        IReadOnlyList<CoordinatorMember> members,
        TimeZoneInfo zone,
        int minutes,
        CancellationToken cancellationToken)
    {
        switch (reservation.Status)
        {
            case CoordinatorReservationStatus.Accepted or CoordinatorReservationStatus.Declined:
                var who = reservation.HandledByMemberId == presser.Id
                    ? CoordinatorReservationCards.You
                    : members.FirstOrDefault(member => member.Id == reservation.HandledByMemberId) is { } handler
                        ? await _sender.DisplayNameAsync(handler.LineUserId, cancellationToken) ?? CoordinatorRoleNames.Display(handler.Role)
                        : CoordinatorReservationCards.FormerMember;
                return CoordinatorReservationCards.AlreadyHandled(reservation.Status, who, reservation.HandledAt!.Value, zone);
            case CoordinatorReservationStatus.Expired:
                return CoordinatorReservationCards.Expired(minutes);
            default:
                return CoordinatorReservationCards.Cancelled;
        }
    }

    private static IReadOnlyList<JsonObject> Say(string text) => [LineMessages.Text(text)];
}
