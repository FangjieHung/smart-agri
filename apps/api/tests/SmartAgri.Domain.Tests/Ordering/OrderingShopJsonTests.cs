using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Domain.Tests.Ordering;

/// <summary>
/// The online-ordering import file (online-ordering Slice O1, #397): the example parses; a broken file lists every error
/// with its JSON path; unknown keys, prices with decimals or below zero, groups whose minimum exceeds their maximum, more
/// than four group levels and repeated ids are refused; the stored menu reads back the same whatever the key order.
/// </summary>
public class OrderingShopJsonTests
{
    [Fact]
    public void The_example_file_is_valid_and_reads_as_written()
    {
        var import = OrderingSamples.Example();

        import.Shop.Slug.ShouldBe("wangji-noodles");
        import.Shop.TimeZoneId.ShouldBe("Asia/Taipei");
        import.Shop.Services.ShouldBe(new OrderingServices(TakeoutScheduled: true, TakeoutAsap: true, Delivery: true, DeliveryAsap: true, DineIn: true));
        (import.Shop.SlotMinutes, import.Shop.DaysAhead, import.Shop.MinLeadMinutes, import.Shop.MaxOrdersPerSlot).ShouldBe((30, 3, 20, (int?)8));
        import.Shop.PendingTimeoutMinutes.ShouldBe(15);
        import.Shop.OrderingHours[DayOfWeek.Monday].ShouldBeEmpty();
        import.Shop.OrderingHours[DayOfWeek.Saturday][1].IsOvernight.ShouldBeTrue();
        import.Shop.ReminderNotice.ShouldNotBeNull().ShouldContain("\n");
        import.Menu.Categories.Select(category => category.Id).ShouldBe(["noodles", "sides", "lunch-special"]);
        import.Menu.Items.Count().ShouldBe(6);
        var beef = import.Menu.FindItem("beef-noodle").ShouldNotBeNull().Item;
        beef.OptionGroups.Select(group => group.Id).ShouldBe(["size", "noodle", "toppings", "combo"]);
        beef.OptionGroups[2].Repeatable.ShouldBeTrue();
        beef.OptionGroups[3].Options[0].OptionGroups.Select(group => group.Id).ShouldBe(["side", "drink"]);
        import.Menu.Categories[2].Availability!.Days.ShouldBe([DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
    }

    [Fact]
    public void Optional_shop_fields_take_their_defaults()
    {
        var tree = OrderingSamples.ExampleTree();
        var shop = tree["shop"]!.AsObject();
        foreach (var field in new[] { "timeZone", "address", "notices", "slotMinutes", "daysAhead", "minLeadMinutes", "maxOrdersPerSlot", "showOrderComment", "showTableware", "pendingTimeoutMinutes" })
        {
            shop.Remove(field);
        }

        var settings = OrderingSamples.Valid(tree.ToJsonString()).Shop;

        (settings.TimeZoneId, settings.Address, settings.ReminderNotice, settings.CheckoutNotice).ShouldBe(("Asia/Taipei", (string?)null, (string?)null, (string?)null));
        (settings.SlotMinutes, settings.DaysAhead, settings.MinLeadMinutes, settings.MaxOrdersPerSlot).ShouldBe((30, 3, 15, (int?)null));
        (settings.ShowOrderComment, settings.ShowTableware).ShouldBe((true, true));
        settings.PendingTimeoutMinutes.ShouldBe(OrderingShopSettings.DefaultPendingTimeoutMinutes);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void The_pending_timeout_is_5_to_120_minutes(int minutes, bool valid)
    {
        var tree = OrderingSamples.ExampleTree();
        tree["shop"]!["pendingTimeoutMinutes"] = minutes;

        var result = OrderingShopJson.Parse(tree.ToJsonString());

        result.IsValid.ShouldBe(valid);
        if (valid)
        {
            result.Import!.Shop.PendingTimeoutMinutes.ShouldBe(minutes);
        }
        else
        {
            result.Errors.Select(error => error.ToString()).ShouldHaveSingleItem()
                .ShouldStartWith($"$.shop.pendingTimeoutMinutes：必須是 5 到 120 的整數（目前 {minutes}）");
        }
    }

    [Fact]
    public void A_broken_file_lists_every_error_with_its_path()
    {
        var tree = OrderingSamples.ExampleTree();
        var shop = tree["shop"]!.AsObject();
        shop["slug"] = "Wang Ji";
        shop["timeZone"] = "Mars/Olympus";
        shop["phone"] = "123";
        shop["owner"] = "王老闆";
        shop["services"] = JsonNode.Parse("""{ "takeoutScheduled": false, "takeoutAsap": false }""");
        shop["orderingHours"]!["tue"]![0]!["close"] = "11:00";
        shop["slotMinutes"] = 25;
        shop["daysAhead"] = 15;
        var categories = tree["menu"]!["categories"]!.AsArray();
        var beef = categories[0]!["items"]![0]!;
        beef["price"] = 150.5;
        beef["imageUrl"] = "http://example.com/beef.jpg";
        beef["optionGroups"]![0]!["min"] = 2;
        beef["optionGroups"]![1]!["options"]![0]!["price"] = -5;
        beef["optionGroups"]![1]!["optionz"] = new JsonArray();
        categories[0]!["items"]![1]!["id"] = "beef-noodle";
        categories[1]!["items"]![0]!["id"] = "Cucumber!";
        categories[2]!["availability"]!["days"] = JsonNode.Parse("""["tue", "tue", "fri"]""");

        var result = OrderingShopJson.Parse(tree.ToJsonString());

        result.IsValid.ShouldBeFalse();
        result.Import.ShouldBeNull();
        var lines = result.Errors.Select(error => error.ToString()).ToList();
        string[] expected =
        [
            "$.shop.slug：必須是 3–40 個小寫英文字母",
            "$.shop.timeZone：不認得的時區「Mars/Olympus」",
            "$.shop.phone：必須是店家的電話號碼",
            "$.shop.owner：不認得的欄位",
            "$.shop.services：至少要開放一種取餐方式",
            "$.shop.orderingHours.tue[0]：開店與打烊時間相同",
            "$.shop.slotMinutes：必須是 10、15、20、30、60 其中之一",
            "$.shop.daysAhead：必須是 1 到 14 的整數（目前 15）",
            "$.menu.categories[0].items[0].price：必須是整數（新台幣元，不能有小數）",
            "$.menu.categories[0].items[0].imageUrl：必須是完整的 https:// 網址",
            "$.menu.categories[0].items[0].optionGroups[0]：min（2）不能大於 max（1）",
            "$.menu.categories[0].items[0].optionGroups[1].optionz：不認得的欄位",
            "$.menu.categories[0].items[0].optionGroups[1].options[0].price：不能是負數（目前 -5）",
            "$.menu.categories[0].items[1].id：「beef-noodle」和 $.menu.categories[0].items[0] 的品項 id 重複",
            "$.menu.categories[1].items[0].id：必須是 1–40 個小寫英文字母",
            "$.menu.categories[2].availability.days[1]：「tue」重複",
        ];
        foreach (var line in expected)
        {
            lines.ShouldContain(actual => actual.StartsWith(line, StringComparison.Ordinal), $"missing: {line}\nall:\n{string.Join('\n', lines)}");
        }

        lines.Count.ShouldBe(expected.Length, string.Join('\n', lines));
    }

    [Fact]
    public void Five_group_levels_are_refused_and_four_are_fine()
    {
        var four = FourLevels(fifth: false);
        var five = FourLevels(fifth: true);

        OrderingShopJson.Parse(OrderingSamples.WithMenu(four)).Errors.ShouldBeEmpty();
        var error = OrderingShopJson.Parse(OrderingSamples.WithMenu(five)).Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe(
            "$.menu.categories[0].items[0].optionGroups[0].options[0].optionGroups[0].options[0].optionGroups[0].options[0]"
            + ".optionGroups[0].options[0].optionGroups");
        error.Message.ShouldBe("選項群組最多 4 層，這裡是第 5 層。");
    }

    [Fact]
    public void Group_and_option_ids_are_unique_within_their_parent_only()
    {
        var menu = """
            { "categories": [{ "id": "c", "name": "分類", "items": [
              { "id": "a", "name": "甲", "price": 10, "optionGroups": [
                { "id": "g", "name": "群組", "min": 0, "max": 1, "options": [
                  { "id": "o", "name": "選項", "optionGroups": [{ "id": "g", "name": "同名群組", "min": 0, "max": 1, "options": [{ "id": "o", "name": "同名選項" }] }] },
                  { "id": "o", "name": "重複選項" }
                ] },
                { "id": "g", "name": "重複群組", "min": 0, "max": 1, "options": [{ "id": "x", "name": "叉" }] }
              ] },
              { "id": "b", "name": "乙", "price": 10, "optionGroups": [{ "id": "g", "name": "別的品項的群組", "min": 0, "max": 1, "options": [{ "id": "o", "name": "選項" }] }] }
            ] }] }
            """;

        var errors = OrderingShopJson.Parse(OrderingSamples.WithMenu(menu)).Errors.Select(error => error.Path).ToList();

        errors.ShouldBe(
        [
            "$.menu.categories[0].items[0].optionGroups[0].options[1].id",
            "$.menu.categories[0].items[0].optionGroups[1].id",
        ]);
    }

    [Fact]
    public void Missing_and_mistyped_required_fields_are_errors()
    {
        var result = OrderingShopJson.Parse("""{ "shop": { "slug": 3 }, "menu": { "categories": [{ "id": "c", "items": [{ "id": "a", "name": "甲" }] }] } }""");

        result.Errors.Select(error => error.ToString()).ShouldBe(
        [
            "$.shop.slug：必須是字串。",
            "$.shop.name：缺少這個欄位。",
            "$.shop.phone：缺少這個欄位。",
            "$.shop.services：缺少這個欄位，例如 { \"takeoutScheduled\": true, \"takeoutAsap\": true }。",
            "$.shop.orderingHours：缺少這個欄位：每週可點餐（取餐）的時間，格式同店家設定的 weeklyHours。",
            "$.menu.categories[0].name：缺少這個欄位。",
            "$.menu.categories[0].items[0].price：缺少這個欄位。",
        ]);
        OrderingShopJson.Parse("[]").Errors.ShouldHaveSingleItem().ToString().ShouldBe("$：必須是物件（{ … }）。");
        OrderingShopJson.Parse("{ nope").Errors.ShouldHaveSingleItem().Message.ShouldStartWith("不是有效的 JSON");
    }

    [Fact]
    public void A_non_repeatable_groups_maximum_cannot_exceed_its_options_but_a_repeatable_ones_can()
    {
        static string Menu(bool repeatable) => $$"""
            { "categories": [{ "id": "c", "name": "分類", "items": [{ "id": "a", "name": "甲", "price": 10, "optionGroups": [
              { "id": "g", "name": "加料", "min": 0, "max": 5, "repeatable": {{(repeatable ? "true" : "false")}}, "options": [{ "id": "egg", "name": "蛋", "price": 15 }] }
            ] }] }] }
            """;

        OrderingShopJson.Parse(OrderingSamples.WithMenu(Menu(repeatable: true))).Errors.ShouldBeEmpty();
        OrderingShopJson.Parse(OrderingSamples.WithMenu(Menu(repeatable: false))).Errors.ShouldHaveSingleItem().Path
            .ShouldBe("$.menu.categories[0].items[0].optionGroups[0].max");
    }

    [Fact]
    public void A_stored_menu_reads_back_the_same_whatever_the_key_order_and_spacing()
    {
        var menu = OrderingSamples.Example().Menu;
        var written = OrderingShopJson.WriteMenu(menu);

        OrderingShopJson.WriteMenu(OrderingShopJson.ReadMenu(written)).ShouldBe(written);

        // PostgreSQL's jsonb puts shorter keys first and adds spaces: still the same menu.
        var reordered = JsonNode.Parse(written)!;
        var shuffled = Reverse(reordered).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        shuffled.ShouldNotBe(written);
        OrderingShopJson.WriteMenu(OrderingShopJson.ReadMenu(shuffled)).ShouldBe(written);
        Should.Throw<FormatException>(() => OrderingShopJson.ReadMenu("""{ "categories": [{ "id": 1 }] }"""));
    }

    [Fact]
    public void Days_are_written_monday_first_and_defaults_are_left_out()
    {
        var menu = OrderingSamples.Menu("""
            { "categories": [{ "id": "c", "name": "分類", "availability": { "days": ["sun", "mon"] }, "items": [
              { "id": "a", "name": "甲", "price": 10, "optionGroups": [{ "id": "g", "name": "群組", "min": 0, "max": 1, "repeatable": false, "options": [{ "id": "o", "name": "選項", "price": 0, "optionGroups": [] }] }] }
            ] }] }
            """);

        OrderingShopJson.WriteMenu(menu).ShouldBe(
            """{"categories":[{"id":"c","name":"分類","availability":{"days":["mon","sun"]},"items":[{"id":"a","name":"甲","price":10,"optionGroups":[{"id":"g","name":"群組","min":0,"max":1,"options":[{"id":"o","name":"選項","price":0}]}]}]}]}""");
    }

    private static JsonNode Reverse(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Reverse().Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)Reverse(pair.Value!.DeepClone())))),
        JsonArray array => new JsonArray([.. array.Select(item => (JsonNode?)Reverse(item!.DeepClone()))]),
        _ => node.DeepClone(),
    };

    /// <summary>A menu whose one item has four nested group levels, and a fifth when <paramref name="fifth"/>.</summary>
    internal static string FourLevels(bool fifth)
    {
        var innermost = fifth
            ? """{ "id": "soy", "name": "醬油", "price": 2, "optionGroups": [{ "id": "extra", "name": "第五層", "min": 0, "max": 1, "options": [{ "id": "x", "name": "叉" }] }] }"""
            : """{ "id": "soy", "name": "醬油", "price": 2 }""";
        return $$"""
            { "categories": [{ "id": "sets", "name": "套餐", "items": [{ "id": "set", "name": "套餐", "price": 100, "optionGroups": [
              { "id": "main", "name": "主食", "min": 1, "max": 1, "options": [
                { "id": "rice", "name": "飯", "price": 10, "optionGroups": [
                  { "id": "topping", "name": "加料", "min": 0, "max": 3, "repeatable": true, "options": [
                    { "id": "egg", "name": "蛋", "price": 15, "optionGroups": [
                      { "id": "egg-style", "name": "蛋的做法", "min": 1, "max": 1, "options": [
                        { "id": "fried", "name": "荷包蛋", "price": 5, "optionGroups": [
                          { "id": "sauce", "name": "醬料", "min": 0, "max": 1, "options": [{{innermost}}] }
                        ] },
                        { "id": "boiled", "name": "水煮蛋" }
                      ] }
                    ] },
                    { "id": "tofu", "name": "豆腐", "price": 10 }
                  ] }
                ] },
                { "id": "noodles", "name": "麵", "price": 0 }
              ] }
            ] }] }] }
            """;
    }
}
