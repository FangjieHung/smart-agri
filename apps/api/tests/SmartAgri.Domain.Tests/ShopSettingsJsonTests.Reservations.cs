using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// The settings file's <c>reservations</c> block (store-reservations design §3.1 point 2, #389): optional, read with its
/// defaults and written back in full, and refused with each error at its JSON path.
/// </summary>
public partial class ShopSettingsJsonTests
{
    private const string Reservations = "$.reservations";

    [Fact]
    public void Without_a_reservations_block_the_shop_takes_none_and_none_is_written()
    {
        var settings = ShopSettingsSamples.ValidDocument();

        settings.Reservations.ShouldBeNull();
        ShopSettingsJson.Write(settings).ShouldNotContain("reservations");
        ShopSettingsChange.Created(settings).Sections.ShouldBe(["storeName", "timeZone", "weeklyHours", "scenarios"]);
    }

    [Fact]
    public void A_reservations_block_takes_the_defaults_and_is_written_back_in_full()
    {
        var tree = ShopSettingsSamples.ValidTree();
        tree["reservations"] = JsonNode.Parse("""{ "enabled": true, "slug": "wangji", "phone": "02-2345-6789" }""");

        var settings = ShopSettingsSamples.Parse(tree.ToJsonString());

        settings.Reservations.ShouldBe(new ShopReservationSettings(true, "wangji", "02-2345-6789", 30, 120, 30, 60, 20, 60));
        settings.Reservations!.IsOpen.ShouldBeTrue();
        var written = ShopSettingsJson.Write(settings);
        written.ShouldEndWith(
            """
              "reservations": {
                "enabled": true,
                "slug": "wangji",
                "phone": "02-2345-6789",
                "slotMinutes": 30,
                "minLeadMinutes": 120,
                "maxDaysAhead": 30,
                "lastSeatingMinutes": 60,
                "maxPartySize": 20,
                "pendingExpiryMinutes": 60
              }
            }
            """);
        ShopSettingsJson.Write(ShopSettingsSamples.Parse(written)).ShouldBe(written);
        ShopSettingsJson.ReadReservations(ShopSettingsJson.WriteReservations(settings.Reservations)).ShouldBe(settings.Reservations);
        ShopSettingsChange.Created(settings).Sections.ShouldBe(["storeName", "timeZone", "weeklyHours", "scenarios", "reservations"]);
    }

    [Fact]
    public void Every_value_can_be_set_within_its_range()
    {
        var tree = ShopSettingsSamples.ValidTree();
        tree["reservations"] = JsonNode.Parse(
            """
            {
              "enabled": true, "slug": "wang-ji-2", "phone": "+886 2 2345 6789", "slotMinutes": 15, "minLeadMinutes": 10080,
              "maxDaysAhead": 90, "lastSeatingMinutes": 0, "maxPartySize": 100, "pendingExpiryMinutes": 1440
            }
            """);

        ShopSettingsSamples.Parse(tree.ToJsonString()).Reservations.ShouldBe(
            new ShopReservationSettings(true, "wang-ji-2", "+886 2 2345 6789", 15, 10080, 90, 0, 100, 1440));
    }

    [Fact]
    public void A_disabled_block_needs_neither_slug_nor_phone()
    {
        var tree = ShopSettingsSamples.ValidTree();
        tree["reservations"] = JsonNode.Parse("""{ "enabled": false }""");

        var settings = ShopSettingsSamples.Parse(tree.ToJsonString());

        settings.Reservations.ShouldBe(new ShopReservationSettings(false, null, null));
        settings.Reservations!.IsOpen.ShouldBeFalse();
        ShopSettingsJson.Write(settings).ShouldContain("\"reservations\": {\n    \"enabled\": false,\n    \"slotMinutes\": 30,");
    }

    [Fact]
    public void A_change_of_the_reservations_is_its_own_section()
    {
        var before = ShopSettingsSamples.ValidDocument();
        var tree = ShopSettingsSamples.ValidTree();
        tree["reservations"] = JsonNode.Parse("""{ "enabled": false, "slug": "wangji" }""");
        var added = ShopSettingsSamples.Parse(tree.ToJsonString());
        tree["reservations"]!["enabled"] = true;
        tree["reservations"]!["phone"] = "02-2345-6789";
        var enabled = ShopSettingsSamples.Parse(tree.ToJsonString());

        ShopSettingsChange.Between(before, added).Sections.ShouldBe(["reservations"]);
        ShopSettingsChange.Between(added, enabled).Sections.ShouldBe(["reservations"]);
        ShopSettingsChange.Between(enabled, before).Sections.ShouldBe(["reservations"]);
        ShopSettingsChange.Between(enabled, enabled with { }).IsEmpty.ShouldBeTrue();
    }

    private static readonly Dictionary<string, (string Block, string Path, string Message)> InvalidReservations = new()
    {
        ["enabled without a slug"] = ("""{ "enabled": true, "phone": "02-2345-6789" }""", $"{Reservations}.slug", "必須設定訂位網址的代號"),
        ["enabled without a phone"] = ("""{ "enabled": true, "slug": "wangji" }""", $"{Reservations}.phone", "必須設定店家電話"),
        ["no enabled"] = ("""{ "slug": "wangji" }""", $"{Reservations}.enabled", "缺少這個欄位"),
        ["an uppercase slug"] = ("""{ "enabled": false, "slug": "WangJi" }""", $"{Reservations}.slug", "小寫英文字母"),
        ["a short slug"] = ("""{ "enabled": false, "slug": "wj" }""", $"{Reservations}.slug", "3–40 個"),
        ["a slug ending with a dash"] = ("""{ "enabled": false, "slug": "wangji-" }""", $"{Reservations}.slug", "不能以 - 開頭或結尾"),
        ["a phone with letters"] = ("""{ "enabled": false, "phone": "02-ABCD-6789" }""", $"{Reservations}.phone", "店家的電話號碼"),
        ["a long phone"] = ("""{ "enabled": false, "phone": "02-2345-6789-0000-1111" }""", $"{Reservations}.phone", "最多 20 個字"),
        ["a slot of 45 minutes"] = ("""{ "enabled": false, "slotMinutes": 45 }""", $"{Reservations}.slotMinutes", "10、15、20、30、60"),
        ["a slot that is not a whole number"] = ("""{ "enabled": false, "slotMinutes": 30.5 }""", $"{Reservations}.slotMinutes", "必須是整數"),
        ["a slot as text"] = ("""{ "enabled": false, "slotMinutes": "30" }""", $"{Reservations}.slotMinutes", "必須是整數"),
        ["no lead time"] = ("""{ "enabled": false, "minLeadMinutes": 0 }""", $"{Reservations}.minLeadMinutes", "1 到 10080"),
        ["91 days ahead"] = ("""{ "enabled": false, "maxDaysAhead": 91 }""", $"{Reservations}.maxDaysAhead", "1 到 90"),
        ["a negative last seating"] = ("""{ "enabled": false, "lastSeatingMinutes": -1 }""", $"{Reservations}.lastSeatingMinutes", "0 到 720"),
        ["a party of none"] = ("""{ "enabled": false, "maxPartySize": 0 }""", $"{Reservations}.maxPartySize", "1 到 100"),
        ["expiry not before the lead time"] = ("""{ "enabled": false, "minLeadMinutes": 60, "pendingExpiryMinutes": 60 }""",
            $"{Reservations}.pendingExpiryMinutes", "必須小於 minLeadMinutes（目前 60 ≥ 60）"),
        ["expiry after the default lead time"] = ("""{ "enabled": false, "pendingExpiryMinutes": 180 }""",
            $"{Reservations}.pendingExpiryMinutes", "必須小於 minLeadMinutes（目前 180 ≥ 120）"),
        ["an unknown field"] = ("""{ "enabled": false, "seats": 40 }""", $"{Reservations}.seats", "不認得的欄位"),
        ["not an object"] = ("""true""", Reservations, "必須是物件"),
    };

    public static TheoryData<string> InvalidReservationNames => [.. InvalidReservations.Keys];

    [Theory]
    [MemberData(nameof(InvalidReservationNames))]
    public void An_invalid_reservations_block_is_refused_with_the_error_at_its_path(string why)
    {
        var (block, path, message) = InvalidReservations[why];
        var tree = ShopSettingsSamples.ValidTree();
        tree["reservations"] = JsonNode.Parse(block);

        var result = ShopSettingsJson.Parse(tree.ToJsonString());

        result.IsValid.ShouldBeFalse(why);
        result.Errors.ShouldContain(error => error.Path == path && error.Message.Contains(message, StringComparison.Ordinal), $"{why}:\n{string.Join('\n', result.Errors)}");
    }
}
