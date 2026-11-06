using System.Globalization;
using System.Text;
using SmartAgri.Application.Notifications;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Application.Ordering;

/// <summary>
/// The Email a customer gets when their online order was accepted, declined, cancelled (by the shop or by themselves) or
/// expired unanswered (online-ordering design D5, Slice O4, #400): one plain-text message in Traditional Chinese to the
/// address they left, from the platform's <c>Smtp:From</c> with the shop's name as the sender's name.
/// </summary>
/// <remarks>
/// <para>
/// It says what happened (with the shop's reason and note when it declined or cancelled), then the order: its number, the
/// shop, how and when it is picked up (the promised time once accepted), 「取餐時付款」, the shop's phone, the lines with
/// their options and totals, and — when the platform's public address is set — the shop's ordering page
/// <c>/order/{slug}</c>. A delivery order (#401) says 外送 and 「預計送達」, its zone (never the address) and fee, and
/// 「貨到付款」.
/// </para>
/// <para>
/// Never the status page's address: only the token's hash is stored, and a token is never put in a URL's path or query
/// (design D4). Nothing the customer typed is in it either — not their name, phone, comments — so a mistyped address shows
/// a stranger no more than an order of noodles; a short footer says the platform sent it for the shop and that a reply is
/// read by nobody.
/// </para>
/// </remarks>
public static class OrderingOrderEmail
{
    /// <summary>The statuses there is an Email for.</summary>
    public static bool Covers(OrderingOrderStatus status) =>
        status is OrderingOrderStatus.Accepted or OrderingOrderStatus.Declined or OrderingOrderStatus.Cancelled or OrderingOrderStatus.Expired;

    /// <summary>Whether <paramref name="order"/>'s customer is told its status by Email: they left an address (still kept),
    /// there is an Email for the status, and the platform sends Email (<paramref name="emailConfigured"/>). The one rule of
    /// the job's enqueueing and sending.</summary>
    public static bool Sends(OrderingOrder order, bool emailConfigured)
    {
        ArgumentNullException.ThrowIfNull(order);
        return emailConfigured && order.CustomerEmail is not null && Covers(order.Status);
    }

    /// <summary>The subject of the Email for <paramref name="order"/>'s status at <paramref name="storeName"/>.</summary>
    public static string Subject(string storeName, OrderingOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var store = OneLine(storeName);
        var number = OrderingOrder.FormatNumber(order.Number);
        return order.Status switch
        {
            OrderingOrderStatus.Accepted => $"{store}：已接單（訂單 {number}）",
            OrderingOrderStatus.Declined => $"{store}：無法接受您的訂單（訂單 {number}）",
            OrderingOrderStatus.Cancelled when order.CancelledBy == OrderingCanceller.Shop => $"{store}：店家已取消您的訂單（訂單 {number}）",
            OrderingOrderStatus.Cancelled => $"{store}：您的訂單已取消（訂單 {number}）",
            OrderingOrderStatus.Expired => $"{store}：訂單已自動取消（訂單 {number}）",
            _ => throw new ArgumentException("Only an accepted, declined, cancelled or expired order is told by Email.", nameof(order)),
        };
    }

    /// <summary>
    /// The Email to <paramref name="order"/>'s customer for its status: to <see cref="OrderingOrder.CustomerEmail"/>, from
    /// <paramref name="storeName"/> (the sender's name), with <paramref name="shopPhone"/>, times in <paramref name="zone"/>,
    /// and <paramref name="orderingPageUrl"/> (the shop's <c>/order/{slug}</c>; <see langword="null"/> when the platform's
    /// public address is not set).
    /// </summary>
    public static EmailMessage Compose(string storeName, string shopPhone, TimeZoneInfo zone, string? orderingPageUrl, OrderingOrder order)
    {
        ArgumentNullException.ThrowIfNull(storeName);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(order);
        var to = order.CustomerEmail
            ?? throw new ArgumentException("Only a customer who left an Email address is told by Email.", nameof(order));
        var store = OneLine(storeName);
        var phone = string.IsNullOrWhiteSpace(shopPhone) ? null : OneLine(shopPhone);
        var number = OrderingOrder.FormatNumber(order.Number);
        var call = phone is null ? "請直接與店家聯絡" : $"請來電 {phone}";

        var opening = order.Status switch
        {
            OrderingOrderStatus.Accepted when order.ServiceType.IsDelivery() =>
                $"{store}已接受您的外送訂單 {number}，預計 {Format(order.PromisedAt ?? order.PickupAt, order, zone)} 送達"
                + (order.ServiceType == OrderingServiceType.DeliveryScheduled && order.PromisedAt > order.PickupAt ? "（店家調整了外送時間）" : string.Empty)
                + $"。餐點送達時付款（貨到付款）。如需更改或取消，{call}。",
            OrderingOrderStatus.Accepted =>
                $"{store}已接受您的訂單 {number}，預計 {Format(order.PromisedAt ?? order.PickupAt, order, zone)} 可取餐"
                + (order.ServiceType == OrderingServiceType.TakeoutScheduled && order.PromisedAt > order.PickupAt ? "（店家調整了取餐時間）" : string.Empty)
                + $"。請準時到店取餐，取餐時付款。如需更改或取消，{call}。",
            OrderingOrderStatus.Declined =>
                $"很抱歉，{store}無法接受您的訂單 {number}。{Reason(DeclineWords(order.DeclineReason), order.DeclineNote)}"
                + $"這筆訂單不會製作，也不需要付款。歡迎改天再點，有問題{call}。",
            OrderingOrderStatus.Cancelled when order.CancelledBy == OrderingCanceller.Shop =>
                $"很抱歉，{store}已取消您的訂單 {number}。{Reason(CancelWords(order.CancelReason), order.CancelNote)}"
                + $"這筆訂單不會製作，也不需要付款。有問題{call}。",
            OrderingOrderStatus.Cancelled =>
                $"您已取消在{store}的訂單 {number}，這筆訂單不會製作，也不需要付款。",
            OrderingOrderStatus.Expired =>
                $"很抱歉，店家未能及時確認，這筆訂單已自動取消（{store}，訂單 {number}）。這筆訂單不會製作，也不需要付款。"
                + $"需要的話請重新點餐，或{call}。",
            _ => throw new ArgumentException("Only an accepted, declined, cancelled or expired order is told by Email.", nameof(order)),
        };

        var body = new StringBuilder();
        body.Append(opening).Append("\n\n");
        body.Append(CultureInfo.InvariantCulture, $"訂單編號：{number}\n");
        body.Append(CultureInfo.InvariantCulture, $"店家：{store}\n");
        if (order.Delivery is { } delivery)
        {
            // The zone is the shop's own words; the customer's address stays out of the Email (#401).
            body.Append(CultureInfo.InvariantCulture, $"外送：{Pickup(order, zone)}\n");
            body.Append(CultureInfo.InvariantCulture, $"外送區域：{OneLine(delivery.ZoneName)}\n");
            body.Append("付款：貨到付款\n");
        }
        else
        {
            body.Append(CultureInfo.InvariantCulture, $"取餐：{Pickup(order, zone)}\n");
            body.Append("付款：取餐時付款\n");
        }
        if (phone is not null)
        {
            body.Append(CultureInfo.InvariantCulture, $"店家電話：{phone}\n");
        }

        body.Append("\n明細：\n");
        foreach (var line in order.Lines)
        {
            body.Append(CultureInfo.InvariantCulture, $"・{OneLine(line.ItemName)} ×{line.Quantity}　{Money(line.LineTotal)}\n");
            var options = OptionsText(line.Options);
            if (options.Length > 0)
            {
                body.Append(CultureInfo.InvariantCulture, $"　{options}\n");
            }
        }

        if (order.Subtotal != order.Total || order.ServiceType.IsDelivery())
        {
            body.Append(CultureInfo.InvariantCulture, $"小計：{Money(order.Subtotal)}\n");
        }

        if (order.ServiceType.IsDelivery())
        {
            body.Append(CultureInfo.InvariantCulture, $"運費：{Money(order.DeliveryFee)}\n");
        }

        body.Append(CultureInfo.InvariantCulture, $"合計：{Money(order.Total)}\n");
        if (!string.IsNullOrWhiteSpace(orderingPageUrl))
        {
            body.Append(CultureInfo.InvariantCulture, $"\n{store}的點餐網頁：{orderingPageUrl.Trim()}\n");
        }

        body.Append("\n——\n");
        body.Append(CultureInfo.InvariantCulture, $"這封信由線上點餐系統代{store}寄出；回覆這封信不會有人讀取。\n");
        return new EmailMessage(to, Subject(store, order), body.ToString(), FromName: store);
    }

    /// <summary>The reason as the status page writes it (<c>apps/shop</c>'s <c>reasonText</c>): 「原因：有品項已經賣完。牛肉麵賣完了。」</summary>
    private static string Reason(string words, string? note)
    {
        var text = $"原因：{words}。";
        if (!string.IsNullOrWhiteSpace(note))
        {
            var cleaned = OneLine(note);
            text += cleaned + (cleaned.EndsWith('。') ? string.Empty : "。");
        }

        return text;
    }

    /// <summary>A decline reason in words, as the status page says it.</summary>
    public static string DeclineWords(OrderingDeclineReason? reason) => reason switch
    {
        OrderingDeclineReason.TooBusy => "店家目前太忙",
        OrderingDeclineReason.SoldOut => "有品項已經賣完",
        OrderingDeclineReason.Closed => "店家已經休息",
        OrderingDeclineReason.CannotMeetRequest => "無法配合訂單的需求或時間",
        _ => "其他原因",
    };

    /// <summary>A shop's cancel reason in words, as the status page says it.</summary>
    public static string CancelWords(OrderingCancelReason? reason) => reason switch
    {
        OrderingCancelReason.CustomerNoShow => "超過取餐時間沒有取餐",
        OrderingCancelReason.CustomerRequest => "依您的要求取消",
        _ => "其他原因",
    };

    /// <summary>「外帶，11/17（二）12:30」; an ASAP order not yet accepted 「外帶，儘快（11/17（二）約 12:15–12:30）」; an accepted
    /// one at its promised time. A delivery order (#401): 「預計送達 11/17（二）12:30」, 「儘快（11/17（二）約 12:35–12:50 送達）」.</summary>
    private static string Pickup(OrderingOrder order, TimeZoneInfo zone)
    {
        var delivery = order.ServiceType.IsDelivery();
        var prefix = delivery ? "預計送達 " : "外帶，";
        if (order.PromisedAt is { } promised)
        {
            return prefix + Format(promised, order, zone);
        }

        if (order.ServiceType.IsAsap() && order.AsapMinMinutes is { } min)
        {
            var from = TimeZoneInfo.ConvertTime(order.CreatedAt.AddMinutes(min), zone);
            var until = TimeZoneInfo.ConvertTime(order.PickupAt, zone);
            return (delivery ? string.Empty : "外帶，") + $"儘快（{ShopAnnouncements.FormatDate(DateOnly.FromDateTime(from.DateTime))}約 "
                + $"{ShopAnnouncements.FormatTime(TimeOnly.FromDateTime(from.DateTime))}–{ShopAnnouncements.FormatTime(TimeOnly.FromDateTime(until.DateTime))}"
                + (delivery ? " 送達）" : "）");
        }

        return prefix + Format(order.PickupAt, order, zone);
    }

    /// <summary>「11/17（二）12:30」 in <paramref name="zone"/>; the small hours of an overnight business day 「11/14（六）00:30（隔天）」
    /// (the order's <see cref="OrderingOrder.BusinessDate"/> is the day before).</summary>
    private static string Format(DateTimeOffset instant, OrderingOrder order, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        var date = DateOnly.FromDateTime(local);
        var time = ShopAnnouncements.FormatTime(TimeOnly.FromDateTime(local));
        return date == order.BusinessDate.AddDays(1)
            ? $"{ShopAnnouncements.FormatDate(order.BusinessDate)} {time}（隔天）"
            : $"{ShopAnnouncements.FormatDate(date)} {time}";
    }

    /// <summary>The chosen options as the status page lists them: 「大碗、加蛋 ×2、套餐（小黃瓜、冬瓜茶）」.</summary>
    private static string OptionsText(IReadOnlyList<ChosenOption> options) =>
        string.Join('、', options.Select(option =>
        {
            var inner = OptionsText(option.Options);
            return OneLine(option.OptionName)
                + (option.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" ×{option.Count}") : string.Empty)
                + (inner.Length > 0 ? $"（{inner}）" : string.Empty);
        }));

    private static string Money(int amount) => "NT$" + amount.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>Names are typed by people: no line breaks or control characters (as <see cref="HandoffEmail"/>).</summary>
    private static string OneLine(string text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(character => !char.IsControl(character))])));
}
