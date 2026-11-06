using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Domain.Ordering;
using static SmartAgri.Domain.Tests.Ordering.OrderingSamples;

namespace SmartAgri.Domain.Tests.Ordering;

/// <summary>
/// 外送 (online-ordering design D10, Slice O5, #401): the import file's zones, fees and minimums and their rules; delivery
/// slots on their own hours and lead time; the ASAP delivery estimate (the band plus the ride); a delivery order's total
/// and its address cleared after 90 days.
/// </summary>
public class OrderingDeliveryTests
{
    private static readonly DateTimeOffset TuesdayNine = AtTaipei(11, 10, 9);

    [Fact]
    public void The_example_file_delivers_to_three_zones_on_its_own_hours()
    {
        var shop = Example().Shop;

        shop.Services.ShouldBe(new OrderingServices(true, true, true, true));
        var delivery = shop.Delivery.ShouldNotBeNull();
        delivery.Zones.Select(zone => (zone.Id, zone.Fee, zone.MinimumSubtotal)).ShouldBe(
            [("minquan", 0, 150), ("xinying", 30, 200), ("within-5km", 60, 400)]);
        delivery.OrderingHours.ShouldNotBeNull()[DayOfWeek.Tuesday][0].ToString().ShouldBe("11:00–13:30");
        delivery.MinLeadMinutes.ShouldBe(45);
        delivery.AsapExtraMinutes.ShouldBe(20);
        delivery.Note.ShouldNotBeNull();
        delivery.Zone("xinying")!.Name.ShouldBe("新營區其他里");
        delivery.Zone("nowhere").ShouldBeNull();
    }

    [Fact]
    public void Optional_delivery_fields_take_their_defaults_and_the_takeout_hours()
    {
        var tree = ExampleTree();
        tree["shop"]!["delivery"] = JsonNode.Parse("""{ "zones": [{ "id": "near", "name": "3 公里內", "fee": 30 }] }""");

        var delivery = Valid(tree.ToJsonString()).Shop.Delivery.ShouldNotBeNull();

        delivery.ShouldBe(delivery with { OrderingHours = null, MinLeadMinutes = 45, AsapExtraMinutes = 20, Note = null });
        delivery.Zones.ShouldBe([new DeliveryZone("near", "3 公里內", 30, 0)]);
    }

    [Fact]
    public void A_file_without_delivery_still_imports_and_offers_none()
    {
        var tree = ExampleTree();
        var shop = tree["shop"]!.AsObject();
        shop.Remove("delivery");
        shop["services"] = JsonNode.Parse("""{ "takeoutScheduled": true, "takeoutAsap": true }""");

        var settings = Valid(tree.ToJsonString()).Shop;

        settings.Services.ShouldBe(new OrderingServices(true, true));
        settings.Delivery.ShouldBeNull();
    }

    [Fact]
    public void Every_wrong_zone_fee_and_minimum_is_reported()
    {
        var tree = ExampleTree();
        tree["shop"]!["delivery"] = JsonNode.Parse("""
            {
              "zones": [
                { "id": "near", "name": "3 公里內", "fee": 30, "minimumSubtotal": 200 },
                { "id": "near", "name": "", "fee": -1, "minimumSubtotal": 100001 },
                { "id": "Far Away", "name": "long", "fee": 10001 },
                { "id": "x", "name": "x", "fee": 1.5, "colour": "red" }
              ],
              "minLeadMinutes": 1441,
              "asapExtraMinutes": 121
            }
            """);
        tree["shop"]!["delivery"]!["zones"]![2]!["name"] = new string('區', 41);

        var errors = OrderingShopJson.Parse(tree.ToJsonString()).Errors.Select(error => error.ToString()).ToList();

        errors.ShouldContain(error => error.StartsWith("$.shop.delivery.zones[1].id：「near」和 $.shop.delivery.zones[0] 的外送區域 id 重複", StringComparison.Ordinal));
        errors.ShouldContain("$.shop.delivery.zones[1].name：不能是空白。");
        errors.ShouldContain("$.shop.delivery.zones[1].fee：必須是 0 到 10000 的整數（目前 -1）。");
        errors.ShouldContain("$.shop.delivery.zones[1].minimumSubtotal：必須是 0 到 100000 的整數（目前 100001）。");
        errors.ShouldContain(error => error.StartsWith("$.shop.delivery.zones[2].id：必須是", StringComparison.Ordinal));
        errors.ShouldContain(error => error.StartsWith("$.shop.delivery.zones[2].name：最多 40 個字", StringComparison.Ordinal));
        errors.ShouldContain("$.shop.delivery.zones[2].fee：必須是 0 到 10000 的整數（目前 10001）。");
        errors.ShouldContain("$.shop.delivery.zones[3].fee：必須是整數。");
        errors.ShouldContain(error => error.StartsWith("$.shop.delivery.zones[3].colour：不認得的欄位", StringComparison.Ordinal));
        errors.ShouldContain("$.shop.delivery.minLeadMinutes：必須是 0 到 1440 的整數（目前 1441）。");
        errors.ShouldContain("$.shop.delivery.asapExtraMinutes：必須是 0 到 120 的整數（目前 121）。");
    }

    [Fact]
    public void Delivery_needs_its_section_and_zones_and_ASAP_delivery_needs_delivery()
    {
        var withoutSection = ExampleTree();
        withoutSection["shop"]!.AsObject().Remove("delivery");
        OrderingShopJson.Parse(withoutSection.ToJsonString()).Errors.Select(error => error.Path).ShouldBe(["$.shop.delivery"]);

        var noZones = ExampleTree();
        noZones["shop"]!["delivery"] = JsonNode.Parse("""{ "zones": [] }""");
        OrderingShopJson.Parse(noZones.ToJsonString()).Errors.Select(error => error.ToString())
            .ShouldBe(["$.shop.delivery.zones：至少要有一個外送區域；不外送請把 services.delivery 設成 false。"]);

        var asapOnly = ExampleTree();
        asapOnly["shop"]!["services"] = JsonNode.Parse("""{ "takeoutScheduled": true, "takeoutAsap": false, "delivery": false, "deliveryAsap": true }""");
        OrderingShopJson.Parse(asapOnly.ToJsonString()).Errors.Select(error => error.ToString())
            .ShouldBe(["$.shop.services.deliveryAsap：要先開放外送（delivery: true）才能開放外送儘快。"]);

        var deliveryOnly = ExampleTree();
        deliveryOnly["shop"]!["services"] = JsonNode.Parse("""{ "takeoutScheduled": false, "takeoutAsap": false, "delivery": true }""");
        OrderingShopJson.Parse(deliveryOnly.ToJsonString()).Errors.ShouldBeEmpty();
    }

    [Fact]
    public void The_stored_section_reads_back_the_same_and_tells_a_change()
    {
        var settings = Example().Shop;
        var written = OrderingShopJson.WriteDelivery(settings.Delivery);

        OrderingShopJson.WriteDelivery(OrderingShopJson.ReadDelivery(written)).ShouldBe(written);
        OrderingShopJson.WriteDelivery(null).ShouldBe("null");
        var cheaper = settings with
        {
            Delivery = settings.Delivery! with { Zones = [.. settings.Delivery.Zones.Select(zone => zone with { Fee = 0 })] },
        };
        OrderingShopChange.Between(settings, cheaper).ShouldBe(["delivery"]);
        OrderingShopChange.Between(settings, settings with { Delivery = settings.Delivery with { } }).ShouldBeEmpty();
    }

    [Fact]
    public void Delivery_slots_use_the_delivery_hours_and_lead_time()
    {
        var shop = Shop(
            Week(),
            minLeadMinutes: 0,
            delivery: new OrderingDeliverySettings(Zones(), Hours([(DayOfWeek.Tuesday, "17:00", "19:00")]), MinLeadMinutes: 45));

        var takeout = OrderingSlots.Scheduled(shop, AtTaipei(11, 10, 16, 30));
        var delivery = OrderingSlots.Scheduled(shop, AtTaipei(11, 10, 16, 30), channel: OrderingChannel.Delivery);

        takeout[0].Slots[0].Value.ShouldBe("2026-11-10T17:00");
        delivery.Select(date => date.Date).ShouldBe([new DateOnly(2026, 11, 10)]);
        delivery[0].Slots.Select(slot => slot.Value[11..]).ShouldBe(["17:30", "18:00", "18:30", "19:00"]);
        OrderingSlots.On(shop, new DateOnly(2026, 11, 11), TuesdayNine, channel: OrderingChannel.Delivery).ShouldBeEmpty();
    }

    [Fact]
    public void Without_own_hours_delivery_uses_the_takeout_hours_and_counts_its_own_places()
    {
        var shop = Shop(Week(), maxOrdersPerSlot: 1, delivery: new OrderingDeliverySettings(Zones(), MinLeadMinutes: 0));
        var noonFull = (Func<DateTimeOffset, int>)(at => at == AtTaipei(11, 10, 12) ? 1 : 0);

        var delivery = OrderingSlots.On(shop, new DateOnly(2026, 11, 10), TuesdayNine, noonFull, OrderingChannel.Delivery);

        delivery.Select(slot => slot.Value).ShouldBe(OrderingSlots.On(shop, new DateOnly(2026, 11, 10), TuesdayNine).Select(slot => slot.Value));
        delivery.Single(slot => slot.IsFull).Value.ShouldBe("2026-11-10T12:00");
        OrderingSlots.Find(shop, new DateOnly(2026, 11, 10), "2026-11-10T12:00", TuesdayNine, noonFull, OrderingChannel.Delivery).ShouldBeNull();
    }

    [Fact]
    public void A_shop_that_does_not_deliver_or_is_paused_offers_no_delivery_slot()
    {
        var takeoutOnly = Shop(Week());
        OrderingSlots.Scheduled(takeoutOnly, TuesdayNine, channel: OrderingChannel.Delivery).ShouldBeEmpty();
        OrderingSlots.Asap(takeoutOnly, AtTaipei(11, 10, 12), OrderingChannel.Delivery).ShouldBeNull();

        var paused = Shop(Week(), delivery: new OrderingDeliverySettings(Zones()), deliveryAsap: true);
        paused.SetPaused(true, TuesdayNine);
        OrderingSlots.Scheduled(paused, TuesdayNine, channel: OrderingChannel.Delivery).ShouldBeEmpty();
        OrderingSlots.Asap(paused, AtTaipei(11, 10, 12), OrderingChannel.Delivery).ShouldBeNull();
    }

    [Fact]
    public void ASAP_delivery_adds_the_ride_to_the_band_and_must_arrive_before_the_delivery_hours_close()
    {
        var shop = Shop(
            Week(),
            delivery: new OrderingDeliverySettings(Zones(), Hours([(DayOfWeek.Tuesday, "11:00", "13:30")]), AsapExtraMinutes: 20),
            deliveryAsap: true);

        // Band 15–30 (the default) plus 20.
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 12), OrderingChannel.Delivery).ShouldBe(new AsapEstimate(35, 50));
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 12, 40), OrderingChannel.Delivery).ShouldBe(new AsapEstimate(35, 50));
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 12, 41), OrderingChannel.Delivery).ShouldBeNull();
        // Takeout is open until 14:00 and does not add the ride.
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 12, 41)).ShouldBe(new AsapEstimate(15, 30));
        OrderingSlots.Asap(shop, AtTaipei(11, 10, 17, 30), OrderingChannel.Delivery).ShouldBeNull();

        var scheduledOnly = Shop(Week(), delivery: new OrderingDeliverySettings(Zones()), deliveryAsap: false);
        OrderingSlots.Asap(scheduledOnly, AtTaipei(11, 10, 12), OrderingChannel.Delivery).ShouldBeNull();
    }

    [Fact]
    public void A_delivery_order_totals_its_subtotal_and_fee_and_its_address_is_cleared_with_the_rest()
    {
        var shop = Shop(Week(), delivery: new OrderingDeliverySettings(Zones()));
        var order = Delivery(shop, new OrderingOrderDelivery("xinying", "新營區其他里", 30, "新營區中山路 1 號 3 樓", "請按電鈴"));

        order.Subtotal.ShouldBe(210);
        order.DeliveryFee.ShouldBe(30);
        order.Total.ShouldBe(240);
        order.Delivery.ShouldBe(new OrderingOrderDelivery("xinying", "新營區其他里", 30, "新營區中山路 1 號 3 樓", "請按電鈴"));

        order.ClearPersonalData(AtTaipei(11, 10, 13));

        order.Delivery.ShouldBe(new OrderingOrderDelivery("xinying", "新營區其他里", 30, string.Empty, null));
        order.DeliveryAddress.ShouldBe(string.Empty);
        order.Total.ShouldBe(240);
    }

    [Fact]
    public void A_delivery_order_needs_a_zone_and_an_address_and_a_takeout_one_has_none()
    {
        var shop = Shop(Week(), delivery: new OrderingDeliverySettings(Zones()));

        Should.Throw<ArgumentException>(() => Delivery(shop, null));
        Should.Throw<ArgumentException>(() => Delivery(shop, new OrderingOrderDelivery("near", "近", 30, " ", null)));
        Should.Throw<ArgumentException>(() => Delivery(shop, new OrderingOrderDelivery("near", "近", 30, new string('路', 121), null)));
        Should.Throw<ArgumentException>(() => OrderingOrder.Submit(
            Guid.CreateVersion7(), OrderingServiceType.TakeoutScheduled, new DateOnly(2026, 11, 10), 1, AtTaipei(11, 10, 12), null, "陳小姐",
            "0912345678", null, null, null, Lines(), 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), shop.Hours, 15,
            TuesdayNine, new OrderingOrderDelivery("near", "近", 30, "新營區中山路 1 號", null)));
    }

    [Fact]
    public void Accepting_a_delivery_order_follows_the_scheduled_and_ASAP_rules_with_delivery_words()
    {
        var shop = Shop(Week(), delivery: new OrderingDeliverySettings(Zones()));
        var order = Delivery(shop, new OrderingOrderDelivery("minquan", "新營區・民權里", 0, "民權路 2 號", null));

        order.PromisedAtProblem(AtTaipei(11, 10, 11, 30), TuesdayNine).ShouldBe("預計送達時間不能早於顧客選的時間。");
        order.PromisedAtProblem(AtTaipei(11, 10, 12, 30), TuesdayNine).ShouldBeNull();

        var asap = OrderingOrder.Submit(
            Guid.CreateVersion7(), OrderingServiceType.DeliveryAsap, new DateOnly(2026, 11, 10), 2, default, new AsapEstimate(35, 50),
            "陳小姐", "0912345678", null, null, null, Lines(), 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"),
            shop.HoursFor(OrderingChannel.Delivery), 15, AtTaipei(11, 10, 12),
            new OrderingOrderDelivery("minquan", "新營區・民權里", 0, "民權路 2 號", null));
        asap.PickupAt.ShouldBe(AtTaipei(11, 10, 12, 50));
        asap.PromisedAtProblem(AtTaipei(11, 10, 11), AtTaipei(11, 10, 12)).ShouldBe("預計送達時間不能早於現在。");
    }

    private static OrderingOrder Delivery(OrderingShop shop, OrderingOrderDelivery? delivery) => OrderingOrder.Submit(
        Guid.CreateVersion7(), OrderingServiceType.DeliveryScheduled, new DateOnly(2026, 11, 10), 1, AtTaipei(11, 10, 12), null, "陳小姐",
        "0912345678", null, null, null, Lines(), 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"),
        shop.HoursFor(OrderingChannel.Delivery), 15, TuesdayNine, delivery);

    private static IReadOnlyList<OrderingOrderLine> Lines() =>
        OrderingCart.Price(Example().Menu, [new OrderingLineInput("wonton-soup", 3, null, null)]).Lines;
}
