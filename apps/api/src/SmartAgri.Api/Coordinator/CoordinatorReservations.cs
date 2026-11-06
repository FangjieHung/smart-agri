using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Coordinator;
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
}

/// <param name="Reservation">The stored reservation (<see cref="ReservationSubmitOutcome.Stored"/>, the one sent before
/// for <see cref="ReservationSubmitOutcome.Duplicate"/>); <see langword="null"/> otherwise.</param>
public sealed record ReservationSubmitResult(ReservationSubmitOutcome Outcome, CoordinatorReservation? Reservation);

/// <summary>
/// A shop's reservations (store-reservations design §3.1 points 5, 6 and 11, #389), each operation in a context acting
/// for that organization (<see cref="FixedOrganizationContext"/>), under the usual filter and write guard.
/// </summary>
public sealed class CoordinatorReservations
{
    /// <summary>EF Core's name for the unique index on <c>CoordinatorReservations.SubmissionKey</c>.</summary>
    internal const string SubmissionKeyIndex = "IX_CoordinatorReservations_SubmissionKey";

    private readonly DbContextOptions<AppDbContext> _options;

    public CoordinatorReservations(DbContextOptions<AppDbContext> options)
    {
        _options = options;
    }

    /// <summary>The shop's reservation sent with the form key <paramref name="submissionKey"/>, if any.</summary>
    public async Task<CoordinatorReservation?> FindBySubmissionKeyAsync(Guid organizationId, string submissionKey, CancellationToken cancellationToken)
    {
        await using var dbContext = ForOrganization(organizationId);
        return await dbContext.CoordinatorReservations.AsNoTracking()
            .SingleOrDefaultAsync(reservation => reservation.SubmissionKey == submissionKey, cancellationToken);
    }

    /// <summary>
    /// Stores <paramref name="reservation"/> unless its form was sent before (<see cref="ReservationSubmitOutcome.Duplicate"/>)
    /// or its phone number already has <see cref="CoordinatorReservation.MaxPendingPerPhone"/> pending reservations at the
    /// shop whose time is still ahead at <paramref name="now"/> (<see cref="ReservationSubmitOutcome.TooManyPending"/>).
    /// </summary>
    /// <remarks>
    /// One transaction that first locks the shop's settings row (an update that changes nothing), so the submissions of one
    /// shop are checked and stored one at a time: two forms of the same phone sent at once cannot both be the third.
    /// </remarks>
    public async Task<ReservationSubmitResult> SubmitAsync(CoordinatorReservation reservation, DateTimeOffset now, CancellationToken cancellationToken)
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

    private AppDbContext ForOrganization(Guid organizationId) => new(_options, new FixedOrganizationContext(organizationId));
}
