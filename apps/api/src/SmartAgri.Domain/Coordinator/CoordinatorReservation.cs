using System.Buffers.Text;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Coordinator;

/// <summary>Where a <see cref="CoordinatorReservation"/> is (store-reservations design §3.1 point 6, #389).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoordinatorReservationStatus>))]
public enum CoordinatorReservationStatus
{
    /// <summary>Sent by the customer; nobody of the shop answered yet.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>A member pressed 〔接受〕 first (Slice R2).</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    /// <summary>A member pressed 〔婉拒〕 first (Slice R2).</summary>
    [JsonStringEnumMemberName("declined")]
    Declined,

    /// <summary>A confirmed 臨時店休／提早打烊／延後開店 took its time away (Slice R4).</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    /// <summary>Still pending <see cref="ShopReservationSettings.PendingExpiryMinutes"/> before its time (Slice R2).</summary>
    [JsonStringEnumMemberName("expired")]
    Expired,
}

/// <summary>Whether the shop's members were told of a <see cref="CoordinatorReservation"/> (§3.1 point 7, Slice R2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoordinatorReservationNotice>))]
public enum CoordinatorReservationNotice
{
    /// <summary>Not yet (every reservation of Slice R1 stays here).</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>At least one member was pushed the reservation card.</summary>
    [JsonStringEnumMemberName("sent")]
    Sent,

    /// <summary>「未通知成員」: no push got through (e.g. the 統籌助理's monthly quota is used up).</summary>
    [JsonStringEnumMemberName("not-sent")]
    NotSent,
}

/// <summary>
/// 訂位 (table <c>CoordinatorReservations</c>, store-reservations design §3.1 points 6 and 10, Slice R1, #389): one
/// booking a customer sent from the shop's reservation page — when (<see cref="ReservedAt"/>, UTC, on the business day
/// <see cref="Date"/>), for how many, and how to reach them. The columns of Slices R2–R4 (who handled it and when, why
/// it was cancelled, whether the members were told) are here already.
/// </summary>
/// <remarks>
/// <para>
/// The customer's name, phone, Email and note are personal data the platform processes for the shop (R8): never logged,
/// never in an activity, and the row is deleted 90 days after <see cref="ReservedAt"/> (<see cref="RetentionDays"/>,
/// the daily retention sweep). <see cref="SubmissionKey"/> is the form's one-time key, unique across all
/// organizations, so a form sent twice is one reservation.
/// </para>
/// <para>
/// Every change of <see cref="Status"/> is a compare-and-set on (<see cref="Status"/>, <see cref="Version"/>) in the
/// persistence layer (Slice R2), and <see cref="Version"/> grows with it.
/// </para>
/// </remarks>
public sealed class CoordinatorReservation : IOrganizationScoped
{
    /// <summary>A reservation is kept this many days after its time (R8).</summary>
    public const int RetentionDays = 90;

    /// <summary>The most pending reservations one phone number may have at one shop (§3.1 point 5).</summary>
    public const int MaxPendingPerPhone = 3;

    /// <summary>Length of <see cref="SubmissionKey"/> (<see cref="NewSubmissionKey"/>: 32 random bytes, base64url).</summary>
    public const int SubmissionKeyLength = 43;

    /// <summary>For EF Core materialization.</summary>
    private CoordinatorReservation()
    {
        CustomerName = string.Empty;
        CustomerPhone = string.Empty;
        SubmissionKey = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The shop's business day it is for (<see cref="ReservationSlot.Date"/>; a time after midnight of an
    /// overnight period is the day before's).</summary>
    public DateOnly Date { get; private set; }

    /// <summary>The time booked, in UTC.</summary>
    public DateTimeOffset ReservedAt { get; private set; }

    public int PartySize { get; private set; }

    public string CustomerName { get; private set; }

    /// <summary>Digits only, Taiwanese form (<see cref="ReservationContact.NormalizePhone"/>): <c>0912345678</c>.</summary>
    public string CustomerPhone { get; private set; }

    public string? CustomerEmail { get; private set; }

    public string? Note { get; private set; }

    public CoordinatorReservationStatus Status { get; private set; }

    /// <summary>1 at first; one more with every change of <see cref="Status"/>.</summary>
    public int Version { get; private set; }

    /// <summary>The form's one-time key; unique.</summary>
    public string SubmissionKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When <see cref="Status"/> last changed; <see langword="null"/> while it never has.</summary>
    public DateTimeOffset? StatusChangedAt { get; private set; }

    /// <summary>The member (<see cref="CoordinatorMember.Id"/>) who accepted or declined it (Slice R2); kept when a
    /// closure cancels it later. Not a foreign key: a member who leaves the shop does not take the history along.</summary>
    public Guid? HandledByMemberId { get; private set; }

    /// <summary>When <see cref="HandledByMemberId"/> accepted or declined it; set exactly with it.</summary>
    public DateTimeOffset? HandledAt { get; private set; }

    /// <summary>Why it was cancelled (Slice R4): the scenario of the command that took its time away; set exactly when
    /// <see cref="Status"/> is <see cref="CoordinatorReservationStatus.Cancelled"/>.</summary>
    public ShopScenario? CancelReason { get; private set; }

    /// <summary>That command (Slice R4).</summary>
    public Guid? CancelledByCommandId { get; private set; }

    /// <summary>Whether the shop's members were told (Slice R2).</summary>
    public CoordinatorReservationNotice MembersNotified { get; private set; }

    /// <summary>A new pending reservation, sent at <paramref name="now"/>. The values are already checked
    /// (<see cref="ReservationContact"/>); this only guards against a caller's mistake.</summary>
    public static CoordinatorReservation Submit(
        Guid organizationId,
        ReservationSlot slot,
        int partySize,
        string customerName,
        string customerPhone,
        string? customerEmail,
        string? note,
        string submissionKey,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A reservation always belongs to an organization.", nameof(organizationId));
        }

        ArgumentNullException.ThrowIfNull(slot);
        ArgumentOutOfRangeException.ThrowIfLessThan(partySize, 1);
        if (ReservationContact.NameProblem(customerName) is not null
            || ReservationContact.NormalizePhone(customerPhone) != customerPhone
            || (customerEmail is not null && ReservationContact.EmailProblem(customerEmail) is not null)
            || (note is not null && ReservationContact.NoteProblem(note) is not null))
        {
            throw new ArgumentException("The customer's details are checked before a reservation is made.");
        }

        if (!IsSubmissionKey(submissionKey))
        {
            throw new ArgumentException("Not a submission key.", nameof(submissionKey));
        }

        return new CoordinatorReservation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Date = slot.Date,
            // Npgsql writes timestamptz only from UTC.
            ReservedAt = slot.At.ToUniversalTime(),
            PartySize = partySize,
            CustomerName = customerName,
            CustomerPhone = customerPhone,
            CustomerEmail = customerEmail,
            Note = note,
            Status = CoordinatorReservationStatus.Pending,
            Version = 1,
            SubmissionKey = submissionKey,
            CreatedAt = now,
            MembersNotified = CoordinatorReservationNotice.Pending,
        };
    }

    /// <summary>When it expires if nobody handled it (§3.1 point 6, Slice R2): <paramref name="pendingExpiryMinutes"/>
    /// (<see cref="ShopReservationSettings.PendingExpiryMinutes"/>) before <see cref="ReservedAt"/>.</summary>
    public DateTimeOffset ExpiresAt(int pendingExpiryMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pendingExpiryMinutes);
        return ReservedAt.AddMinutes(-pendingExpiryMinutes);
    }

    /// <summary>Whether it is one a member can still accept or decline at <paramref name="now"/>: pending and not past
    /// <see cref="ExpiresAt"/>.</summary>
    public bool IsOpenAt(DateTimeOffset now, int pendingExpiryMinutes) =>
        Status == CoordinatorReservationStatus.Pending && now < ExpiresAt(pendingExpiryMinutes);

    /// <summary>The date and time as the reservation page shows them, in <paramref name="zone"/>: 「11/8（日）18:30」, and
    /// 「11/7（六）00:30（隔天）」 for the small hours of an overnight business day (<see cref="Date"/> is the day before).</summary>
    public string When(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(ReservedAt, zone).DateTime;
        return $"{ShopAnnouncements.FormatDate(Date)} {ShopAnnouncements.FormatTime(TimeOnly.FromDateTime(local))}"
            + (DateOnly.FromDateTime(local) != Date ? "（隔天）" : string.Empty);
    }

    /// <summary>A new form's one-time key.</summary>
    public static string NewSubmissionKey() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether <paramref name="key"/> looks like <see cref="NewSubmissionKey"/>'s.</summary>
    public static bool IsSubmissionKey(string? key) =>
        key is { Length: SubmissionKeyLength } && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

/// <summary>
/// The customer's details on the reservation form (#389): each check gives the message shown next to its field, or
/// <see langword="null"/> when it is fine. The page trims values before checking them.
/// </summary>
public static partial class ReservationContact
{
    public const int NameMaxLength = 50;

    /// <summary>Longest Email address (RFC 5321's path limit).</summary>
    public const int EmailMaxLength = 254;

    public const int NoteMaxLength = 200;

    /// <summary>Longest phone number as typed (with spaces, dashes and a country code).</summary>
    public const int PhoneInputMaxLength = 30;

    /// <summary>Longest stored phone number (digits).</summary>
    public const int PhoneMaxLength = 10;

    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "請填寫姓名。";
        }

        if (name.Length > NameMaxLength)
        {
            return $"姓名最多 {NameMaxLength} 個字。";
        }

        return name.Any(char.IsControl) ? "姓名不能有換行或特殊字元。" : null;
    }

    /// <summary>
    /// A Taiwanese phone number in digits only (<c>0912345678</c>, <c>0223456789</c>), from what a customer types —
    /// full-width characters read as their ASCII forms, spaces, <c>-</c>, <c>.</c> and parentheses dropped,
    /// <c>+886</c> (or <c>886</c>, <c>+886 0</c>) read as
    /// <c>0</c> — when it is a mobile number (<c>09</c> and 8 digits) or a landline (<c>0</c>, an area code 2–8 and
    /// 7–8 digits); otherwise <see langword="null"/>.
    /// </summary>
    public static string? NormalizePhone(string? phone)
    {
        if (phone is null || phone.Length > PhoneInputMaxLength)
        {
            return null;
        }

        // Full-width digits, dashes and spaces (a Chinese keyboard) become their ASCII forms.
        var builder = new StringBuilder(phone.Length);
        foreach (var character in phone.Normalize(NormalizationForm.FormKC).Trim())
        {
            if (char.IsAsciiDigit(character) || (character == '+' && builder.Length == 0))
            {
                builder.Append(character);
            }
            else if (character is not (' ' or '-' or '.' or '(' or ')'))
            {
                return null;
            }
        }

        var digits = builder.ToString();
        if (digits.StartsWith("+886", StringComparison.Ordinal) || digits.StartsWith("886", StringComparison.Ordinal))
        {
            var national = digits[(digits.IndexOf("886", StringComparison.Ordinal) + 3)..];
            digits = "0" + (national.StartsWith('0') ? national[1..] : national);
        }

        return TaiwanPhone().IsMatch(digits) ? digits : null;
    }

    public static string? PhoneProblem(string? phone) =>
        string.IsNullOrWhiteSpace(phone) ? "請填寫電話。"
        : NormalizePhone(phone) is null ? "請填寫台灣的手機或市話號碼，例如 0912-345-678 或 02-2345-6789。"
        : null;

    /// <summary>For people, best effort: a mobile number <c>0912-345-678</c>, a ten-digit landline <c>02-2345-6789</c>,
    /// a nine-digit one <c>07-123-4567</c>; anything else as it is.</summary>
    public static string FormatPhone(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);
        return phone switch
        {
            { Length: 10 } when phone.StartsWith("09", StringComparison.Ordinal) => $"{phone[..4]}-{phone[4..7]}-{phone[7..]}",
            { Length: 10 } => $"{phone[..2]}-{phone[2..6]}-{phone[6..]}",
            { Length: 9 } => $"{phone[..2]}-{phone[2..5]}-{phone[5..]}",
            _ => phone,
        };
    }

    /// <summary>Only the last three digits: <c>…678</c> (the operator's list, §3.1 point 11).</summary>
    public static string MaskPhone(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);
        return phone.Length <= 3 ? "…" : "…" + phone[^3..];
    }

    public static string? EmailProblem(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        if (email.Length > EmailMaxLength)
        {
            return $"Email 最多 {EmailMaxLength} 個字元。";
        }

        return email.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || !MailAddress.TryCreate(email, out var address)
            || address.Address != email
            || !address.Host.Contains('.', StringComparison.Ordinal)
                ? "Email 的格式不正確，例如 name@example.com。"
                : null;
    }

    public static string? NoteProblem(string? note)
    {
        if (string.IsNullOrEmpty(note))
        {
            return null;
        }

        if (note.Length > NoteMaxLength)
        {
            return $"備註最多 {NoteMaxLength} 個字（目前 {note.Length} 個）。";
        }

        return note.Any(character => character != '\n' && char.IsControl(character)) ? "備註不能有特殊字元。" : null;
    }

    [GeneratedRegex("^(?:09[0-9]{8}|0[2-8][0-9]{7,8})$")]
    private static partial Regex TaiwanPhone();
}
