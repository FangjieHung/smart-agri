using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Jobs;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Persistence;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>How a submitted reservation went (<see cref="CoordinatorReservations.SubmitAsync"/>).</summary>
public enum ReservationSubmitOutcome
{
    /// <summary>Stored as a new pending reservation.</summary>
    Stored,

    /// <summary>The same form was sent before: that reservation (<see cref="ReservationSubmitResult.Reservation"/>), nothing new.</summary>
    Duplicate,

    /// <summary>The phone number already has <see cref="CoordinatorReservation.MaxPendingPerPhone"/> upcoming pending
    /// reservations at the shop: nothing stored.</summary>
    TooManyPending,

    /// <summary>Another shop's reservation has the form's key (a forged or reused key): nothing stored.</summary>
    KeyTaken,

    /// <summary>A confirmed 臨時店休、提早打烊 or 延後開店 took the time away since the form was checked
    /// (<see cref="ReservationSubmitResult.TakenBy"/>, #392): nothing stored.</summary>
    SlotGone,
}

/// <param name="Reservation">The stored reservation (<see cref="ReservationSubmitOutcome.Stored"/>, the one sent before
/// for <see cref="ReservationSubmitOutcome.Duplicate"/>); <see langword="null"/> otherwise.</param>
/// <param name="TakenBy">The command that took the time away (<see cref="ReservationSubmitOutcome.SlotGone"/>).</param>
public sealed record ReservationSubmitResult(ReservationSubmitOutcome Outcome, CoordinatorReservation? Reservation, ShopCommandFact? TakenBy = null);

/// <summary>
/// A shop's reservations (store-reservations design §3.1 points 5–8 and 11, #389, #390), each operation in a context
/// acting for that organization (<see cref="FixedOrganizationContext"/>), under the usual filter and write guard.
/// </summary>
/// <remarks>
/// Every change of status is a compare-and-set on (<c>Status</c> pending, <c>Version</c>), so of any number of presses
/// and the expiry job exactly one moves a reservation out of pending; that one also calls
/// <see cref="ICoordinatorReservationOutcomes"/> in the same transaction (the seam of #391's Email). A confirmed closure
/// (#392) cancels a pending or accepted one the same way (<see cref="CancelTakenAwayAsync"/>), in the transaction that
/// starts the command.
/// </remarks>
public sealed class CoordinatorReservations
{
    /// <summary>EF Core's name for the unique index on <c>CoordinatorReservations.SubmissionKey</c>.</summary>
    internal const string SubmissionKeyIndex = "IX_CoordinatorReservations_SubmissionKey";

    private readonly DbContextOptions<AppDbContext> _options;
    private readonly ICoordinatorReservationOutcomes _outcomes;

    public CoordinatorReservations(DbContextOptions<AppDbContext> options, ICoordinatorReservationOutcomes outcomes)
    {
        _options = options;
        _outcomes = outcomes;
    }

    /// <summary>The shop's reservation sent with the form key <paramref name="submissionKey"/>, if any.</summary>
    public async Task<CoordinatorReservation?> FindBySubmissionKeyAsync(Guid organizationId, string submissionKey, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorReservations.AsNoTracking()
            .SingleOrDefaultAsync(reservation => reservation.SubmissionKey == submissionKey, cancellationToken);
    }

    /// <summary>
    /// Stores <paramref name="reservation"/> unless its form was sent before (<see cref="ReservationSubmitOutcome.Duplicate"/>),
    /// a confirmed 店休-type command of its business day takes its time away (<see cref="ReservationSubmitOutcome.SlotGone"/>,
    /// #392), or its phone number already has <see cref="CoordinatorReservation.MaxPendingPerPhone"/> pending reservations at
    /// the shop whose time is still ahead at <paramref name="now"/> (<see cref="ReservationSubmitOutcome.TooManyPending"/>).
    /// </summary>
    /// <remarks>
    /// One transaction that first locks the shop's settings row (an update that changes nothing), so the submissions of one
    /// shop are checked and stored one at a time: two forms of the same phone sent at once cannot both be the third. The
    /// closure check reads the shop's hours and the day's confirmed commands after the lock
    /// (<see cref="ReservationSlots.TakenBy"/>, reopens honoured), as <see cref="CancelTakenAwayAsync"/> reads the day's
    /// reservations after the same lock: a command is confirmed before its cancellation starts, so whichever of the two takes
    /// the lock first, a reservation in a time taken away is either refused here or cancelled there. A
    /// stored reservation comes with its two jobs (#390): pushing its card to the shop's members now
    /// (<see cref="CoordinatorNotifyReservationJob"/>), and expiring it <paramref name="pendingExpiryMinutes"/> before its
    /// time if nobody handled it (<see cref="CoordinatorExpireReservationJob"/>).
    /// </remarks>
    public async Task<ReservationSubmitResult> SubmitAsync(
        CoordinatorReservation reservation, int pendingExpiryMinutes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        await using var dbContext = ForOrganization(reservation.OrganizationId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.CoordinatorShopSettings.ExecuteUpdateAsync(
            setters => setters.SetProperty(settings => settings.UpdatedAt, settings => settings.UpdatedAt), cancellationToken);

        var sentBefore = await dbContext.CoordinatorReservations.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.SubmissionKey == reservation.SubmissionKey, cancellationToken);
        if (sentBefore is not null)
        {
            return new ReservationSubmitResult(ReservationSubmitOutcome.Duplicate, sentBefore);
        }

        if (await dbContext.CoordinatorShopSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) is { } settings)
        {
            var commands = await dbContext.CoordinatorCommands.AsNoTracking()
                .Where(command => command.Status == CoordinatorCommandStatus.Confirmed && command.Date == reservation.Date)
                .ToListAsync(cancellationToken);
            if (ReservationSlots.TakenBy(settings.Document.Hours, commands.Select(ShopCommandFact.Of), reservation.Date, reservation.ReservedAt) is { } takenBy)
            {
                return new ReservationSubmitResult(ReservationSubmitOutcome.SlotGone, null, takenBy);
            }
        }

        var pending = await dbContext.CoordinatorReservations.CountAsync(
            candidate => candidate.CustomerPhone == reservation.CustomerPhone
                && candidate.Status == CoordinatorReservationStatus.Pending
                && candidate.ReservedAt >= now,
            cancellationToken);
        if (pending >= CoordinatorReservation.MaxPendingPerPhone)
        {
            return new ReservationSubmitResult(ReservationSubmitOutcome.TooManyPending, null);
        }

        dbContext.CoordinatorReservations.Add(reservation);
        dbContext.BackgroundJobs.Add(BackgroundJob.Create(
            reservation.OrganizationId, CoordinatorNotifyReservationJob.Kind, new CoordinatorNotifyReservationJob(reservation.Id), now));
        dbContext.BackgroundJobs.Add(ExpiryJob(reservation.OrganizationId, reservation.Id, reservation.ExpiresAt(pendingExpiryMinutes), now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, SubmissionKeyIndex))
        {
            // Not this shop's (its rows are checked above under the lock): another shop's form key.
            return new ReservationSubmitResult(ReservationSubmitOutcome.KeyTaken, null);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReservationSubmitResult(ReservationSubmitOutcome.Stored, reservation);
    }

    /// <summary>The shop's reservations whose time is at or after <paramref name="from"/>, earliest first, at most
    /// <paramref name="limit"/> (the operator's list).</summary>
    public async Task<IReadOnlyList<CoordinatorReservation>> FromAsync(Guid organizationId, DateTimeOffset from, int limit, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorReservations.AsNoTracking()
            .Where(reservation => reservation.ReservedAt >= from)
            .OrderBy(reservation => reservation.ReservedAt)
            .ThenBy(reservation => reservation.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The shop's reservations whose time is from <paramref name="from"/> to before <paramref name="until"/>,
    /// latest first, at most <paramref name="limit"/> (the operator's list of recent ones).</summary>
    public async Task<IReadOnlyList<CoordinatorReservation>> BetweenAsync(
        Guid organizationId, DateTimeOffset from, DateTimeOffset until, int limit, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorReservations.AsNoTracking()
            .Where(reservation => reservation.ReservedAt >= from && reservation.ReservedAt < until)
            .OrderByDescending(reservation => reservation.ReservedAt)
            .ThenByDescending(reservation => reservation.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The shop's reservation <paramref name="reservationId"/>, if it exists.</summary>
    public async Task<CoordinatorReservation?> FindAsync(Guid organizationId, Guid reservationId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorReservations.AsNoTracking()
            .SingleOrDefaultAsync(reservation => reservation.Id == reservationId, cancellationToken);
    }

    /// <summary>
    /// The shop's pending and accepted reservations on the business days <paramref name="from"/> to before
    /// <paramref name="until"/>, earliest first — and those of the business day before <paramref name="from"/> whose time is
    /// at or after <paramref name="startOfFrom"/> (the small hours of an overnight day, already on <paramref name="from"/>'s
    /// calendar date).
    /// </summary>
    public async Task<IReadOnlyList<CoordinatorReservation>> OpenBetweenAsync(
        Guid organizationId, DateOnly from, DateOnly until, DateTimeOffset startOfFrom, CancellationToken cancellationToken)
    {
        var dayBefore = from.AddDays(-1);
        // Npgsql writes timestamptz only from UTC (the shop's midnight comes at its own offset).
        var startOfFromUtc = startOfFrom.ToUniversalTime();
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorReservations.AsNoTracking()
            .Where(reservation => (reservation.Status == CoordinatorReservationStatus.Pending || reservation.Status == CoordinatorReservationStatus.Accepted)
                && reservation.Date >= dayBefore
                && reservation.Date < until
                && (reservation.Date >= from || reservation.ReservedAt >= startOfFromUtc))
            .OrderBy(reservation => reservation.ReservedAt)
            .ThenBy(reservation => reservation.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The shop's pending and accepted reservations of business day <paramref name="date"/> (what a 臨時店休、
    /// 提早打烊 or 延後開店 of that day may take away, #392), earliest first.</summary>
    public async Task<IReadOnlyList<CoordinatorReservation>> OpenOnAsync(Guid organizationId, DateOnly date, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await OpenOn(dbContext, date).ToListAsync(cancellationToken);
    }

    /// <summary>How many of the shop's reservations command <paramref name="commandId"/> cancelled (#392; the retention sweep
    /// may have deleted some since).</summary>
    public async Task<int> CountCancelledByAsync(Guid organizationId, Guid commandId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorReservations.CountAsync(
            reservation => reservation.CancelledByCommandId == commandId && reservation.Status == CoordinatorReservationStatus.Cancelled,
            cancellationToken);
    }

    /// <summary>
    /// Cancels, in <paramref name="dbContext"/>'s transaction (acting for the command's shop), the reservations whose time
    /// the just-confirmed <paramref name="command"/> takes away (<see cref="ReservationSlots.TakenAway"/> over the shop's
    /// <paramref name="hours"/>, store-reservations design R6, Slice R4, #392): each one <c>pending</c> or <c>accepted</c>
    /// → <c>cancelled</c> in its own compare-and-set, with the scenario as <c>CancelReason</c>, the command as
    /// <c>CancelledByCommandId</c>, a new version and <paramref name="now"/>; each that changed gets
    /// <see cref="ICoordinatorReservationOutcomes.Decided"/> (the customer's cancellation Email) in the same transaction.
    /// The reservations cancelled, as they are now, earliest first.
    /// </summary>
    /// <remarks>
    /// First the shop's settings row is locked, as <see cref="SubmitAsync"/> does, and the command is confirmed before this
    /// runs: a reservation being stored at this moment is either committed before the lock is granted here (and cancelled
    /// below) or checked after it (and refused, <see cref="ReservationSubmitOutcome.SlotGone"/>). A reservation a member
    /// declined meanwhile is not cancelled; one a member accepted meanwhile is. Running it again cancels nothing more and
    /// adds no second Email: the compare-and-set only takes a pending or accepted row.
    /// </remarks>
    public async Task<IReadOnlyList<CoordinatorReservation>> CancelTakenAwayAsync(
        AppDbContext dbContext, CoordinatorCommand command, BusinessHours hours, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(hours);
        if (command.Status != CoordinatorCommandStatus.Confirmed || command.Date is not { } date)
        {
            throw new ArgumentException("Only a confirmed command with its date cancels reservations.", nameof(command));
        }

        await dbContext.CoordinatorShopSettings.ExecuteUpdateAsync(
            setters => setters.SetProperty(settings => settings.UpdatedAt, settings => settings.UpdatedAt), cancellationToken);
        var takenAway = ReservationSlots.TakenAway(
            hours, command.Scenario, date, command.Time, await OpenOn(dbContext, date).ToListAsync(cancellationToken), now);
        // Npgsql writes timestamptz only from UTC.
        var at = now.ToUniversalTime();
        var cancelledIds = new List<Guid>();
        foreach (var reservation in takenAway)
        {
            var changed = await dbContext.CoordinatorReservations
                .Where(row => row.Id == reservation.Id
                    && (row.Status == CoordinatorReservationStatus.Pending || row.Status == CoordinatorReservationStatus.Accepted))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Status, CoordinatorReservationStatus.Cancelled)
                        .SetProperty(row => row.Version, row => row.Version + 1)
                        .SetProperty(row => row.StatusChangedAt, at)
                        .SetProperty(row => row.CancelReason, (ShopScenario?)command.Scenario)
                        .SetProperty(row => row.CancelledByCommandId, (Guid?)command.Id),
                    cancellationToken);
            if (changed == 1)
            {
                cancelledIds.Add(reservation.Id);
            }
        }

        var cancelled = await dbContext.CoordinatorReservations.AsNoTracking()
            .Where(row => cancelledIds.Contains(row.Id))
            .OrderBy(row => row.ReservedAt)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken);
        foreach (var reservation in cancelled)
        {
            _outcomes.Decided(dbContext, reservation, now);
        }

        return cancelled;
    }

    /// <summary>
    /// 〔接受〕 or 〔婉拒〕 by member <paramref name="memberId"/>: <c>pending → </c><paramref name="decision"/> in one
    /// compare-and-set on version <paramref name="version"/> (the member still in the shop), recording who and when;
    /// the reservation as it is after, or <see langword="null"/> when this press changed nothing.
    /// </summary>
    public Task<CoordinatorReservation?> DecideAsync(
        Guid organizationId,
        Guid reservationId,
        int version,
        CoordinatorReservationStatus decision,
        Guid memberId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (decision is not (CoordinatorReservationStatus.Accepted or CoordinatorReservationStatus.Declined))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "A member accepts or declines.");
        }

        return LeavePendingAsync(
            organizationId,
            reservationId,
            version,
            (dbContext, rows) => rows
                .Where(reservation => dbContext.CoordinatorMembers.Any(member => member.Id == memberId))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(reservation => reservation.Status, decision)
                        .SetProperty(reservation => reservation.Version, reservation => reservation.Version + 1)
                        .SetProperty(reservation => reservation.StatusChangedAt, now)
                        .SetProperty(reservation => reservation.HandledByMemberId, memberId)
                        .SetProperty(reservation => reservation.HandledAt, now),
                    cancellationToken),
            now,
            cancellationToken);
    }

    /// <summary><c>pending → expired</c> in one compare-and-set on version <paramref name="version"/> (§3.1 point 6);
    /// the reservation as it is after, or <see langword="null"/> when it was not pending at that version any more.</summary>
    public Task<CoordinatorReservation?> ExpireAsync(
        Guid organizationId, Guid reservationId, int version, DateTimeOffset now, CancellationToken cancellationToken) =>
        LeavePendingAsync(
            organizationId,
            reservationId,
            version,
            (_, rows) => rows.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(reservation => reservation.Status, CoordinatorReservationStatus.Expired)
                    .SetProperty(reservation => reservation.Version, reservation => reservation.Version + 1)
                    .SetProperty(reservation => reservation.StatusChangedAt, now),
                cancellationToken),
            now,
            cancellationToken);

    /// <summary>A new expiry job of the reservation, to run at <paramref name="runAfter"/> (the shop's
    /// <c>pendingExpiryMinutes</c> changed since it was stored).</summary>
    public async Task ScheduleExpiryAsync(Guid organizationId, Guid reservationId, DateTimeOffset runAfter, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        dbContext.BackgroundJobs.Add(ExpiryJob(organizationId, reservationId, runAfter, now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Records whether the members were told (#390): <see cref="CoordinatorReservationNotice.Sent"/> is final;
    /// <see cref="CoordinatorReservationNotice.NotSent"/> only replaces <see cref="CoordinatorReservationNotice.Pending"/>.
    /// Whether the row changed.</summary>
    public async Task<bool> MarkNotifiedAsync(Guid organizationId, Guid reservationId, CoordinatorReservationNotice notice, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        var rows = dbContext.CoordinatorReservations.Where(reservation => reservation.Id == reservationId);
        rows = notice switch
        {
            CoordinatorReservationNotice.Sent => rows.Where(reservation => reservation.MembersNotified != CoordinatorReservationNotice.Sent),
            CoordinatorReservationNotice.NotSent => rows.Where(reservation => reservation.MembersNotified == CoordinatorReservationNotice.Pending),
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, "A reservation's members are told or not."),
        };
        return await rows.ExecuteUpdateAsync(setters => setters.SetProperty(reservation => reservation.MembersNotified, notice), cancellationToken) == 1;
    }

    /// <summary>The shop's members, the earliest bound first (whom a new reservation's card goes to).</summary>
    public async Task<IReadOnlyList<CoordinatorMember>> MembersAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorMembers.AsNoTracking()
            .OrderBy(member => member.BoundAt)
            .ThenBy(member => member.Id)
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<CoordinatorReservation> OpenOn(AppDbContext dbContext, DateOnly date) =>
        dbContext.CoordinatorReservations.AsNoTracking()
            .Where(reservation => reservation.Date == date
                && (reservation.Status == CoordinatorReservationStatus.Pending || reservation.Status == CoordinatorReservationStatus.Accepted))
            .OrderBy(reservation => reservation.ReservedAt)
            .ThenBy(reservation => reservation.Id);

    /// <summary>The expiry job of <paramref name="reservationId"/>, not before <paramref name="now"/>.</summary>
    private static BackgroundJob ExpiryJob(Guid organizationId, Guid reservationId, DateTimeOffset expiresAt, DateTimeOffset now) =>
        BackgroundJob.Create(
            organizationId,
            CoordinatorExpireReservationJob.Kind,
            new CoordinatorExpireReservationJob(reservationId),
            now.ToUniversalTime(),
            runAfter: (expiresAt > now ? expiresAt : now).ToUniversalTime());

    /// <summary>One transaction: <paramref name="update"/> on the reservation while pending at <paramref name="version"/>;
    /// when it changed the row, <see cref="ICoordinatorReservationOutcomes.Decided"/> adds its work to the same save.</summary>
    private async Task<CoordinatorReservation?> LeavePendingAsync(
        Guid organizationId,
        Guid reservationId,
        int version,
        Func<AppDbContext, IQueryable<CoordinatorReservation>, Task<int>> update,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var rows = dbContext.CoordinatorReservations.Where(reservation => reservation.Id == reservationId
            && reservation.Status == CoordinatorReservationStatus.Pending
            && reservation.Version == version);
        if (await update(dbContext, rows) != 1)
        {
            return null;
        }

        var changed = await dbContext.CoordinatorReservations.AsNoTracking()
            .SingleAsync(reservation => reservation.Id == reservationId, cancellationToken);
        _outcomes.Decided(dbContext, changed, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
