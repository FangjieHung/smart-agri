using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// The customer's reservation Emails (store-reservations design decisions R2, R3, R7, §3.1 point 9, Slice R3, #391): the
/// accepted, declined and expired messages word for word (the cancelled one is in <see cref="CoordinatorReservationClosuresTests"/>), with the date and time as the page writes them (「（隔天）」 after
/// midnight), the shop's phone, the shop's name as the sender's name, a footer saying who sent it, and nothing the customer
/// typed.
/// </summary>
public class CoordinatorReservationEmailTests
{
    private const string Footer = "——\n這封信由線上訂位系統代王記麵店寄出；回覆這封信不會有人讀取。\n";

    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");

    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    [Fact]
    public void An_accepted_reservation_is_confirmed_with_the_shops_phone()
    {
        var message = CoordinatorReservationEmail.Compose("王記麵店", "02-2345-6789", Taipei, Reservation(CoordinatorReservationStatus.Accepted));

        message.To.ShouldBe("chen@example.com");
        message.FromName.ShouldBe("王記麵店");
        message.Subject.ShouldBe("王記麵店：訂位已確認");
        message.Body.ShouldBe("王記麵店已確認您的訂位：11/8（日） 18:30，4 位。如需更改或取消，請來電 02-2345-6789。\n\n" + Footer);
    }

    [Fact]
    public void A_declined_reservation_is_regretted_and_offers_other_times()
    {
        var message = CoordinatorReservationEmail.Compose("王記麵店", "02-2345-6789", Taipei, Reservation(CoordinatorReservationStatus.Declined));

        message.Subject.ShouldBe("王記麵店：無法接受您的訂位");
        message.Body.ShouldBe("很抱歉，王記麵店無法接受您 11/8（日） 18:30 的訂位，歡迎改選其他時間或來電 02-2345-6789。\n\n" + Footer);
    }

    [Fact]
    public void An_expired_reservation_says_it_lapsed()
    {
        var message = CoordinatorReservationEmail.Compose("王記麵店", "02-2345-6789", Taipei, Reservation(CoordinatorReservationStatus.Expired));

        message.Subject.ShouldBe("王記麵店：訂位未能確認");
        message.Body.ShouldBe("很抱歉，王記麵店未能及時確認您 11/8（日） 18:30 的訂位，這筆訂位已失效。如仍需訂位，請來電 02-2345-6789。\n\n" + Footer);
    }

    [Fact]
    public void After_midnight_the_time_is_the_next_day_of_the_business_day_as_on_the_page()
    {
        // Saturday's 18:00–01:00: Sunday 00:30 is Saturday's.
        var reservation = Reservation(
            CoordinatorReservationStatus.Accepted, new DateOnly(2026, 11, 7), new DateTimeOffset(2026, 11, 8, 0, 30, 0, TimeSpan.FromHours(8)), party: 2);

        CoordinatorReservationEmail.Compose("王記麵店", "02-2345-6789", Taipei, reservation).Body
            .ShouldStartWith("王記麵店已確認您的訂位：11/7（六） 00:30（隔天），2 位。");
    }

    [Fact]
    public void Nothing_the_customer_typed_is_in_it_and_a_shop_without_a_phone_is_to_be_contacted()
    {
        var reservation = Reservation(CoordinatorReservationStatus.Accepted, name: "吳先生", phone: "0977888999", note: "素食兩位");
        var message = CoordinatorReservationEmail.Compose("王記麵店", "02-2345-6789", Taipei, reservation);
        foreach (var typed in new[] { "吳先生", "0977888999", "0977-888-999", "素食兩位" })
        {
            (message.Subject + message.Body).ShouldNotContain(typed);
        }

        CoordinatorReservationEmail.Compose("王記麵店", null, Taipei, reservation).Body
            .ShouldStartWith("王記麵店已確認您的訂位：11/8（日） 18:30，4 位。如需更改或取消，請直接與店家聯絡。\n");
        CoordinatorReservationEmail.Compose("王記麵店", " ", Taipei, Reservation(CoordinatorReservationStatus.Declined)).Body
            .ShouldStartWith("很抱歉，王記麵店無法接受您 11/8（日） 18:30 的訂位，歡迎改選其他時間。\n");
        CoordinatorReservationEmail.Compose("王記麵店", null, Taipei, Reservation(CoordinatorReservationStatus.Expired)).Body
            .ShouldStartWith("很抱歉，王記麵店未能及時確認您 11/8（日） 18:30 的訂位，這筆訂位已失效。如仍需訂位，請直接與店家聯絡。\n");
    }

    [Fact]
    public void A_store_name_never_brings_a_line_break_into_the_subject_or_sender()
    {
        var message = CoordinatorReservationEmail.Compose("王記\r\nBcc: evil@example.com", "02-2345-6789", Taipei, Reservation(CoordinatorReservationStatus.Accepted));

        message.Subject.ShouldBe("王記 Bcc: evil@example.com：訂位已確認");
        message.FromName.ShouldBe("王記 Bcc: evil@example.com");
        message.Subject.ShouldNotContain("\n");
        message.Subject.ShouldNotContain("\r");
    }

    [Fact]
    public void Only_accepted_declined_expired_and_cancelled_reservations_with_an_address_have_an_Email()
    {
        CoordinatorReservationEmail.Covers(CoordinatorReservationStatus.Accepted).ShouldBeTrue();
        CoordinatorReservationEmail.Covers(CoordinatorReservationStatus.Declined).ShouldBeTrue();
        CoordinatorReservationEmail.Covers(CoordinatorReservationStatus.Expired).ShouldBeTrue();
        CoordinatorReservationEmail.Covers(CoordinatorReservationStatus.Cancelled).ShouldBeTrue("a closure's (#392)");
        CoordinatorReservationEmail.Covers(CoordinatorReservationStatus.Pending).ShouldBeFalse();

        Should.Throw<ArgumentException>(() => CoordinatorReservationEmail.Compose("王記麵店", "02-2345-6789", Taipei, Reservation(CoordinatorReservationStatus.Pending)));
        Should.Throw<ArgumentException>(
            () => CoordinatorReservationEmail.Compose("王記麵店", "02-2345-6789", Taipei, Reservation(CoordinatorReservationStatus.Cancelled)),
            "a cancellation says why");
        Should.Throw<ArgumentException>(() => CoordinatorReservationEmail.Compose("王記麵店", "02-2345-6789", Taipei, Reservation(CoordinatorReservationStatus.Accepted, email: null)));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorReservationEmail.Subject("王記麵店", CoordinatorReservationStatus.Pending));
    }

    /// <summary>A reservation as it is read back once it left pending (the persistence layer sets the status).</summary>
    private static CoordinatorReservation Reservation(
        CoordinatorReservationStatus status,
        DateOnly? date = null,
        DateTimeOffset? at = null,
        int party = 4,
        string name = "陳小姐",
        string phone = "0912345678",
        string? email = "chen@example.com",
        string? note = "需要兒童椅")
    {
        var when = at ?? new DateTimeOffset(2026, 11, 8, 18, 30, 0, TimeSpan.FromHours(8));
        var reservation = CoordinatorReservation.Submit(
            Organization,
            new ReservationSlot(date ?? new DateOnly(2026, 11, 8), when, when.DateTime),
            party,
            name,
            phone,
            email,
            note,
            CoordinatorReservation.NewSubmissionKey(),
            when.AddDays(-2));
        typeof(CoordinatorReservation).GetProperty(nameof(CoordinatorReservation.Status))!.SetValue(reservation, status);
        return reservation;
    }
}
