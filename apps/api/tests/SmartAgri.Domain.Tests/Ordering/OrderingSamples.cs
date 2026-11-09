using System.Globalization;
using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Domain.Tests.Ordering;

/// <summary>Ordering import files, menus and shops for the tests (#397).</summary>
internal static class OrderingSamples
{
    public static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    /// <summary>The example file <c>deploy/ordering-shop.example.json</c>.</summary>
    public static string ExampleFile() =>
        File.ReadAllText(ShopSettingsSamples.RepositoryFile("deploy", "ordering-shop.example.json"));

    /// <summary>The example file as a tree to change.</summary>
    public static JsonObject ExampleTree() => JsonNode.Parse(ExampleFile())!.AsObject();

    /// <summary>The example file's import.</summary>
    public static OrderingImport Example() => Valid(ExampleFile());

    public static OrderingImport Valid(string json)
    {
        var result = OrderingShopJson.Parse(json);
        result.Errors.ShouldBeEmpty();
        return result.Import.ShouldNotBeNull();
    }

    /// <summary>A file with the example's shop and <paramref name="menuJson"/> as its menu.</summary>
    public static string WithMenu(string menuJson)
    {
        var tree = ExampleTree();
        tree["menu"] = JsonNode.Parse(menuJson);
        return tree.ToJsonString();
    }

    /// <summary>A valid menu: <paramref name="menuJson"/> parsed as the example's menu.</summary>
    public static MenuDocument Menu(string menuJson) => Valid(WithMenu(menuJson)).Menu;

    /// <summary>A shop with the given ordering periods (<c>(day, "HH:mm", "HH:mm")</c>) and settings.</summary>
    public static OrderingShop Shop(
        IEnumerable<(DayOfWeek Day, string Open, string Close)> periods,
        string timeZone = "Asia/Taipei",
        int slotMinutes = 30,
        int daysAhead = 3,
        int minLeadMinutes = 0,
        int? maxOrdersPerSlot = null,
        bool scheduled = true,
        bool asap = true,
        OrderingDeliverySettings? delivery = null,
        bool deliveryAsap = false)
    {
        var settings = new OrderingShopSettings(
            "test-shop", "測試麵店", timeZone, "02-2345-6789", null, null, null,
            new OrderingServices(scheduled, asap, delivery is not null, deliveryAsap), Hours(periods),
            slotMinutes, daysAhead, minLeadMinutes, maxOrdersPerSlot, Delivery: delivery);
        return OrderingShop.Create(Guid.CreateVersion7(), settings, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>Tuesday–Friday 11:00–14:00 and 17:00–21:00, Saturday 17:00–01:00 (past midnight), Sunday 11:00–14:00,
    /// closed Mondays.</summary>
    public static IEnumerable<(DayOfWeek, string, string)> Week()
    {
        foreach (var day in new[] { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
        {
            yield return (day, "11:00", "14:00");
            yield return (day, "17:00", "21:00");
        }

        yield return (DayOfWeek.Saturday, "17:00", "01:00");
        yield return (DayOfWeek.Sunday, "11:00", "14:00");
    }

    /// <summary>Weekly hours of the given periods.</summary>
    public static WeeklyHours Hours(IEnumerable<(DayOfWeek Day, string Open, string Close)> periods) =>
        new(periods
            .GroupBy(period => period.Day)
            .ToDictionary(
                day => day.Key,
                day => (IReadOnlyList<BusinessPeriod>)day.Select(period => new BusinessPeriod(Time(period.Open), Time(period.Close))).ToList()));

    /// <summary>Three zones: 民權里 free from NT$150, 新營 NT$30 from NT$200, 5 公里內 NT$60 from NT$400 (#401).</summary>
    public static IReadOnlyList<DeliveryZone> Zones() =>
    [
        new DeliveryZone("minquan", "新營區・民權里", 0, 150),
        new DeliveryZone("xinying", "新營區其他里", 30, 200),
        new DeliveryZone("within-5km", "5 公里內", 60, 400),
    ];

    private static TimeOnly Time(string text) => TimeOnly.ParseExact(text, "HH:mm", CultureInfo.InvariantCulture);

    /// <summary>An instant on the Taipei clock.</summary>
    public static DateTimeOffset AtTaipei(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, Taipei);
}
