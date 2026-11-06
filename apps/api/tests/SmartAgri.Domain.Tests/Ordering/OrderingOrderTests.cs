using Shouldly;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Domain.Tests.Ordering;

/// <summary>
/// A whole cart against the menu and an order made of it (online-ordering Slice O2, #398): every line priced with its index
/// in the errors, items that cannot be had at the pickup time (停售, 今日售完 until it is back, a category outside its
/// window), the order's number, pickup, tokens and submission keys.
/// </summary>
public class OrderingOrderTests
{
    private static readonly MenuDocument Example = OrderingSamples.Example().Menu;

    private static readonly Guid Organization = Guid.CreateVersion7();

    private static readonly OrderingShop Shop = OrderingSamples.Shop(OrderingSamples.Week());

    private static readonly OrderingLineInput Beef = new(
        "beef-noodle",
        1,
        [
            new OptionSelection("size", "large"),
            new OptionSelection("noodle", "wide"),
            new OptionSelection("toppings", "egg", 2),
            new OptionSelection("combo", "set", 1, [new OptionSelection("side", "cucumber"), new OptionSelection("drink", "winter-melon")]),
        ],
        "麵硬一點");

    private static readonly OrderingLineInput Wonton = new("wonton-soup", 3, null, null);

    [Fact]
    public void A_cart_is_priced_line_by_line_keeping_each_comment()
    {
        var result = OrderingCart.Price(Example, [Beef, Wonton]);

        result.IsValid.ShouldBeTrue();
        result.Lines.Select(line => (line.ItemId, line.UnitPrice, line.LineTotal, line.Comment)).ShouldBe(
        [
            ("beef-noodle", 150 + 20 + 0 + (2 * 15) + (60 + 0 + 10), 270, "麵硬一點"),
            ("wonton-soup", 70, 210, (string?)null),
        ]);
        result.Subtotal.ShouldBe(480);
        result.Lines[0].Options[^1].Options.Select(option => option.OptionName).ShouldBe(["涼拌小黃瓜", "冬瓜檸檬"]);
    }

    [Fact]
    public void Every_wrong_line_is_reported_with_its_index_and_path()
    {
        var result = OrderingCart.Price(
            Example,
            [
                Beef,
                new OrderingLineInput("zhajiang", 1, [], null),
                new OrderingLineInput("no-such-item", 1, null, null),
                new OrderingLineInput("wonton-soup", 0, null, null),
                new OrderingLineInput("beef-noodle", 1, [.. Beef.Selections!.Take(3), new OptionSelection("combo", "set")], null),
            ]);

        result.IsValid.ShouldBeFalse();
        result.Lines.ShouldBeEmpty();
        result.Errors.ShouldBe(
        [
            new OrderingLineError(1, "too-few", "spicy"),
            new OrderingLineError(2, "unknown-item", string.Empty),
            new OrderingLineError(3, "invalid-quantity", string.Empty),
            new OrderingLineError(4, "too-few", "combo.set.side"),
            new OrderingLineError(4, "too-few", "combo.set.drink"),
        ]);
    }

    [Fact]
    public void Items_that_cannot_be_had_at_the_pickup_time_are_named_by_line()
    {
        var tuesdayNoon = OrderingSamples.AtTaipei(11, 10, 12);
        var states = new[]
        {
            OrderingItemState.SoldOutToday(Organization, "zhajiang", Shop.Hours, tuesdayNoon),
            OrderingItemState.Unavailable(Organization, "cucumber", tuesdayNoon),
        };
        var cart = OrderingCart.Price(
            Example,
            [
                new OrderingLineInput("zhajiang", 1, [new OptionSelection("spicy", "mild")], null),
                new OrderingLineInput("cucumber", 1, null, null),
                new OrderingLineInput("lunch-set", 1, [new OptionSelection("drink", "green-tea")], null),
                Wonton,
            ]).Lines;
        var zone = Shop.Hours.Zone;

        OrderingCart.Unobtainable(Example, states, cart, OrderingSamples.AtTaipei(11, 10, 13), zone).ShouldBe(
        [
            new OrderingLineError(0, OrderingCart.SoldOutToday, string.Empty),
            new OrderingLineError(1, OrderingCart.Unavailable, string.Empty),
        ]);

        // Sold out until Wednesday's opening; the lunch set only Tuesday–Friday 11:00–14:00.
        OrderingCart.Unobtainable(Example, states, cart, OrderingSamples.AtTaipei(11, 11, 11, 30), zone)
            .ShouldBe([new OrderingLineError(1, OrderingCart.Unavailable, string.Empty)]);
        OrderingCart.Unobtainable(Example, states, cart, OrderingSamples.AtTaipei(11, 11, 18), zone).ShouldBe(
        [
            new OrderingLineError(1, OrderingCart.Unavailable, string.Empty),
            new OrderingLineError(2, OrderingCart.CategoryUnavailable, string.Empty),
        ]);
    }

    [Fact]
    public void A_scheduled_order_is_picked_up_at_its_time_and_an_asap_one_at_the_estimates_end()
    {
        var now = OrderingSamples.AtTaipei(11, 10, 12, 3);
        var lines = OrderingCart.Price(Example, [Wonton]).Lines;
        var key = Guid.NewGuid().ToString("D");

        var scheduled = OrderingOrder.Submit(
            Organization, OrderingServiceType.TakeoutScheduled, new DateOnly(2026, 11, 10), 7, OrderingSamples.AtTaipei(11, 10, 12, 30), null,
            "陳小姐", "0912345678", null, null, null, lines, 1, OrderingOrder.NewStatusToken(), key, now);
        var asap = OrderingOrder.Submit(
            Organization, OrderingServiceType.TakeoutAsap, new DateOnly(2026, 11, 10), 8, default, new AsapEstimate(15, 30),
            "陳小姐", "0912345678", "chen@example.com", "不要辣", true, lines, 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), now);

        (scheduled.Status, scheduled.Version, scheduled.Number, scheduled.Total, scheduled.CanCancel).ShouldBe((OrderingOrderStatus.Pending, 1, 7, 210, true));
        scheduled.PickupAt.ShouldBe(OrderingSamples.AtTaipei(11, 10, 12, 30));
        scheduled.PickupAt.Offset.ShouldBe(TimeSpan.Zero, "Npgsql writes timestamptz only from UTC");
        (scheduled.AsapMinMinutes, scheduled.AsapMaxMinutes).ShouldBe((null, null));
        asap.PickupAt.ShouldBe(now.AddMinutes(30));
        (asap.AsapMinMinutes, asap.AsapMaxMinutes).ShouldBe((15, 30));
        Should.Throw<ArgumentException>(() => OrderingOrder.Submit(
            Organization, OrderingServiceType.TakeoutAsap, new DateOnly(2026, 11, 10), 9, now, null,
            "陳小姐", "0912345678", null, null, null, lines, 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), now));
        Should.Throw<ArgumentException>(() => OrderingOrder.Submit(
            Organization, OrderingServiceType.TakeoutScheduled, new DateOnly(2026, 11, 10), 9, now, null,
            "陳小姐", "0912-345-678", null, null, null, lines, 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), now));
    }

    [Fact]
    public void Numbers_tokens_and_submission_keys()
    {
        OrderingOrder.FormatNumber(1).ShouldBe("#001");
        OrderingOrder.FormatNumber(23).ShouldBe("#023");
        OrderingOrder.FormatNumber(1234).ShouldBe("#1234");

        var token = OrderingOrder.NewStatusToken();
        OrderingOrder.IsStatusToken(token).ShouldBeTrue();
        OrderingOrder.HashStatusToken(token).Length.ShouldBe(OrderingOrder.StatusTokenHashLength);
        OrderingOrder.HashStatusToken(token).ShouldNotBe(OrderingOrder.HashStatusToken(OrderingOrder.NewStatusToken()));
        OrderingOrder.IsStatusToken(token[..^1]).ShouldBeFalse();
        OrderingOrder.IsStatusToken(token[..^1] + "=").ShouldBeFalse();

        OrderingOrder.NormalizeSubmissionKey("3F2504E0-4F89-11D3-9A0C-0305E82C3301").ShouldBe("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        OrderingOrder.NormalizeSubmissionKey("3f2504e04f8911d39a0c0305e82c3301").ShouldBeNull("only the D format");
        OrderingOrder.NormalizeSubmissionKey(Guid.Empty.ToString("D")).ShouldBeNull();
        OrderingOrder.NormalizeSubmissionKey(null).ShouldBeNull();
    }

    [Fact]
    public void An_asap_order_in_the_small_hours_counts_on_the_day_its_period_opened()
    {
        // Saturday 17:00–01:00: Sunday 00:20 is Saturday's.
        OrderingOrder.AsapBusinessDate(Shop.Hours, OrderingSamples.AtTaipei(11, 15, 0, 20)).ShouldBe(new DateOnly(2026, 11, 14));
        OrderingOrder.AsapBusinessDate(Shop.Hours, OrderingSamples.AtTaipei(11, 15, 12)).ShouldBe(new DateOnly(2026, 11, 15));
    }

    [Fact]
    public void Contact_details_have_their_limits()
    {
        OrderingContact.NameProblem(new string('陳', 40)).ShouldBeNull();
        OrderingContact.NameProblem(new string('陳', 41)).ShouldBe("姓名最多 40 個字。");
        OrderingContact.NameProblem(" ").ShouldBe("請填寫姓名。");
        OrderingContact.LineCommentProblem(new string('辣', 100)).ShouldBeNull();
        OrderingContact.LineCommentProblem(new string('辣', 101)).ShouldBe("品項備註最多 100 個字（目前 101 個）。");
        OrderingContact.OrderCommentProblem("第一行\n第二行").ShouldBeNull();
        OrderingContact.OrderCommentProblem("a\u0007").ShouldBe("訂單備註不能有特殊字元。");
    }
}
