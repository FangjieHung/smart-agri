using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Ordering;
using SmartAgri.Api.Organizations;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ordering;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Ordering;

/// <summary>
/// 外送 (online-ordering design D10, Slice O5, #401) through the real endpoints and PostgreSQL: the shop's zones and delivery
/// times; a delivery order charged the zone's fee by the server whatever the client sends; the zone's minimum, an unknown
/// zone, a missing address and a shop that does not deliver refused; delivery and takeout slot places counted apart; the ASAP
/// delivery estimate; the staff's view; the Email naming the zone but never the address; and the address cleared after 90
/// days.
/// </summary>
/// <remarks>The clock stands at Tuesday 2026-11-17 12:00 in Taipei. Takeout every day 11:00–14:00 and 17:00–21:00 (lead 20
/// minutes); delivery as the example file: Tuesday 11:00–13:30 and 17:00–20:30, lead 45 minutes, ASAP the band 15–30 plus 20;
/// zones 民權里 (free from NT$150), 新營區其他里 (NT$30 from NT$200), 5 公里內 (NT$60 from NT$400).</remarks>
[Trait("Category", TestCategories.Docker)]
public sealed class OrderingDeliveryTests : IClassFixture<OrderingFollowUpHostFixture>
{
    private const string Password = "Staff-Password-1!";

    private const string Address = "台南市新營區中山路 1 號 3 樓";

    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    private static readonly DateTimeOffset Noon = new(2026, 11, 17, 12, 0, 0, Taipei);

    private static int _phones;

    private readonly OrderingFollowUpHostFixture _host;
    private readonly HttpClient _client;

    public OrderingDeliveryTests(OrderingFollowUpHostFixture host)
    {
        _host = host;
        _host.Clock.Advance(Noon - _host.Clock.GetUtcNow());
        _client = host.Factory.CreateClient();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_shop_and_its_slots_tell_the_zones_and_the_delivery_times()
    {
        var (_, slug) = await CreateShopAsync();

        var info = await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}", CancellationToken));
        var slots = await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/slots", CancellationToken));

        info["services"]!.ToJsonString().ShouldBe("""{"takeoutScheduled":true,"takeoutAsap":true,"delivery":true,"deliveryAsap":true}""");
        var delivery = info["delivery"]!;
        ShouldBeJson(
            delivery["zones"],
            """[{"id":"minquan","name":"新營區・民權里","fee":0,"minimumSubtotal":150},{"id":"xinying","name":"新營區其他里","fee":30,"minimumSubtotal":200},"""
            + """{"id":"within-5km","name":"5 公里內（鹽水、柳營）","fee":60,"minimumSubtotal":400}]""");
        delivery["asap"]!.ToJsonString().ShouldBe("""{"minMinutes":35,"maxMinutes":50}""");
        delivery["minLeadMinutes"]!.GetValue<int>().ShouldBe(45);
        delivery["note"]!.GetValue<string>().ShouldStartWith("外送由店員騎車送達");
        delivery["open"]!["currentPeriodCloses"]!.GetValue<string>().ShouldBe("2026-11-17T13:30:00+08:00");
        delivery["orderingHours"]![1]!["periods"]!.ToJsonString().ShouldBe("""[{"open":"11:00","close":"13:30"},{"open":"17:00","close":"20:30"}]""");
        (delivery["addressMaxLength"]!.GetValue<int>(), delivery["noteMaxLength"]!.GetValue<int>()).ShouldBe((120, 100));

        slots["dates"]![0]!["slots"]![0]!["time"]!.GetValue<string>().ShouldBe("12:30", "takeout: 20 minutes ahead");
        slots["deliveryDates"]![0]!["date"]!.GetValue<string>().ShouldBe("2026-11-17");
        slots["deliveryDates"]![0]!["slots"]!.AsArray().Select(slot => slot!["time"]!.GetValue<string>()).ShouldBe(
            ["13:00", "13:30", "17:00", "17:30", "18:00", "18:30", "19:00", "19:30", "20:00", "20:30"]);
    }

    [Fact]
    public async Task A_delivery_order_is_charged_the_zones_fee_by_the_server_whatever_the_client_sends()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var body = Order(Scheduled("2026-11-17T13:00"), "xinying", [Beef()], note: "請按電鈴");
        body["deliveryFee"] = 0;
        body["total"] = 1;
        body["zones"] = new JsonArray(new JsonObject { ["id"] = "xinying", ["fee"] = 0 });

        var submitted = await JsonAsync(await PostAsync(slug, body), HttpStatusCode.Created);

        var order = submitted["order"]!;
        (order["serviceType"]!.GetValue<string>(), order["subtotal"]!.GetValue<int>(), order["deliveryFee"]!.GetValue<int>(), order["total"]!.GetValue<int>())
            .ShouldBe(("delivery-scheduled", 270, 30, 300));
        ShouldBeJson(order["delivery"], $$"""{"zoneId":"xinying","zoneName":"新營區其他里","fee":30,"address":"{{Address}}","note":"請按電鈴"}""");
        order["pickupAt"]!.GetValue<string>().ShouldStartWith("2026-11-17T05:00:00");
        var token = submitted["token"]!.GetValue<string>();
        var current = await JsonAsync(await SendWithTokenAsync(HttpMethod.Get, $"/api/v1/public/ordering/{slug}/orders/current", token));
        current["delivery"]!["address"]!.GetValue<string>().ShouldBe(Address);

        var stored = (await OrdersAsync(shop)).ShouldHaveSingleItem();
        (stored.DeliveryZoneId, stored.DeliveryZoneName, stored.DeliveryFee, stored.Total, stored.DeliveryAddress, stored.DeliveryNote)
            .ShouldBe(("xinying", "新營區其他里", 30, 300, Address, "請按電鈴"));
        var staffView = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/ordering/orders/{stored.Id}", staff));
        staffView["delivery"]!["address"]!.GetValue<string>().ShouldBe(Address);
        (staffView["deliveryFee"]!.GetValue<int>(), staffView["total"]!.GetValue<int>()).ShouldBe((30, 300));
        var summary = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff));
        summary["services"]!["delivery"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Below_the_zones_minimum_the_order_is_refused_with_the_shortfall()
    {
        var (shop, slug) = await CreateShopAsync();

        var problem = await ProblemAsync(
            await PostAsync(slug, Order(Scheduled("2026-11-17T13:00"), "xinying", [Wonton()])), HttpStatusCode.UnprocessableEntity, "below-minimum");

        (problem["minimumSubtotal"]!.GetValue<int>(), problem["shortfall"]!.GetValue<int>()).ShouldBe((200, 130));
        problem["message"]!.GetValue<string>().ShouldBe("外送到「新營區其他里」最低消費 NT$200，還差 NT$130。");
        (await OrdersAsync(shop)).ShouldBeEmpty();

        // Two bowls (NT$140) are still short of the free zone's NT$150; three reach it.
        (await ProblemAsync(
            await PostAsync(slug, Order(Scheduled("2026-11-17T13:00"), "minquan", [Wonton(2)])), HttpStatusCode.UnprocessableEntity, "below-minimum"))
            ["shortfall"]!.GetValue<int>().ShouldBe(10);
        (await PostAsync(slug, Order(Scheduled("2026-11-17T13:00"), "minquan", [Wonton(3)]))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await OrdersAsync(shop)).Single().Total.ShouldBe(210);
    }

    [Fact]
    public async Task An_unknown_zone_a_missing_address_and_a_shop_that_does_not_deliver_are_refused()
    {
        var (shop, slug) = await CreateShopAsync();

        await ProblemAsync(
            await PostAsync(slug, Order(Scheduled("2026-11-17T13:00"), "the-moon", [Beef()])), HttpStatusCode.UnprocessableEntity, "unknown-zone");
        var missing = Order(Scheduled("2026-11-17T13:00"), null, [Beef()], address: "  ");
        var invalid = await ProblemAsync(await PostAsync(slug, missing), HttpStatusCode.UnprocessableEntity, "invalid-order");
        invalid["errors"]!.AsObject().Select(error => error.Key).Order(StringComparer.Ordinal).ShouldBe(["deliveryAddress", "deliveryZoneId"]);
        var tooLong = await ProblemAsync(
            await PostAsync(slug, Order(Scheduled("2026-11-17T13:00"), "xinying", [Beef()], address: new string('路', 121), note: new string('鈴', 101))),
            HttpStatusCode.UnprocessableEntity,
            "invalid-order");
        tooLong["errors"]!.AsObject().Select(error => error.Key).Order(StringComparer.Ordinal).ShouldBe(["deliveryAddress", "deliveryNote"]);
        await ProblemAsync(
            await PostAsync(slug, Order(Scheduled("2026-11-17T12:30"), "xinying", [Beef()])), HttpStatusCode.Conflict, "slot-unavailable");

        await ImportAsync(shop, slug, tree => tree["shop"]!["services"] = JsonNode.Parse(
            """{ "takeoutScheduled": true, "takeoutAsap": true, "delivery": true, "deliveryAsap": false }"""));
        await ProblemAsync(await PostAsync(slug, Order(Asap(), "xinying", [Beef()])), HttpStatusCode.Conflict, "service-unavailable");
        (await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}", CancellationToken)))["delivery"]!["asap"].ShouldBeNull();

        await ImportAsync(shop, slug, tree => tree["shop"]!["services"] = JsonNode.Parse("""{ "takeoutScheduled": true, "takeoutAsap": true }"""));
        var refused = await ProblemAsync(
            await PostAsync(slug, Order(Scheduled("2026-11-17T13:00"), "xinying", [Beef()])), HttpStatusCode.Conflict, "service-unavailable");
        refused["message"]!.GetValue<string>().ShouldBe("店家目前不提供外送。");
        var info = await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}", CancellationToken));
        (info["services"]!["delivery"]!.GetValue<bool>(), info["delivery"]).ShouldBe((false, null));
        (await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/slots", CancellationToken)))["deliveryDates"]!.AsArray().ShouldBeEmpty();
        (await OrdersAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delivery_and_takeout_count_their_slot_places_apart()
    {
        var (shop, slug) = await CreateShopAsync(maxOrdersPerSlot: 1);

        (await PostAsync(slug, Takeout("2026-11-17T13:00"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostAsync(slug, Order(Scheduled("2026-11-17T13:00"), "xinying", [Beef()]))).StatusCode.ShouldBe(HttpStatusCode.Created);
        await ProblemAsync(await PostAsync(slug, Order(Scheduled("2026-11-17T13:00"), "minquan", [Beef()])), HttpStatusCode.Conflict, "slot-full");
        await ProblemAsync(await PostAsync(slug, Takeout("2026-11-17T13:00")), HttpStatusCode.Conflict, "slot-full");

        var slots = await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/slots", CancellationToken));
        Full(slots["dates"]!).ShouldBe(["13:00"]);
        Full(slots["deliveryDates"]!).ShouldBe(["13:00"]);
        (await PostAsync(slug, Order(Scheduled("2026-11-17T13:30"), "minquan", [Beef()]))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await OrdersAsync(shop)).Select(order => order.Number).ShouldBe([1, 2, 3], "one day's numbers for both");
    }

    [Fact]
    public async Task An_ASAP_delivery_is_estimated_with_the_ride()
    {
        var (shop, slug) = await CreateShopAsync();

        var order = (await JsonAsync(await PostAsync(slug, Order(Asap(), "within-5km", [Beef(), Beef()])), HttpStatusCode.Created))["order"]!;

        (order["serviceType"]!.GetValue<string>(), order["total"]!.GetValue<int>()).ShouldBe(("delivery-asap", 540 + 60));
        order["estimate"]!.ToJsonString().ShouldBe("""{"minMinutes":35,"maxMinutes":50}""");
        var stored = (await OrdersAsync(shop)).Single();
        stored.PickupAt.ShouldBe(stored.CreatedAt.AddMinutes(50));
    }

    [Fact]
    public async Task The_Email_names_the_zone_never_the_address_and_after_90_days_the_address_is_cleared()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var to = $"guest-{Guid.NewGuid():N}@example.com";
        var submitted = await JsonAsync(
            await PostAsync(slug, Order(Scheduled("2026-11-17T13:00"), "xinying", [Beef()], email: to, note: "請按電鈴")), HttpStatusCode.Created);
        var id = (await OrdersAsync(shop)).Single().Id;

        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/accept", staff, new { version = 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        var email = _host.Smtp.To(to).ShouldHaveSingleItem();
        email.Body.ShouldStartWith("王記麵店已接受您的外送訂單 #001，預計 11/17（二） 13:00 送達。餐點送達時付款（貨到付款）。");
        email.Body.ShouldContain("外送：預計送達 11/17（二） 13:00\n外送區域：新營區其他里\n付款：貨到付款\n");
        email.Body.ShouldContain("小計：NT$270\n運費：NT$30\n合計：NT$300\n");
        email.Body.ShouldNotContain("中山路");
        email.Body.ShouldNotContain("請按電鈴");
        var ready = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/ready", staff, new { version = 2 }));
        ready["status"]!.GetValue<string>().ShouldBe("ready", "已出發 is the ready status of a delivery order");

        var promised = new DateTimeOffset(2026, 11, 17, 13, 0, 0, Taipei);
        (await ClearAsync(shop, promised.AddDays(90))).ShouldBe(1);

        var cleared = (await OrdersAsync(shop)).Single();
        (cleared.DeliveryAddress, cleared.DeliveryNote, cleared.DeliveryZoneName, cleared.DeliveryFee, cleared.Total)
            .ShouldBe((string.Empty, null, "新營區其他里", 30, 300));
        var staffView = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/ordering/orders/{id}", staff));
        ShouldBeJson(staffView["delivery"], """{"zoneId":"xinying","zoneName":"新營區其他里","fee":30,"address":"","note":null}""");
        var publicView = await JsonAsync(await SendWithTokenAsync(
            HttpMethod.Get, $"/api/v1/public/ordering/{slug}/orders/current", submitted["token"]!.GetValue<string>()));
        publicView["delivery"]!["address"]!.GetValue<string>().ShouldBe(string.Empty);
    }

    // --- Helpers ---------------------------------------------------------------------------------------------

    private sealed record Staff(string Token, Guid AccountId);

    /// <summary>Compares JSON by content: <see cref="JsonNode.ToJsonString"/> escapes Chinese as <c>\uXXXX</c>.</summary>
    private static void ShouldBeJson(JsonNode? actual, string expected) =>
        JsonNode.DeepEquals(actual, JsonNode.Parse(expected)).ShouldBeTrue(actual?.ToJsonString());

    private static List<string> Full(JsonNode dates) =>
    [
        .. dates[0]!["slots"]!.AsArray().Where(slot => slot!["isFull"]!.GetValue<bool>()).Select(slot => slot!["time"]!.GetValue<string>()),
    ];

    private static (string? Service, string? Slot) Scheduled(string slot) => ("delivery-scheduled", slot);

    private static (string? Service, string? Slot) Asap() => ("delivery-asap", null);

    private static JsonObject Order(
        (string? Service, string? Slot) delivery,
        string? zoneId,
        IEnumerable<JsonNode> lines,
        string? address = Address,
        string? note = null,
        string? email = null) =>
        new()
        {
            ["serviceType"] = delivery.Service,
            ["pickupDate"] = delivery.Slot?[..10],
            ["pickupSlot"] = delivery.Slot,
            ["menuVersion"] = 1,
            ["lines"] = new JsonArray([.. lines]),
            ["customer"] = new JsonObject { ["name"] = "陳小姐", ["phone"] = Phone(), ["email"] = email },
            ["orderComment"] = null,
            ["tableware"] = null,
            ["submissionKey"] = Guid.NewGuid().ToString("D"),
            ["deliveryZoneId"] = zoneId,
            ["deliveryAddress"] = address,
            ["deliveryNote"] = note,
        };

    private static JsonObject Takeout(string slot) =>
        new()
        {
            ["serviceType"] = "takeout-scheduled",
            ["pickupDate"] = slot[..10],
            ["pickupSlot"] = slot,
            ["menuVersion"] = 1,
            ["lines"] = new JsonArray(Wonton()),
            ["customer"] = new JsonObject { ["name"] = "林先生", ["phone"] = Phone(), ["email"] = null },
            ["submissionKey"] = Guid.NewGuid().ToString("D"),
        };

    private static string Phone() =>
        "0933" + Interlocked.Increment(ref _phones).ToString("000000", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonNode Wonton(int quantity = 1) =>
        new JsonObject { ["itemId"] = "wonton-soup", ["quantity"] = quantity, ["selections"] = null, ["comment"] = null };

    /// <summary>Large, wide noodles, two eggs and the set with cucumber and winter melon tea: 270.</summary>
    private static JsonNode Beef() => JsonNode.Parse("""
        {"itemId":"beef-noodle","quantity":1,"comment":null,"selections":[
          {"groupId":"size","optionId":"large","count":1,"selections":null},
          {"groupId":"noodle","optionId":"wide","count":1,"selections":null},
          {"groupId":"toppings","optionId":"egg","count":2,"selections":null},
          {"groupId":"combo","optionId":"set","count":1,"selections":[
            {"groupId":"side","optionId":"cucumber","count":1,"selections":null},
            {"groupId":"drink","optionId":"winter-melon","count":1,"selections":null}]}]}
        """)!;

    private static string ShopFile(string slug, int? maxOrdersPerSlot, Action<JsonObject>? change = null)
    {
        var tree = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "deploy", "ordering-shop.example.json")))!.AsObject();
        tree["shop"]!["slug"] = slug;
        tree["shop"]!["maxOrdersPerSlot"] = maxOrdersPerSlot;
        var hours = new JsonObject();
        foreach (var day in new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" })
        {
            hours[day] = JsonNode.Parse("""[{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }]""");
        }

        tree["shop"]!["orderingHours"] = hours;
        change?.Invoke(tree);
        return tree.ToJsonString();
    }

    private async Task<(Organization Shop, string Slug)> CreateShopAsync(int? maxOrdersPerSlot = null)
    {
        var shop = await _host.CreateOrganizationAsync("王記麵店");
        var slug = "shop-" + Guid.NewGuid().ToString("N")[..10];
        await ImportFileAsync(shop, ShopFile(slug, maxOrdersPerSlot));
        return (shop, slug);
    }

    private Task ImportAsync(Organization shop, string slug, Action<JsonObject> change) => ImportFileAsync(shop, ShopFile(slug, null, change));

    private async Task ImportFileAsync(Organization shop, string file)
    {
        var parsed = OrderingShopJson.Parse(file);
        parsed.Errors.ShouldBeEmpty();
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<OrderingShops>().ImportAsync(shop.Id, parsed.Import!, CancellationToken);
        result.Outcome.ShouldBeOneOf(OrderingImportOutcome.Created, OrderingImportOutcome.Updated);
    }

    private async Task<List<OrderingOrder>> OrdersAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.OrderingOrders.AsNoTracking().OrderBy(order => order.Number).ToListAsync(CancellationToken);
    }

    private async Task<int> ClearAsync(Organization shop, DateTimeOffset asOf)
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
        await using var dbContext = new AppDbContext(options, new FixedOrganizationContext(shop.Id));
        var cleanup = ActivatorUtilities.CreateInstance<RetentionCleanupService>(scope.ServiceProvider, dbContext);
        return (await cleanup.ClearOrderPersonalDataAsync(asOf, CancellationToken)).Count;
    }

    private async Task<Staff> SignInAsync(Organization organization, string loginName)
    {
        var account = await _host.CreateAccountAsync(organization, loginName, Password, AccountRole.InternalEmployee, null, AccountPermission.ManageOrders);
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, loginName, Password);
        return new Staff(token.AccessToken, account.Id);
    }

    private Task<HttpResponseMessage> PostAsync(string slug, JsonNode body) =>
        _client.PostAsync(
            $"/api/v1/public/ordering/{slug}/orders",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            CancellationToken);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Staff staff, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staff.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(OrderingPublicEndpoints.TokenHeader, token);
        return await _client.SendAsync(request, CancellationToken);
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(expected, text);
        return JsonNode.Parse(text)!;
    }

    private static async Task<JsonNode> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        var problem = await JsonAsync(response, status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        problem["reason"]!.GetValue<string>().ShouldBe(reason, problem.ToJsonString());
        return problem;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "nx.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (nx.json) above " + AppContext.BaseDirectory);
    }
}
