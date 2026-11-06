using Shouldly;
using SmartAgri.Application.Notifications;
using SmartAgri.Application.Ordering;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Application.Tests.Ordering;

/// <summary>
/// The customer's online-order Emails (online-ordering design D5, Slice O4, #400): each outcome word for word — accepted
/// (with the promised time), declined and cancelled by the shop (the reason in words and the staff's note), cancelled by the
/// customer, expired — with the order's number, the shop, the pickup, 「取餐時付款」, the shop's phone, the lines and total
/// and the ordering page; never the status token or anything the customer typed.
/// </summary>
public class OrderingOrderEmailTests
{
    private const string Page = "https://order.example.org/order/wangji";

    private const string Facts =
        "訂單編號：#007\n店家：王記麵店\n取餐：外帶，{0}\n付款：取餐時付款\n店家電話：06-123-4567\n\n"
        + "明細：\n・紅燒牛肉麵 ×5　NT$1,350\n　大碗、滷蛋 ×2、小菜飲料套餐（涼拌小黃瓜、冬瓜檸檬）\n・餛飩湯 ×1　NT$60\n合計：NT$1,410\n\n"
        + "王記麵店的點餐網頁：https://order.example.org/order/wangji\n\n"
        + "——\n這封信由線上點餐系統代王記麵店寄出；回覆這封信不會有人讀取。\n";

    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000d1");

    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    /// <summary>Tuesday 2026-11-17 12:00 in Taipei.</summary>
    private static readonly DateTimeOffset Sent = new(2026, 11, 17, 12, 0, 0, TimeSpan.FromHours(8));

    private static readonly DateTimeOffset Slot = Sent.AddMinutes(30);

    [Fact]
    public void An_accepted_order_gives_the_promised_time_and_says_when_the_shop_moved_it()
    {
        var order = Scheduled();
        order.Accept(Guid.NewGuid(), Slot.AddMinutes(15), Sent.AddMinutes(2));

        var message = Compose(order);

        message.To.ShouldBe("chen@example.com");
        message.FromName.ShouldBe("王記麵店");
        message.Subject.ShouldBe("王記麵店：已接單（訂單 #007）");
        message.Body.ShouldBe(
            "王記麵店已接受您的訂單 #007，預計 11/17（二） 12:45 可取餐（店家調整了取餐時間）。請準時到店取餐，取餐時付款。如需更改或取消，請來電 06-123-4567。\n\n"
            + Fill("11/17（二） 12:45"));

        var kept = Scheduled();
        kept.Accept(Guid.NewGuid(), null, Sent.AddMinutes(2));
        Compose(kept).Body.ShouldStartWith("王記麵店已接受您的訂單 #007，預計 11/17（二） 12:30 可取餐。");
    }

    [Fact]
    public void A_declined_order_gives_the_reason_in_words_and_the_staffs_note()
    {
        var order = Scheduled();
        order.Decline(Guid.NewGuid(), OrderingDeclineReason.SoldOut, "牛肉麵賣完了", Sent.AddMinutes(2));

        var message = Compose(order);

        message.Subject.ShouldBe("王記麵店：無法接受您的訂單（訂單 #007）");
        message.Body.ShouldBe(
            "很抱歉，王記麵店無法接受您的訂單 #007。原因：有品項已經賣完。牛肉麵賣完了。這筆訂單不會製作，也不需要付款。歡迎改天再點，有問題請來電 06-123-4567。\n\n"
            + Fill("11/17（二） 12:30"));

        var busy = Scheduled();
        busy.Decline(Guid.NewGuid(), OrderingDeclineReason.TooBusy, null, Sent.AddMinutes(2));
        Compose(busy).Body.ShouldStartWith("很抱歉，王記麵店無法接受您的訂單 #007。原因：店家目前太忙。這筆訂單不會製作");
    }

    [Fact]
    public void An_order_the_shop_cancelled_gives_the_reason_and_note()
    {
        var order = Scheduled();
        order.Accept(Guid.NewGuid(), null, Sent.AddMinutes(2));
        order.CancelByShop(Guid.NewGuid(), OrderingCancelReason.Other, "瓦斯沒了。", Sent.AddMinutes(5));

        var message = Compose(order);

        message.Subject.ShouldBe("王記麵店：店家已取消您的訂單（訂單 #007）");
        message.Body.ShouldBe(
            "很抱歉，王記麵店已取消您的訂單 #007。原因：其他原因。瓦斯沒了。這筆訂單不會製作，也不需要付款。有問題請來電 06-123-4567。\n\n"
            + Fill("11/17（二） 12:30"));
    }

    [Fact]
    public void An_order_its_customer_cancelled_is_confirmed()
    {
        var order = Scheduled();
        order.CancelByCustomer(Sent.AddMinutes(1));

        var message = Compose(order);

        message.Subject.ShouldBe("王記麵店：您的訂單已取消（訂單 #007）");
        message.Body.ShouldBe("您已取消在王記麵店的訂單 #007，這筆訂單不會製作，也不需要付款。\n\n" + Fill("11/17（二） 12:30"));
    }

    [Fact]
    public void An_expired_order_says_it_was_cancelled_because_the_shop_did_not_answer_in_time()
    {
        var order = Asap();
        order.Expire(Sent.AddMinutes(15));

        var message = Compose(order);

        message.Subject.ShouldBe("王記麵店：訂單已自動取消（訂單 #007）");
        message.Body.ShouldBe(
            "很抱歉，店家未能及時確認，這筆訂單已自動取消（王記麵店，訂單 #007）。這筆訂單不會製作，也不需要付款。需要的話請重新點餐，或請來電 06-123-4567。\n\n"
            + Fill("儘快（11/17（二）約 12:15–12:30）"));
    }

    [Fact]
    public void Nothing_the_customer_typed_nor_the_token_is_in_any_Email_and_the_page_link_is_left_out_without_a_public_address()
    {
        var token = OrderingOrder.NewStatusToken();
        var order = Scheduled(token);
        order.Accept(Guid.NewGuid(), null, Sent.AddMinutes(2));

        var message = OrderingOrderEmail.Compose("王記麵店", "06-123-4567", Taipei, orderingPageUrl: null, order);

        foreach (var secret in new[] { token, OrderingOrder.HashStatusToken(token), "陳小姐", "0912345678", "0912-345-678", "麵硬一點", "請放門口", "chen@example.com" })
        {
            message.Body.ShouldNotContain(secret);
            message.Subject.ShouldNotContain(secret);
        }

        message.Body.ShouldNotContain("點餐網頁");
        message.Body.ShouldNotContain("http");
    }

    [Fact]
    public void Only_an_answered_order_with_an_address_is_told_and_only_when_SMTP_is_configured()
    {
        var pending = Scheduled();
        OrderingOrderEmail.Sends(pending, emailConfigured: true).ShouldBeFalse("nothing to tell while it waits");

        var accepted = Scheduled();
        accepted.Accept(Guid.NewGuid(), null, Sent.AddMinutes(2));
        OrderingOrderEmail.Sends(accepted, emailConfigured: true).ShouldBeTrue();
        OrderingOrderEmail.Sends(accepted, emailConfigured: false).ShouldBeFalse();

        accepted.MarkReady(Guid.NewGuid(), Sent.AddMinutes(20));
        OrderingOrderEmail.Sends(accepted, emailConfigured: true).ShouldBeFalse("ready and completed are not told by Email");

        var anonymous = Scheduled(email: null);
        anonymous.Accept(Guid.NewGuid(), null, Sent.AddMinutes(2));
        OrderingOrderEmail.Sends(anonymous, emailConfigured: true).ShouldBeFalse();

        var cleared = Scheduled();
        cleared.Decline(Guid.NewGuid(), OrderingDeclineReason.TooBusy, null, Sent.AddMinutes(2));
        cleared.ClearPersonalData(Sent.AddDays(91));
        OrderingOrderEmail.Sends(cleared, emailConfigured: true).ShouldBeFalse("the address was cleared");
    }

    [Fact]
    public void A_shop_name_or_note_with_line_breaks_stays_on_one_line()
    {
        var order = Scheduled();
        order.Accept(Guid.NewGuid(), null, Sent.AddMinutes(2));

        var message = OrderingOrderEmail.Compose("王記\r\nBcc: x@example.com", "06-123-4567", Taipei, Page, order);

        message.FromName.ShouldBe("王記 Bcc: x@example.com");
        message.Subject.ShouldNotContain("\n");
    }

    private static string Fill(string pickup) => string.Format(System.Globalization.CultureInfo.InvariantCulture, Facts, pickup);

    [Fact]
    public void A_delivery_order_says_delivery_its_zone_and_fee_and_cash_on_delivery_but_never_the_address()
    {
        var order = Delivery(OrderingServiceType.DeliveryScheduled, null);
        order.Accept(Guid.NewGuid(), Slot.AddMinutes(15), Sent.AddMinutes(2));

        var message = Compose(order);

        message.Subject.ShouldBe("王記麵店：已接單（訂單 #007）");
        message.Body.ShouldBe(
            "王記麵店已接受您的外送訂單 #007，預計 11/17（二） 12:45 送達（店家調整了外送時間）。餐點送達時付款（貨到付款）。如需更改或取消，請來電 06-123-4567。\n\n"
            + "訂單編號：#007\n店家：王記麵店\n外送：預計送達 11/17（二） 12:45\n外送區域：新營區其他里\n付款：貨到付款\n店家電話：06-123-4567\n\n"
            + "明細：\n・紅燒牛肉麵 ×5　NT$1,350\n　大碗、滷蛋 ×2、小菜飲料套餐（涼拌小黃瓜、冬瓜檸檬）\n・餛飩湯 ×1　NT$60\n"
            + "小計：NT$1,410\n運費：NT$30\n合計：NT$1,440\n\n"
            + "王記麵店的點餐網頁：https://order.example.org/order/wangji\n\n"
            + "——\n這封信由線上點餐系統代王記麵店寄出；回覆這封信不會有人讀取。\n");
        message.Body.ShouldNotContain("中山路");
        message.Body.ShouldNotContain("按電鈴");
        message.Body.ShouldNotContain("取餐時付款");
    }

    [Fact]
    public void An_ASAP_delivery_not_yet_accepted_gives_the_delivery_estimate()
    {
        var order = Delivery(OrderingServiceType.DeliveryAsap, new AsapEstimate(35, 50));
        order.CancelByCustomer(Sent.AddMinutes(1));

        var body = Compose(order).Body;

        body.ShouldContain("\n外送：儘快（11/17（二）約 12:35–12:50 送達）\n外送區域：新營區其他里\n付款：貨到付款\n");
        body.ShouldNotContain("中山路");
    }

    private static EmailMessage Compose(OrderingOrder order) => OrderingOrderEmail.Compose("王記麵店", "06-123-4567", Taipei, Page, order);

    private static IReadOnlyList<OrderingOrderLine> Lines() =>
    [
        new(
            "beef-noodle", "紅燒牛肉麵", 150, 5, 270, 1350,
            [
                new ChosenOption("size", "份量", "large", "大碗", 20, 1, []),
                new ChosenOption("toppings", "加料", "egg", "滷蛋", 15, 2, []),
                new ChosenOption(
                    "combo", "升級套餐", "set", "小菜飲料套餐", 60, 1,
                    [
                        new ChosenOption("side", "套餐小菜", "cucumber", "涼拌小黃瓜", 0, 1, []),
                        new ChosenOption("drink", "套餐飲料", "winter-melon", "冬瓜檸檬", 10, 1, []),
                    ]),
            ],
            "麵硬一點"),
        new("wonton-soup", "餛飩湯", 60, 1, 60, 60, [], null),
    ];

    private static BusinessHours Hours()
    {
        var periods = new Dictionary<DayOfWeek, IReadOnlyList<BusinessPeriod>>();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            periods[day] = [new BusinessPeriod(new TimeOnly(11, 0), new TimeOnly(14, 0))];
        }

        return new BusinessHours(new WeeklyHours(periods), Taipei);
    }

    private static OrderingOrder Scheduled(string? token = null, string? email = "chen@example.com") => OrderingOrder.Submit(
        Organization, OrderingServiceType.TakeoutScheduled, new DateOnly(2026, 11, 17), 7, Slot, null, "陳小姐", "0912345678", email, "請放門口", true,
        Lines(), 3, token ?? OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), Hours(), 15, Sent);

    private static OrderingOrder Asap() => OrderingOrder.Submit(
        Organization, OrderingServiceType.TakeoutAsap, new DateOnly(2026, 11, 17), 7, default, new AsapEstimate(15, 30), "陳小姐", "0912345678",
        "chen@example.com", "請放門口", true, Lines(), 3, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), Hours(), 15, Sent);

    private static OrderingOrder Delivery(OrderingServiceType type, AsapEstimate? asap) => OrderingOrder.Submit(
        Organization, type, new DateOnly(2026, 11, 17), 7, Slot, asap, "陳小姐", "0912345678", "chen@example.com", null, null,
        Lines(), 3, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), Hours(), 15, Sent,
        new OrderingOrderDelivery("xinying", "新營區其他里", 30, "新營區中山路 1 號 3 樓", "請按電鈴"));
}
