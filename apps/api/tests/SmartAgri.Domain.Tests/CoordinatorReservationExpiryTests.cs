using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// When a pending reservation expires and how its time reads (store-reservations design §3.1 point 6, Slice R2, #390),
/// also for the small hours of an overnight business day; and a row of the 統籌助理's push log.
/// </summary>
public class CoordinatorReservationExpiryTests
{
    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    private static readonly TimeZoneInfo TaipeiZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    [Fact]
    public void A_reservation_expires_the_shops_minutes_before_its_time()
    {
        var reservation = Reservation(new DateOnly(2026, 11, 8), new DateTimeOffset(2026, 11, 8, 18, 30, 0, Taipei));

        reservation.ExpiresAt(60).ShouldBe(new DateTimeOffset(2026, 11, 8, 17, 30, 0, Taipei));
        reservation.ExpiresAt(0).ShouldBe(reservation.ReservedAt);
        reservation.ExpiresAt(1440).ShouldBe(new DateTimeOffset(2026, 11, 7, 18, 30, 0, Taipei));
        Should.Throw<ArgumentOutOfRangeException>(() => reservation.ExpiresAt(-1));

        reservation.IsOpenAt(new DateTimeOffset(2026, 11, 8, 17, 29, 59, Taipei), 60).ShouldBeTrue();
        reservation.IsOpenAt(new DateTimeOffset(2026, 11, 8, 17, 30, 0, Taipei), 60).ShouldBeFalse();
        reservation.IsOpenAt(new DateTimeOffset(2026, 11, 8, 18, 0, 0, Taipei), 0).ShouldBeTrue();
    }

    [Fact]
    public void After_midnight_of_an_overnight_day_it_reads_as_the_next_day_and_expires_before_midnight()
    {
        // Saturday's 17:00–01:00: Sunday 00:30 belongs to Saturday.
        var reservation = Reservation(new DateOnly(2026, 11, 7), new DateTimeOffset(2026, 11, 8, 0, 30, 0, Taipei));

        reservation.When(TaipeiZone).ShouldBe("11/7（六） 00:30（隔天）");
        reservation.ExpiresAt(60).ShouldBe(new DateTimeOffset(2026, 11, 7, 23, 30, 0, Taipei));
        Reservation(new DateOnly(2026, 11, 8), new DateTimeOffset(2026, 11, 8, 18, 30, 0, Taipei)).When(TaipeiZone).ShouldBe("11/8（日） 18:30");
    }

    [Fact]
    public void A_push_log_row_is_one_shops_push_in_UTC()
    {
        var organization = Guid.CreateVersion7();
        var row = CoordinatorPushLog.Record(organization, CoordinatorPushPurpose.Reservation, 1, new DateTimeOffset(2026, 11, 8, 18, 30, 0, Taipei));

        (row.OrganizationId, row.Purpose, row.Recipients).ShouldBe((organization, CoordinatorPushPurpose.Reservation, 1));
        row.SentAt.Offset.ShouldBe(TimeSpan.Zero);
        row.SentAt.ShouldBe(new DateTimeOffset(2026, 11, 8, 10, 30, 0, TimeSpan.Zero));
        WireNames<CoordinatorPushPurpose>.All.ShouldBe(["reservation", "reminder", "follow-up", "re-authorization"]);
        Should.Throw<ArgumentException>(() => CoordinatorPushLog.Record(Guid.Empty, CoordinatorPushPurpose.Reminder, 1, DateTimeOffset.UtcNow));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorPushLog.Record(organization, CoordinatorPushPurpose.Reminder, 0, DateTimeOffset.UtcNow));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorPushLog.Record(organization, (CoordinatorPushPurpose)99, 1, DateTimeOffset.UtcNow));
    }

    private static CoordinatorReservation Reservation(DateOnly date, DateTimeOffset at) =>
        CoordinatorReservation.Submit(
            Guid.CreateVersion7(),
            new ReservationSlot(date, at, at.DateTime),
            2,
            "陳小姐",
            "0912345678",
            null,
            null,
            CoordinatorReservation.NewSubmissionKey(),
            at.AddDays(-1));
}
