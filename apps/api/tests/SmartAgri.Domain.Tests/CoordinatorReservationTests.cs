using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// A reservation and the customer's details on the form (#389): Taiwanese phone numbers in any usual spelling become
/// digits, names, Email addresses and notes are checked with the message shown next to the field, and a reservation is
/// stored in UTC, pending, with a one-time form key.
/// </summary>
public class CoordinatorReservationTests
{
    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    [Theory]
    [InlineData("0912345678", "0912345678")]
    [InlineData("0912-345-678", "0912345678")]
    [InlineData(" 0912 345 678 ", "0912345678")]
    [InlineData("+886 912 345 678", "0912345678")]
    [InlineData("+886-912-345-678", "0912345678")]
    [InlineData("+886 0912 345 678", "0912345678")]
    [InlineData("886912345678", "0912345678")]
    [InlineData("(02) 2345-6789", "0223456789")]
    [InlineData("02-2345-6789", "0223456789")]
    [InlineData("+886-2-2345-6789", "0223456789")]
    [InlineData("07-123-4567", "071234567")]
    [InlineData("037-123456", "037123456")]
    [InlineData("０９１２－３４５－６７８", "0912345678")]
    [InlineData("02.2345.6789", "0223456789")]
    public void Taiwanese_phone_numbers_become_digits(string typed, string stored)
    {
        ReservationContact.NormalizePhone(typed).ShouldBe(stored);
        ReservationContact.PhoneProblem(typed).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("091234567")]
    [InlineData("09123456789")]
    [InlineData("0112345678")]
    [InlineData("12345678")]
    [InlineData("+1 415 555 0100")]
    [InlineData("0912-345-678 #12")]
    [InlineData("0912abc678")]
    [InlineData("09-1234+5678")]
    public void Other_numbers_are_refused(string typed)
    {
        ReservationContact.NormalizePhone(typed).ShouldBeNull();
        ReservationContact.PhoneProblem(typed).ShouldNotBeNull();
    }

    [Fact]
    public void A_missing_phone_says_so_and_a_wrong_one_shows_an_example()
    {
        ReservationContact.PhoneProblem(null).ShouldBe("請填寫電話。");
        ReservationContact.PhoneProblem("123").ShouldBe("請填寫台灣的手機或市話號碼，例如 0912-345-678 或 02-2345-6789。");
        ReservationContact.NormalizePhone(new string('1', ReservationContact.PhoneInputMaxLength + 1)).ShouldBeNull();
    }

    [Theory]
    [InlineData("0912345678", "0912-345-678", "…678")]
    [InlineData("0223456789", "02-2345-6789", "…789")]
    [InlineData("071234567", "07-123-4567", "…567")]
    public void Stored_numbers_are_shown_with_dashes_or_only_their_last_three_digits(string stored, string shown, string masked)
    {
        ReservationContact.FormatPhone(stored).ShouldBe(shown);
        ReservationContact.MaskPhone(stored).ShouldBe(masked);
    }

    [Fact]
    public void A_name_is_required_short_and_on_one_line()
    {
        ReservationContact.NameProblem("陳小姐").ShouldBeNull();
        ReservationContact.NameProblem(new string('陳', ReservationContact.NameMaxLength)).ShouldBeNull();
        ReservationContact.NameProblem(null).ShouldBe("請填寫姓名。");
        ReservationContact.NameProblem("  ").ShouldBe("請填寫姓名。");
        ReservationContact.NameProblem(new string('陳', ReservationContact.NameMaxLength + 1)).ShouldBe("姓名最多 50 個字。");
        ReservationContact.NameProblem("陳\n小姐").ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("chen@example.com", true)]
    [InlineData("chen.mei+reserve@mail.example.com.tw", true)]
    [InlineData("chen", false)]
    [InlineData("chen@", false)]
    [InlineData("chen@localhost", false)]
    [InlineData("chen @example.com", false)]
    [InlineData("陳小姐 <chen@example.com>", false)]
    [InlineData("a@b.c,d@e.f", false)]
    public void An_email_address_is_optional_and_one_plain_address(string? email, bool fine)
    {
        (ReservationContact.EmailProblem(email) is null).ShouldBe(fine);
    }

    [Fact]
    public void An_email_address_has_at_most_254_characters()
    {
        var longest = new string('a', 64) + "@" + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 61) + ".tw";
        longest.Length.ShouldBe(257);
        ReservationContact.EmailProblem(longest).ShouldBe("Email 最多 254 個字元。");
    }

    [Fact]
    public void A_note_is_optional_short_and_may_have_line_breaks()
    {
        ReservationContact.NoteProblem(null).ShouldBeNull();
        ReservationContact.NoteProblem("需要兒童椅\n靠窗").ShouldBeNull();
        ReservationContact.NoteProblem(new string('字', ReservationContact.NoteMaxLength)).ShouldBeNull();
        ReservationContact.NoteProblem(new string('字', ReservationContact.NoteMaxLength + 1)).ShouldBe("備註最多 200 個字（目前 201 個）。");
        ReservationContact.NoteProblem("tab\there").ShouldNotBeNull();
    }

    [Fact]
    public void A_reservation_is_pending_stored_in_UTC_on_its_business_day()
    {
        var organizationId = Guid.NewGuid();
        var slot = new ReservationSlot(new DateOnly(2026, 11, 7), new DateTimeOffset(2026, 11, 8, 0, 30, 0, Taipei), new DateTime(2026, 11, 8, 0, 30, 0));
        var key = CoordinatorReservation.NewSubmissionKey();
        var now = new DateTimeOffset(2026, 11, 6, 6, 0, 0, TimeSpan.Zero);

        var reservation = CoordinatorReservation.Submit(organizationId, slot, 4, "陳小姐", "0912345678", "chen@example.com", "需要兒童椅", key, now);

        reservation.OrganizationId.ShouldBe(organizationId);
        reservation.Date.ShouldBe(new DateOnly(2026, 11, 7));
        reservation.ReservedAt.ShouldBe(slot.At);
        reservation.ReservedAt.Offset.ShouldBe(TimeSpan.Zero, "Npgsql writes timestamptz only from UTC");
        (reservation.Status, reservation.Version, reservation.MembersNotified).ShouldBe(
            (CoordinatorReservationStatus.Pending, 1, CoordinatorReservationNotice.Pending));
        (reservation.StatusChangedAt, reservation.HandledAt, reservation.HandledByMemberId, reservation.CancelReason, reservation.CancelledByCommandId)
            .ShouldBe((null, null, null, null, null));
        reservation.SubmissionKey.ShouldBe(key);
        reservation.CreatedAt.ShouldBe(now);
    }

    [Fact]
    public void Details_that_were_not_checked_are_refused()
    {
        var slot = new ReservationSlot(new DateOnly(2026, 11, 7), new DateTimeOffset(2026, 11, 7, 18, 0, 0, Taipei), new DateTime(2026, 11, 7, 18, 0, 0));
        var key = CoordinatorReservation.NewSubmissionKey();
        var now = DateTimeOffset.UtcNow;

        Should.Throw<ArgumentException>(() => CoordinatorReservation.Submit(Guid.Empty, slot, 2, "陳小姐", "0912345678", null, null, key, now));
        Should.Throw<ArgumentException>(() => CoordinatorReservation.Submit(Guid.NewGuid(), slot, 2, "陳小姐", "0912-345-678", null, null, key, now));
        Should.Throw<ArgumentException>(() => CoordinatorReservation.Submit(Guid.NewGuid(), slot, 2, " ", "0912345678", null, null, key, now));
        Should.Throw<ArgumentException>(() => CoordinatorReservation.Submit(Guid.NewGuid(), slot, 2, "陳小姐", "0912345678", "chen", null, key, now));
        Should.Throw<ArgumentException>(() => CoordinatorReservation.Submit(Guid.NewGuid(), slot, 2, "陳小姐", "0912345678", null, null, "short", now));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorReservation.Submit(Guid.NewGuid(), slot, 0, "陳小姐", "0912345678", null, null, key, now));
    }

    [Fact]
    public void Form_keys_are_random_and_recognized()
    {
        var first = CoordinatorReservation.NewSubmissionKey();
        var second = CoordinatorReservation.NewSubmissionKey();

        first.ShouldNotBe(second);
        first.Length.ShouldBe(CoordinatorReservation.SubmissionKeyLength);
        CoordinatorReservation.IsSubmissionKey(first).ShouldBeTrue();
        CoordinatorReservation.IsSubmissionKey(null).ShouldBeFalse();
        CoordinatorReservation.IsSubmissionKey(first[..^1]).ShouldBeFalse();
        CoordinatorReservation.IsSubmissionKey(first[..^1] + "=").ShouldBeFalse();
    }

    [Theory]
    [InlineData("wangji", true)]
    [InlineData("wang-ji-2", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("WangJi", false)]
    [InlineData("-wangji", false)]
    [InlineData("wangji-", false)]
    [InlineData("wang_ji", false)]
    [InlineData("王記", false)]
    public void A_slug_is_lowercase_letters_digits_and_dashes(string slug, bool valid)
    {
        ShopReservationSettings.IsValidSlug(slug).ShouldBe(valid);
    }

    [Fact]
    public void A_slug_from_a_path_is_lowercased_and_anything_else_is_nothing()
    {
        ShopReservationSettings.NormalizeSlug("WangJi").ShouldBe("wangji");
        ShopReservationSettings.NormalizeSlug("wangji").ShouldBe("wangji");
        ShopReservationSettings.NormalizeSlug("wang ji").ShouldBeNull();
        ShopReservationSettings.NormalizeSlug(new string('a', 41)).ShouldBeNull();
        ShopReservationSettings.NormalizeSlug(null).ShouldBeNull();
    }

    [Fact]
    public void A_shops_phone_becomes_a_tel_link()
    {
        ShopReservationSettings.IsValidPhone("02-2345-6789").ShouldBeTrue();
        ShopReservationSettings.IsValidPhone("+886 2 2345 6789").ShouldBeTrue();
        ShopReservationSettings.IsValidPhone("1234567").ShouldBeFalse("seven digits");
        ShopReservationSettings.IsValidPhone("02-2345-6789 #12").ShouldBeFalse();
        ShopReservationSettings.TelUri("(02) 2345-6789").ShouldBe("tel:0223456789");
        ShopReservationSettings.TelUri("+886 2 2345 6789").ShouldBe("tel:+886223456789");
    }
}
