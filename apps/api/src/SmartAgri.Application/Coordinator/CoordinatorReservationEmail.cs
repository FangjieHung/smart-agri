using System.Globalization;
using System.Text;
using SmartAgri.Application.Notifications;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// The Email a customer gets when their reservation was accepted, declined or expired (store-reservations design decisions
/// R2, R3, R7, §3.1 point 9, Slice R3, #391): one plain-text message to the address they left, from the platform's
/// <c>Smtp:From</c> with the shop's name as the sender's name. The date and time are written as the reservation page and
/// the members' card write them (<see cref="CoordinatorReservation.When"/>, 「（隔天）」 after midnight), and every message
/// gives the shop's phone number to call.
/// </summary>
/// <remarks>
/// Nothing else of the reservation is in it — not the name, phone or note the customer typed — and a short footer says
/// the platform sent it for the shop and that a reply is read by nobody. A shop whose settings no longer have a phone
/// (reservations taken out of its settings since) gets the same messages, asking the customer to contact the shop
/// instead of giving a number.
/// </remarks>
public static class CoordinatorReservationEmail
{
    /// <summary>The outcomes there is an Email for (closures are Slice R4's).</summary>
    public static bool Covers(CoordinatorReservationStatus status) =>
        status is CoordinatorReservationStatus.Accepted or CoordinatorReservationStatus.Declined or CoordinatorReservationStatus.Expired;

    /// <summary>The subject of the Email for <paramref name="outcome"/> at <paramref name="storeName"/>.</summary>
    public static string Subject(string storeName, CoordinatorReservationStatus outcome)
    {
        var store = OneLine(storeName);
        return outcome switch
        {
            CoordinatorReservationStatus.Accepted => $"{store}：訂位已確認",
            CoordinatorReservationStatus.Declined => $"{store}：無法接受您的訂位",
            CoordinatorReservationStatus.Expired => $"{store}：訂位未能確認",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Only an accepted, declined or expired reservation is told by Email."),
        };
    }

    /// <summary>
    /// The Email to <paramref name="reservation"/>'s customer for its status (accepted, declined or expired): to
    /// <see cref="CoordinatorReservation.CustomerEmail"/>, from <paramref name="storeName"/> (the sender's name), with
    /// <paramref name="shopPhone"/> (<see cref="ShopReservationSettings.Phone"/>; <see langword="null"/> when the shop has
    /// none any more) and the time in <paramref name="zone"/>.
    /// </summary>
    public static EmailMessage Compose(string storeName, string? shopPhone, TimeZoneInfo zone, CoordinatorReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(storeName);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(reservation);
        var to = reservation.CustomerEmail
            ?? throw new ArgumentException("Only a customer who left an Email address is told by Email.", nameof(reservation));
        var store = OneLine(storeName);
        var phone = string.IsNullOrWhiteSpace(shopPhone) ? null : OneLine(shopPhone);
        var when = reservation.When(zone);
        var party = string.Create(CultureInfo.InvariantCulture, $"{reservation.PartySize} 位");

        var sentence = reservation.Status switch
        {
            CoordinatorReservationStatus.Accepted =>
                $"{store}已確認您的訂位：{when}，{party}。如需更改或取消，{(phone is null ? "請直接與店家聯絡" : $"請來電 {phone}")}。",
            CoordinatorReservationStatus.Declined =>
                $"很抱歉，{store}無法接受您 {when} 的訂位，歡迎改選其他時間{(phone is null ? string.Empty : $"或來電 {phone}")}。",
            CoordinatorReservationStatus.Expired =>
                $"很抱歉，{store}未能及時確認您 {when} 的訂位，這筆訂位已失效。如仍需訂位，{(phone is null ? "請直接與店家聯絡" : $"請來電 {phone}")}。",
            _ => throw new ArgumentException("Only an accepted, declined or expired reservation is told by Email.", nameof(reservation)),
        };

        var body = new StringBuilder();
        body.Append(sentence).Append("\n\n");
        body.Append("——\n");
        body.Append(CultureInfo.InvariantCulture, $"這封信由線上訂位系統代{store}寄出；回覆這封信不會有人讀取。\n");
        return new EmailMessage(to, Subject(store, reservation.Status), body.ToString(), FromName: store);
    }

    /// <summary>Names are typed by people: no line breaks or control characters reach a header (as
    /// <see cref="HandoffEmail"/>).</summary>
    private static string OneLine(string text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(character => !char.IsControl(character))])));
}
