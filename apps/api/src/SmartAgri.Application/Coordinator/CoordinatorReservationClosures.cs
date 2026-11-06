using System.Globalization;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>What a preview card says about the reservations of its day (store-reservations design R6, Slice R4, #392).</summary>
/// <param name="Accepted">How many accepted reservations the command would cancel.</param>
/// <param name="Pending">How many pending ones.</param>
/// <param name="Listed">The first <see cref="CoordinatorReservationClosures.CardListMax"/> of them, earliest first:
/// 「18:30 陳小姐」.</param>
/// <param name="NotRestored">A reopen card: how many reservations the command it undoes cancelled (they stay cancelled).</param>
public sealed record CoordinatorCardReservations(int Accepted, int Pending, IReadOnlyList<string> Listed, int NotRestored = 0)
{
    /// <summary>How many the command would cancel.</summary>
    public int Count => Accepted + Pending;

    /// <summary>No names: they are the customers'.</summary>
    public override string ToString() =>
        $"{nameof(CoordinatorCardReservations)} {{ Accepted = {Accepted}, Pending = {Pending}, NotRestored = {NotRestored} }}";
}

/// <summary>What the confirmation report says about the reservations a command cancelled (#392).</summary>
/// <param name="Cancelled">How many reservations the command cancelled.</param>
/// <param name="Emailed">Of them, how many customers get the cancellation Email.</param>
/// <param name="Calls">The others, earliest first, 「18:30 陳小姐 0912-345-678」: listed under the
/// <see cref="ShopActionKind.ReservationCalls"/> reminder.</param>
/// <param name="NotRestored">A reopen: how many reservations the command it undoes cancelled (they stay cancelled).</param>
public sealed record CoordinatorReportReservations(int Cancelled, int Emailed, IReadOnlyList<string> Calls, int NotRestored = 0)
{
    /// <summary>No names or phone numbers: they are the customers'.</summary>
    public override string ToString() =>
        $"{nameof(CoordinatorReportReservations)} {{ Cancelled = {Cancelled}, Emailed = {Emailed}, Calls = {Calls.Count}, NotRestored = {NotRestored} }}";
}

/// <summary>
/// The words of a 臨時店休、提早打烊 or 延後開店 that takes reservations away (store-reservations design decision R6, Slice R4,
/// #392): the preview card's line and list, the reason the customer's Email and a member's later press give, the
/// confirmation report's summary and the reminder to call the customers who get no Email, and what a reopen says.
/// Built purely from the reservations (<see cref="ReservationSlots.TakenAway"/> decides which ones).
/// </summary>
/// <remarks>
/// The preview card and the report go only to the shop's members, who see the same names and phone numbers on the
/// reservation cards; the alt texts (a notification's preview) never carry them.
/// </remarks>
public static class CoordinatorReservationClosures
{
    /// <summary>How many reservations a preview card lists by name.</summary>
    public const int CardListMax = 5;

    /// <summary>How many customers to call the report lists (a bubble's JSON is at most 30 KB; with the longest names 50
    /// lines stay well within it).</summary>
    public const int ReportCallsMax = 50;

    /// <summary>「還有 3 筆沒有列出，請聯絡平台營運者」: the customers to call beyond <see cref="ReportCallsMax"/>.</summary>
    public static string CallsNotListed(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"還有 {count} 筆沒有列出，請聯絡平台營運者");

    /// <summary>The <see cref="ShopActionKind.ReservationCalls"/> reminder's label (the report's line and 「已記錄：…」).</summary>
    public const string CallsLabel = "打電話通知顧客訂位已取消";

    /// <summary>The reminder's steps when the customers left no Email address.</summary>
    public const string CallsSteps = "這些顧客沒有留 Email，請來電告知訂位已取消：";

    /// <summary>The reminder's steps when the platform sends no Email (no SMTP): every customer is called.</summary>
    public const string CallsStepsWithoutEmail = "平台沒有設定寄信，請來電告知這些顧客訂位已取消：";

    /// <summary>The reminder's steps: <see cref="CallsSteps"/>, or <see cref="CallsStepsWithoutEmail"/>.</summary>
    public static string StepsOf(bool emailConfigured) => emailConfigured ? CallsSteps : CallsStepsWithoutEmail;

    /// <summary>
    /// Why a reservation was cancelled, as the customer's Email and a member's later press say it: 「臨時店休」,
    /// 「當天提早在 20:00 打烊」 or 「當天延後到 12:00 開店」 (<paramref name="time"/> the command's; without it 「當天提早打烊」,
    /// 「當天延後開店」).
    /// </summary>
    public static string Reason(ShopScenario scenario, TimeOnly? time) => scenario switch
    {
        ShopScenario.TemporaryClosure => "臨時店休",
        ShopScenario.CloseEarly => time is { } closes ? $"當天提早在 {ShopAnnouncements.FormatTime(closes)} 打烊" : "當天提早打烊",
        ShopScenario.OpenLate => time is { } opens ? $"當天延後到 {ShopAnnouncements.FormatTime(opens)} 開店" : "當天延後開店",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Only a 臨時店休, 提早打烊 or 延後開店 cancels reservations."),
    };

    /// <summary>The preview card's line: 「會取消 3 筆訂位（已接受 1 筆、待處理 2 筆）」.</summary>
    public static string CardLine(int accepted, int pending) =>
        string.Create(CultureInfo.InvariantCulture, $"會取消 {accepted + pending} 筆訂位（已接受 {accepted} 筆、待處理 {pending} 筆）");

    /// <summary>「還有 2 筆」: the reservations a preview card does not list by name.</summary>
    public static string More(int count) => string.Create(CultureInfo.InvariantCulture, $"還有 {count} 筆");

    /// <summary>
    /// The report's summary: 「已取消 3 筆訂位，其中 1 筆會寄信通知顧客。」, or 「已取消 3 筆訂位。」 when nobody is told by Email
    /// (the reminder to call them follows).
    /// </summary>
    public static string Cancelled(int cancelled, int emailed) =>
        emailed > 0
            ? string.Create(CultureInfo.InvariantCulture, $"已取消 {cancelled} 筆訂位，其中 {emailed} 筆會寄信通知顧客。")
            : string.Create(CultureInfo.InvariantCulture, $"已取消 {cancelled} 筆訂位。");

    /// <summary>What a reopen card and report say: 「因店休取消的 2 筆訂位不會恢復，請視需要聯絡顧客」.</summary>
    public static string NotRestored(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"因店休取消的 {count} 筆訂位不會恢復，請視需要聯絡顧客");

    /// <summary>
    /// What a preview card of a command for <paramref name="affected"/>'s day says (<paramref name="affected"/> from
    /// <see cref="ReservationSlots.TakenAway"/>, earliest first): how many accepted and pending, and the first
    /// <see cref="CardListMax"/> as 「18:30 陳小姐」 in <paramref name="zone"/>.
    /// </summary>
    public static CoordinatorCardReservations Card(IReadOnlyList<CoordinatorReservation> affected, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(affected);
        ArgumentNullException.ThrowIfNull(zone);
        return new CoordinatorCardReservations(
            affected.Count(reservation => reservation.Status == CoordinatorReservationStatus.Accepted),
            affected.Count(reservation => reservation.Status == CoordinatorReservationStatus.Pending),
            [.. affected.Take(CardListMax).Select(reservation => $"{reservation.TimeOfDay(zone)} {reservation.CustomerName}")]);
    }

    /// <summary>
    /// What the confirmation report says of <paramref name="cancelled"/> (the reservations the command just cancelled):
    /// how many, how many customers get the Email (<see cref="CoordinatorReservationEmail.Sends"/> with
    /// <paramref name="emailConfigured"/>), and the others to call as 「18:30 陳小姐 0912-345-678」, earliest first.
    /// </summary>
    public static CoordinatorReportReservations Report(IReadOnlyList<CoordinatorReservation> cancelled, bool emailConfigured, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(cancelled);
        ArgumentNullException.ThrowIfNull(zone);
        var ordered = cancelled.OrderBy(reservation => reservation.ReservedAt).ThenBy(reservation => reservation.Id).ToList();
        return new CoordinatorReportReservations(
            ordered.Count,
            ordered.Count(reservation => CoordinatorReservationEmail.Sends(reservation, emailConfigured)),
            [.. ordered
                .Where(reservation => !CoordinatorReservationEmail.Sends(reservation, emailConfigured))
                .Select(reservation => $"{reservation.TimeOfDay(zone)} {reservation.CustomerName} {ReservationContact.FormatPhone(reservation.CustomerPhone)}")]);
    }
}
