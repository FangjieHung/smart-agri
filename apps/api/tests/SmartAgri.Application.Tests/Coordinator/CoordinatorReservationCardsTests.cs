using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// The members' side of online reservations (store-reservations design Slices R2 and R3, #390, #391): 「訂位」 and the
/// reservation postbacks are routed before any model call, and the card, the decision replies and the 「訂位」 list stay
/// inside LINE's limits (alt text 400, labels 20, postback data 300, 12 bubbles), show the date and time as the page does,
/// carry nothing but what the shop needs, and say when the members must call because no Email will reach the customer.
/// </summary>
public class CoordinatorReservationCardsTests
{
    private const string UserId = "Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");

    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    // --- Routing --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("訂位")]
    [InlineData("查看訂位")]
    [InlineData("  訂位\n")]
    [InlineData("　查看訂位　")]
    public void A_member_asking_for_the_reservations(string text)
    {
        Route(Text(text), bound: true).Kind.ShouldBe(CoordinatorIntentKind.Reservations);
        Route(Text(text), bound: false).Kind.ShouldBe(CoordinatorIntentKind.AskForCode);
    }

    [Theory]
    [InlineData("訂位吧")]
    [InlineData("明天訂位幾筆")]
    [InlineData("訂 位")]
    public void Other_text_with_reservations_in_it_is_a_command(string text)
    {
        Route(Text(text), bound: true).Kind.ShouldBe(CoordinatorIntentKind.Command);
    }

    [Fact]
    public void The_reservation_postbacks_carry_the_organization_and_reservation()
    {
        var reservation = Guid.CreateVersion7();
        var accept = Route(Postback(CoordinatorMessageRouter.AcceptReservationPostback(Organization, reservation)), bound: true);
        (accept.Kind, accept.OrganizationId, accept.ReservationId).ShouldBe((CoordinatorIntentKind.AcceptReservation, Organization, reservation));
        var decline = Route(Postback(CoordinatorMessageRouter.DeclineReservationPostback(Organization, reservation)), bound: true);
        (decline.Kind, decline.OrganizationId, decline.ReservationId).ShouldBe((CoordinatorIntentKind.DeclineReservation, Organization, reservation));
        var list = Route(Postback(CoordinatorMessageRouter.ReservationsPostback(Organization)), bound: true);
        (list.Kind, list.OrganizationId).ShouldBe((CoordinatorIntentKind.ReservationsFor, Organization));

        Route(Postback(CoordinatorMessageRouter.AcceptReservationPostback(Organization, reservation)), bound: false).Kind
            .ShouldBe(CoordinatorIntentKind.AskForCode);
        CoordinatorMessageRouter.AcceptReservationPostback(Organization, reservation).Length.ShouldBeLessThanOrEqualTo(300);
    }

    [Theory]
    [InlineData("coordinator:reservation-accept:")]
    [InlineData("coordinator:reservation-accept:00000000000000000000000000000000:0194a1000000700080000000000000c1")]
    [InlineData("coordinator:reservation-accept:0194a1000000700080000000000000c1")]
    [InlineData("coordinator:reservation-decline:0194a1000000700080000000000000c1:xyz")]
    [InlineData("coordinator:reservations:")]
    [InlineData("coordinator:reservations:0194a1000000700080000000000000c1:1")]
    public void A_malformed_reservation_postback_is_ignored(string data)
    {
        Route(Postback(data), bound: true).Kind.ShouldBe(CoordinatorIntentKind.Ignore);
    }

    // --- The new-reservation card ---------------------------------------------------------------------

    [Fact]
    public void The_card_shows_the_reservation_and_offers_call_accept_and_decline()
    {
        var reservation = Reservation(new DateOnly(2026, 11, 8), new DateTimeOffset(2026, 11, 8, 18, 30, 0, TimeSpan.FromHours(8)), email: "chen@example.com", note: "需要兒童椅");

        var card = CoordinatorReservationCards.NewReservation("王記麵店", Taipei, reservation, emailConfigured: true);

        card["altText"]!.GetValue<string>().ShouldBe("新訂位：王記麵店 11/8（日） 18:30 4 位");
        Texts(card["contents"]!["header"]!).ShouldBe(["新訂位｜王記麵店"]);
        Texts(card["contents"]!["body"]!).ShouldBe(["11/8（日） 18:30", "4 位", "姓名", "陳小姐", "電話", "0912-345-678", "Email", "chen@example.com", "備註", "需要兒童椅"]);
        Actions(card).ShouldBe(
        [
            ("uri", "撥打電話", "tel:0912345678"),
            ("postback", "接受", CoordinatorMessageRouter.AcceptReservationPostback(Organization, reservation.Id)),
            ("postback", "婉拒", CoordinatorMessageRouter.DeclineReservationPostback(Organization, reservation.Id)),
        ]);
        card.ToJsonString().ShouldNotContain(UserId);
    }

    [Fact]
    public void Without_Email_or_note_the_card_says_so_and_after_midnight_it_says_the_next_day()
    {
        // Saturday's 18:00–01:00: Sunday 00:30 is Saturday's.
        var reservation = Reservation(new DateOnly(2026, 11, 7), new DateTimeOffset(2026, 11, 8, 0, 30, 0, TimeSpan.FromHours(8)), email: null, note: null);

        var card = CoordinatorReservationCards.NewReservation("王記麵店", Taipei, reservation, emailConfigured: true);

        Texts(card["contents"]!["body"]!).ShouldBe(["11/7（六） 00:30（隔天）", "4 位", "姓名", "陳小姐", "電話", "0912-345-678", "Email", "沒有留 Email，請來電確認"]);
        card["altText"]!.GetValue<string>().ShouldBe("新訂位：王記麵店 11/7（六） 00:30（隔天） 4 位");

        // The 「訂位」 list keeps R2's words: by then a member may have called already.
        var bubble = CoordinatorReservationCards.List("王記麵店", Taipei, [reservation])[0]["contents"]!["contents"]![0]!;
        Texts(bubble["body"]!).ShouldBe(["4 位", "姓名", "陳小姐", "電話", "0912-345-678", "Email", "沒有留 Email"]);
    }

    [Fact]
    public void Without_SMTP_every_card_asks_to_call_and_shows_an_Email_only_when_the_customer_left_one()
    {
        var at = new DateTimeOffset(2026, 11, 8, 18, 30, 0, TimeSpan.FromHours(8));
        var none = CoordinatorReservationCards.NewReservation("王記麵店", Taipei, Reservation(new DateOnly(2026, 11, 8), at, note: "需要兒童椅"), emailConfigured: false);
        var left = CoordinatorReservationCards.NewReservation("王記麵店", Taipei, Reservation(new DateOnly(2026, 11, 8), at, email: "chen@example.com"), emailConfigured: false);

        Texts(none["contents"]!["body"]!).ShouldBe(["11/8（日） 18:30", "4 位", "姓名", "陳小姐", "電話", "0912-345-678", "備註", "需要兒童椅", "請來電確認"]);
        Texts(left["contents"]!["body"]!).ShouldBe(["11/8（日） 18:30", "4 位", "姓名", "陳小姐", "電話", "0912-345-678", "Email", "chen@example.com", "請來電確認"]);
        Actions(none).Select(action => action.Label).ShouldBe(["撥打電話", "接受", "婉拒"]);
    }

    [Fact]
    public void A_long_store_name_keeps_the_alt_text_and_labels_inside_LINEs_limits()
    {
        var reservation = Reservation(new DateOnly(2026, 11, 8), new DateTimeOffset(2026, 11, 8, 18, 30, 0, TimeSpan.FromHours(8)));
        var card = CoordinatorReservationCards.NewReservation(new string('長', 450), Taipei, reservation, emailConfigured: true);

        card["altText"]!.GetValue<string>().Length.ShouldBe(400);
        Actions(card).ShouldAllBe(action => action.Label.Length <= 20 && action.Value.Length <= 300);
        var list = CoordinatorReservationCards.List(new string('長', 450), Taipei, [reservation]);
        list[0]["altText"]!.GetValue<string>().Length.ShouldBeLessThanOrEqualTo(400);
    }

    // --- Decisions ------------------------------------------------------------------------------------

    [Fact]
    public void The_decision_replies_and_the_reasons_a_press_changed_nothing()
    {
        var accepted = Decided(CoordinatorReservationStatus.Accepted, email: "chen@example.com");
        CoordinatorReservationCards.DecidedText(accepted, Taipei, emailConfigured: true).ShouldBe("已接受：陳小姐 11/8（日） 18:30 4 位。會寄確認信給顧客。");
        var reply = CoordinatorReservationCards.Decided(accepted, Taipei, emailConfigured: true);
        reply["altText"]!.GetValue<string>().ShouldBe("已接受：陳小姐 11/8（日） 18:30 4 位。會寄確認信給顧客。");
        Actions(reply).ShouldBe([("uri", "撥打電話", "tel:0912345678")], "R2's 〔撥打電話〕 stays");
        CoordinatorReservationCards.DecidedText(Decided(CoordinatorReservationStatus.Declined, email: "chen@example.com"), Taipei, emailConfigured: true)
            .ShouldBe("已婉拒：陳小姐 11/8（日） 18:30 4 位。會寄婉拒信給顧客。");

        // No address: the member calls. No SMTP: the member calls, whatever the customer left.
        CoordinatorReservationCards.DecidedText(Decided(CoordinatorReservationStatus.Accepted), Taipei, emailConfigured: true)
            .ShouldBe("已接受：陳小姐 11/8（日） 18:30 4 位。這位顧客沒有留 Email，請來電 0912-345-678。");
        CoordinatorReservationCards.DecidedText(Decided(CoordinatorReservationStatus.Declined), Taipei, emailConfigured: true)
            .ShouldBe("已婉拒：陳小姐 11/8（日） 18:30 4 位。這位顧客沒有留 Email，請來電 0912-345-678。");
        CoordinatorReservationCards.DecidedText(accepted, Taipei, emailConfigured: false)
            .ShouldBe("已接受：陳小姐 11/8（日） 18:30 4 位。平台沒有設定寄信，請來電 0912-345-678。");
        CoordinatorReservationCards.DecidedText(Decided(CoordinatorReservationStatus.Declined), Taipei, emailConfigured: false)
            .ShouldBe("已婉拒：陳小姐 11/8（日） 18:30 4 位。平台沒有設定寄信，請來電 0912-345-678。");
        Actions(CoordinatorReservationCards.Decided(Decided(CoordinatorReservationStatus.Accepted), Taipei, emailConfigured: false))
            .ShouldBe([("uri", "撥打電話", "tel:0912345678")]);
        Should.Throw<ArgumentException>(() => CoordinatorReservationCards.DecidedText(
            Reservation(new DateOnly(2026, 11, 8), new DateTimeOffset(2026, 11, 8, 18, 30, 0, TimeSpan.FromHours(8))), Taipei, emailConfigured: true));

        var at = new DateTimeOffset(2026, 11, 6, 14, 2, 0, TimeSpan.FromHours(8));
        CoordinatorReservationCards.AlreadyHandled(CoordinatorReservationStatus.Accepted, "老闆", at, Taipei).ShouldBe("這筆訂位已由老闆在 11/6（五）14:02 接受。");
        CoordinatorReservationCards.AlreadyHandled(CoordinatorReservationStatus.Declined, "王小明", at, Taipei).ShouldBe("這筆訂位已由王小明在 11/6（五）14:02 婉拒。");
        CoordinatorReservationCards.Expired(60).ShouldBe("這筆訂位已過期：訂位時間前 60 分鐘還沒有人處理。");
        CoordinatorReservationCards.Expired(0).ShouldBe("這筆訂位已過期：到訂位時間還沒有人處理。");
    }

    // --- 「訂位」 ---------------------------------------------------------------------------------------

    [Fact]
    public void The_list_is_a_carousel_with_buttons_on_the_pending_ones()
    {
        var pending = Reservation(new DateOnly(2026, 11, 8), new DateTimeOffset(2026, 11, 8, 18, 30, 0, TimeSpan.FromHours(8)));
        var accepted = Decided(CoordinatorReservationStatus.Accepted);

        var messages = CoordinatorReservationCards.List("王記麵店", Taipei, [pending, accepted]);

        var carousel = messages.ShouldHaveSingleItem();
        carousel["altText"]!.GetValue<string>().ShouldBe("王記麵店 接下來 7 天的訂位：待處理 1 筆、已接受 1 筆。");
        var bubbles = carousel["contents"]!["contents"]!.AsArray();
        bubbles.Count.ShouldBe(2);
        Texts(bubbles[0]!["header"]!).ShouldBe(["王記麵店", "11/8（日） 18:30", "待處理"]);
        Texts(bubbles[1]!["header"]!).ShouldBe(["王記麵店", "11/8（日） 18:30", "已接受"]);
        Actions(bubbles[0]!).Select(action => action.Label).ShouldBe(["撥打電話", "接受", "婉拒"]);
        Actions(bubbles[1]!).Select(action => action.Label).ShouldBe(["撥打電話"]);
    }

    [Fact]
    public void An_empty_list_says_so_and_more_than_12_says_how_many_more()
    {
        CoordinatorReservationCards.List("王記麵店", Taipei, []).ShouldHaveSingleItem()["text"]!.GetValue<string>()
            .ShouldBe("王記麵店：接下來 7 天沒有訂位。");

        var many = Enumerable.Range(0, 15)
            .Select(index => Reservation(new DateOnly(2026, 11, 8), new DateTimeOffset(2026, 11, 8, 11, 0, 0, TimeSpan.FromHours(8)).AddMinutes(15 * index)))
            .ToList();
        var messages = CoordinatorReservationCards.List("王記麵店", Taipei, many);

        messages.Count.ShouldBe(2);
        messages[0]["contents"]!["contents"]!.AsArray().Count.ShouldBe(CoordinatorReservationCards.CarouselMaxBubbles);
        messages[0]["altText"]!.GetValue<string>().ShouldBe("王記麵店 接下來 7 天的訂位：待處理 15 筆、已接受 0 筆。");
        messages[1]["text"]!.GetValue<string>().ShouldBe("另外還有 3 筆訂位沒有列出（只列最早的 12 筆）。");
    }

    [Fact]
    public void A_member_of_several_shops_chooses_one_by_quick_reply()
    {
        var second = Guid.CreateVersion7();
        var choice = CoordinatorReservationCards.ShopChoice([(Organization, "王記麵店"), (second, new string('長', 30))]);

        choice["text"]!.GetValue<string>().ShouldBe(CoordinatorReservationCards.ChooseShop);
        var items = choice["quickReply"]!["items"]!.AsArray().Select(item => item!["action"]!).ToList();
        items.Select(action => action["data"]!.GetValue<string>())
            .ShouldBe([CoordinatorMessageRouter.ReservationsPostback(Organization), CoordinatorMessageRouter.ReservationsPostback(second)]);
        items.ShouldAllBe(action => action["label"]!.GetValue<string>().Length <= 20);
        items[0]["displayText"]!.GetValue<string>().ShouldBe("查看王記麵店的訂位");
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static CoordinatorReservation Reservation(DateOnly date, DateTimeOffset at, string? email = null, string? note = null) =>
        CoordinatorReservation.Submit(
            Organization,
            new ReservationSlot(date, at, at.DateTime),
            4,
            "陳小姐",
            "0912345678",
            email,
            note,
            CoordinatorReservation.NewSubmissionKey(),
            at.AddDays(-2));

    /// <summary>A reservation as it is read back after a member's decision (the persistence layer sets these columns).</summary>
    private static CoordinatorReservation Decided(CoordinatorReservationStatus status, string? email = null)
    {
        var reservation = Reservation(new DateOnly(2026, 11, 8), new DateTimeOffset(2026, 11, 8, 18, 30, 0, TimeSpan.FromHours(8)), email: email);
        typeof(CoordinatorReservation).GetProperty(nameof(CoordinatorReservation.Status))!.SetValue(reservation, status);
        return reservation;
    }

    private static CoordinatorIntent Route(LineWebhookEvent lineEvent, bool bound) => CoordinatorMessageRouter.Route(lineEvent, bound);

    private static LineWebhookEvent Text(string text) =>
        Event("message", $$""","message":{"id":"m","type":"text","text":{{JsonSerializer.Serialize(text)}}}""");

    private static LineWebhookEvent Postback(string data) =>
        Event("postback", $$""","postback":{"data":{{JsonSerializer.Serialize(data)}}}""");

    private static LineWebhookEvent Event(string type, string extra)
    {
        var body = $$"""
            {"destination":"Ubot","events":[{"type":"{{type}}","mode":"active","timestamp":1727000000000,
              "source":{"type":"user","userId":"{{UserId}}"},"webhookEventId":"01J0000000000000000000000R","replyToken":"reply"{{extra}}}]}
            """;
        return LineWebhookPayload.TryParse(Encoding.UTF8.GetBytes(body))!.Events.ShouldHaveSingleItem();
    }

    private static List<string> Texts(JsonNode node) =>
        [.. Nodes(node).Where(child => child["type"]?.GetValue<string>() == "text").Select(child => child["text"]!.GetValue<string>())];

    /// <summary>Every button's action of <paramref name="node"/>: type, label, and URI or postback data.</summary>
    private static List<(string Type, string Label, string Value)> Actions(JsonNode node) =>
        [.. Nodes(node)
            .Where(child => child["type"]?.GetValue<string>() == "button")
            .Select(child => child["action"]!)
            .Select(action => (
                action["type"]!.GetValue<string>(),
                action["label"]!.GetValue<string>(),
                action["type"]!.GetValue<string>() == "uri" ? action["uri"]!.GetValue<string>() : action["data"]!.GetValue<string>()))];

    private static IEnumerable<JsonObject> Nodes(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                yield return obj;
                foreach (var (_, child) in obj)
                {
                    if (child is not null)
                    {
                        foreach (var descendant in Nodes(child))
                        {
                            yield return descendant;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        foreach (var descendant in Nodes(item))
                        {
                            yield return descendant;
                        }
                    }
                }

                break;
        }
    }
}
