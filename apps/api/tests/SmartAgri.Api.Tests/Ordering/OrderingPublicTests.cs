using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using SmartAgri.Api.Ordering;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Coordinator;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Ordering;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Ordering;

/// <summary>
/// The anonymous online-ordering API (online-ordering Slice O2, #398) through the real endpoints and PostgreSQL: the shop,
/// menu and slots; an order priced by the server only; a slot's last place and the day's numbers under concurrency; the
/// menu version, sold-out items, category windows, a paused shop, the pending limit per phone, a resent key; looking up and
/// cancelling by token; the same 404 for unknown and other shops; the rate limits; no customer detail in a log.
/// </summary>
/// <remarks>The clock stands at Tuesday 2026-11-17 12:00 in Taipei for every test; each shop takes orders every day
/// 11:00–14:00 and 17:00–21:00, 20 minutes ahead, so the first time is 12:30 and ASAP is 15–30 minutes.</remarks>
[Trait("Category", TestCategories.Docker)]
public sealed class OrderingPublicTests : IClassFixture<OrderingHostFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.FromHours(8));

    private readonly OrderingHostFixture _host;
    private readonly HttpClient _client;

    public OrderingPublicTests(OrderingHostFixture host)
    {
        _host = host;
        _host.Clock.Advance(Now - _host.Clock.GetUtcNow());
        _client = host.Factory.CreateClient();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Reading -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_shop_its_menu_and_its_times_with_no_store_headers()
    {
        var (shop, slug) = await CreateShopAsync();
        await using (var dbContext = _host.Postgres.CreateDbContext(shop.Id))
        {
            var settings = await dbContext.OrderingShops.AsNoTracking().SingleAsync(CancellationToken);
            dbContext.OrderingItemStates.Add(OrderingItemState.SoldOutToday(shop.Id, "zhajiang", settings.Hours, _host.Clock.GetUtcNow()));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var response = await _client.GetAsync($"/api/v1/public/ordering/{slug.ToUpperInvariant()}", CancellationToken);
        var info = await JsonAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        (info["slug"]!.GetValue<string>(), info["name"]!.GetValue<string>(), info["paused"]!.GetValue<bool>()).ShouldBe((slug, "王記麵店", false));
        info["services"]!.ToJsonString().ShouldBe("""{"takeoutScheduled":true,"takeoutAsap":true}""");
        info["open"]!["isOpen"]!.GetValue<bool>().ShouldBeTrue();
        Instant(info["open"]!["currentPeriodCloses"]).ShouldBe(new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.FromHours(8)));
        Instant(info["open"]!["nextOpening"]).ShouldBe(new DateTimeOffset(2026, 11, 17, 17, 0, 0, TimeSpan.FromHours(8)));
        info["asap"]!.ToJsonString().ShouldBe("""{"minMinutes":15,"maxMinutes":30}""");
        info["slotRules"]!.ToJsonString().ShouldBe("""{"slotMinutes":30,"daysAhead":3,"minLeadMinutes":20}""");
        info["menuVersion"]!.GetValue<int>().ShouldBe(1);
        info["orderingHours"]!.AsArray().Select(day => day!["day"]!.GetValue<string>()).ShouldBe(["mon", "tue", "wed", "thu", "fri", "sat", "sun"]);
        info["orderingHours"]![1]!["periods"]!.ToJsonString().ShouldBe("""[{"open":"11:00","close":"14:00"},{"open":"17:00","close":"21:00"}]""");
        info["limits"]!["maxLines"]!.GetValue<int>().ShouldBe(30);
        info["notices"]!["checkout"]!.GetValue<string>().ShouldStartWith("取餐時付款");

        var menu = await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/menu", CancellationToken));
        menu["version"]!.GetValue<int>().ShouldBe(1);
        menu["categories"]!.AsArray().Select(category => category!["id"]!.GetValue<string>()).ShouldBe(["noodles", "sides", "lunch-special"]);
        menu["categories"]![2]!["availability"]!.ToJsonString().ShouldBe("""{"days":["tue","wed","thu","fri"],"from":"11:00","to":"14:00"}""");
        var zhajiang = menu["categories"]![0]!["items"]![1]!;
        zhajiang["state"]!["kind"]!.GetValue<string>().ShouldBe("sold-out-today");
        Instant(zhajiang["state"]!["resetsAt"]).ShouldBe(new DateTimeOffset(2026, 11, 18, 11, 0, 0, TimeSpan.FromHours(8)));
        menu["categories"]![0]!["items"]![0]!["state"].ShouldBeNull();
        menu["categories"]![0]!["items"]![0]!["optionGroups"]![3]!["options"]![0]!["optionGroups"]![1]!["options"]![2]!["price"]!.GetValue<int>()
            .ShouldBe(10, "four levels deep, the winter melon tea");

        var slots = await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/slots", CancellationToken));
        var today = slots["dates"]![0]!;
        today["date"]!.GetValue<string>().ShouldBe("2026-11-17");
        today["slots"]!.AsArray().Select(slot => slot!["time"]!.GetValue<string>()).ShouldBe(
            ["12:30", "13:00", "13:30", "14:00", "17:00", "17:30", "18:00", "18:30", "19:00", "19:30", "20:00", "20:30", "21:00"]);
        today["slots"]![0]!.ToJsonString().ShouldContain("\"value\":\"2026-11-17T12:30\"");
        slots["dates"]!.AsArray().Count.ShouldBe(3);
    }

    [Fact]
    public async Task An_unknown_slug_a_shop_without_a_menu_and_a_malformed_slug_are_the_same_404()
    {
        var bare = await _host.CreateOrganizationAsync("沒有菜單的店");
        var bareSlug = NewSlug();
        await using (var dbContext = _host.Postgres.CreateDbContext(bare.Id))
        {
            dbContext.OrderingShops.Add(OrderingShop.Create(bare.Id, OrderingShopJson.Parse(ShopFile(bareSlug)).Import!.Shop, _host.Clock.GetUtcNow()));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var answers = new List<HttpResponseMessage>
        {
            await _client.GetAsync($"/api/v1/public/ordering/{NewSlug()}", CancellationToken),
            await _client.GetAsync($"/api/v1/public/ordering/{bareSlug}", CancellationToken),
            await _client.GetAsync($"/api/v1/public/ordering/{bareSlug}/menu", CancellationToken),
            await _client.GetAsync("/api/v1/public/ordering/x/slots", CancellationToken),
            await _client.PostAsJsonAsync($"/api/v1/public/ordering/{bareSlug}/orders", Order(1, Asap(), [Wonton()]), CancellationToken),
        };

        var first = await answers[0].Content.ReadAsStringAsync(CancellationToken);
        JsonNode.Parse(first)!["reason"]!.GetValue<string>().ShouldBe("shop-not-found");
        foreach (var answer in answers)
        {
            answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            answer.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            (await answer.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(first);
        }
    }

    // --- Sending an order ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_scheduled_order_is_priced_by_the_server_and_found_by_its_token()
    {
        var (shop, slug) = await CreateShopAsync();
        // Prices the client might send are not part of the request and are ignored.
        var beef = Beef();
        beef["price"] = 1;
        beef["unitPrice"] = 1;
        beef["lineTotal"] = 1;
        var body = Order(1, Scheduled("2026-11-17", "2026-11-17T12:30"), [beef, Wonton(2)], comment: "  請放門口  ", tableware: true);
        body["total"] = 1;

        var response = await _client.PostAsJsonAsync($"/api/v1/public/ordering/{slug}/orders", body, CancellationToken);
        var submitted = await JsonAsync(response, HttpStatusCode.Created);

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var token = submitted["token"]!.GetValue<string>();
        OrderingOrder.IsStatusToken(token).ShouldBeTrue();
        var order = submitted["order"]!;
        (order["number"]!.GetValue<int>(), order["displayNumber"]!.GetValue<string>(), order["status"]!.GetValue<string>()).ShouldBe((1, "#001", "pending"));
        (order["serviceType"]!.GetValue<string>(), order["businessDate"]!.GetValue<string>()).ShouldBe(("takeout-scheduled", "2026-11-17"));
        Instant(order["pickupAt"]).ShouldBe(new DateTimeOffset(2026, 11, 17, 12, 30, 0, TimeSpan.FromHours(8)));
        order["estimate"].ShouldBeNull();
        order["lines"]!.AsArray().Select(line => (line!["itemId"]!.GetValue<string>(), line["unitPrice"]!.GetValue<int>(), line["lineTotal"]!.GetValue<int>()))
            .ShouldBe([("beef-noodle", 270, 270), ("wonton-soup", 70, 140)]);
        order["lines"]![0]!["comment"]!.GetValue<string>().ShouldBe("麵硬一點");
        (order["subtotal"]!.GetValue<int>(), order["total"]!.GetValue<int>(), order["menuVersion"]!.GetValue<int>()).ShouldBe((410, 410, 1));
        (order["customerName"]!.GetValue<string>(), order["phoneLastDigits"]!.GetValue<string>()).ShouldBe(("陳小姐", "678"));
        (order["orderComment"]!.GetValue<string>(), order["tableware"]!.GetValue<bool>(), order["canCancel"]!.GetValue<bool>()).ShouldBe(("請放門口", true, true));
        order.AsObject().Select(property => property.Key).ShouldNotContain("customerEmail");

        var lookedUp = await _client.GetAsync($"/api/v1/public/ordering/{slug}/orders/{token}", CancellationToken);
        lookedUp.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        (await JsonAsync(lookedUp)).ToJsonString().ShouldBe(order.ToJsonString());

        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        var stored = await dbContext.OrderingOrders.AsNoTracking().SingleAsync(CancellationToken);
        (stored.CustomerPhone, stored.CustomerEmail, stored.Total).ShouldBe(("0912345678", "chen@example.com", 410));
        stored.StatusTokenHash.ShouldBe(OrderingOrder.HashStatusToken(token));
        stored.StatusTokenHash.ShouldNotContain(token);
    }

    [Fact]
    public async Task An_asap_order_is_picked_up_within_the_estimate_and_numbered_after_the_days_others()
    {
        var (_, slug) = await CreateShopAsync();
        await SubmitAsync(slug, Order(1, Scheduled("2026-11-17", "2026-11-17T13:00"), [Wonton()], phone: "0911000001"));

        var order = (await SubmitAsync(slug, Order(1, Asap(), [Wonton()], phone: "0911000002")))["order"]!;

        (order["displayNumber"]!.GetValue<string>(), order["serviceType"]!.GetValue<string>()).ShouldBe(("#002", "takeout-asap"));
        order["estimate"]!.ToJsonString().ShouldBe("""{"minMinutes":15,"maxMinutes":30}""");
        (Instant(order["pickupAt"]) - Now).ShouldBeInRange(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(31));
        order["tableware"]!.GetValue<bool>().ShouldBeFalse("asked and not given: no");

        // Another business day counts from 1.
        var tomorrow = (await SubmitAsync(slug, Order(1, Scheduled("2026-11-18", "2026-11-18T11:00"), [Wonton()], phone: "0911000003")))["order"]!;
        (tomorrow["displayNumber"]!.GetValue<string>(), tomorrow["businessDate"]!.GetValue<string>()).ShouldBe(("#001", "2026-11-18"));
    }

    [Fact]
    public async Task A_wrong_option_or_field_is_422_with_every_problem_and_nothing_is_stored()
    {
        var (shop, slug) = await CreateShopAsync();
        var beef = Beef();
        beef["selections"]![0]!["optionId"] = "huge";

        var lines = await ProblemAsync(
            await PostAsync(slug, Order(1, Asap(), [Wonton(), beef, Item("zhajiang")])), HttpStatusCode.UnprocessableEntity, "invalid-order");
        lines["lineErrors"]!.ToJsonString().ShouldBe(
            """[{"line":1,"code":"unknown-option","path":"size.huge"},{"line":1,"code":"too-few","path":"size"},{"line":2,"code":"too-few","path":"spicy"}]""");

        var badFields = Order(1, Asap(), [Item("wonton-soup", comment: new string('辣', 101))], phone: "123", name: " ");
        badFields["submissionKey"] = "not-a-uuid";
        badFields["customer"]!["email"] = "not-an-email";
        var fields = await ProblemAsync(await PostAsync(slug, badFields), HttpStatusCode.UnprocessableEntity, "invalid-order");
        fields["errors"]!.AsObject().Select(error => error.Key).Order(StringComparer.Ordinal).ShouldBe(
            ["customer.email", "customer.name", "customer.phone", "lines[0].comment", "submissionKey"]);
        fields["lineErrors"].ShouldBeNull();

        var empty = await ProblemAsync(await PostAsync(slug, Order(1, Scheduled(null, null), [])), HttpStatusCode.UnprocessableEntity, "invalid-order");
        empty["errors"]!.AsObject().Select(error => error.Key).Order(StringComparer.Ordinal).ShouldBe(["lines", "pickupSlot"]);
        var tooMany = await ProblemAsync(
            await PostAsync(slug, Order(1, Asap(), [.. Enumerable.Range(0, 31).Select(_ => Wonton())])), HttpStatusCode.UnprocessableEntity, "invalid-order");
        tooMany["errors"]!["lines"]![0]!.GetValue<string>().ShouldBe("一筆訂單最多 30 項。");
        (await PostAsync(slug, JsonNode.Parse("""{"serviceType":"delivery"}""")!)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await OrdersAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_customers_sending_for_the_last_place_at_once_get_one_order()
    {
        var (shop, slug) = await CreateShopAsync(maxOrdersPerSlot: 1);
        var slot = Scheduled("2026-11-17", "2026-11-17T13:30");

        var answers = await Task.WhenAll(
            PostAsync(slug, Order(1, slot, [Wonton()], phone: "0922000001")),
            PostAsync(slug, Order(1, slot, [Wonton()], phone: "0922000002")));

        answers.Select(answer => answer.StatusCode).Order().ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        await ProblemAsync(answers.Single(answer => answer.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "slot-full");
        (await OrdersAsync(shop)).Count.ShouldBe(1);
        var slots = await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/slots", CancellationToken));
        slots["dates"]![0]!["slots"]!.AsArray().Single(candidate => candidate!["time"]!.GetValue<string>() == "13:30")!["isFull"]!.GetValue<bool>().ShouldBeTrue();

        // A slot that is not offered (too soon, or no such time).
        await ProblemAsync(await PostAsync(slug, Order(1, Scheduled("2026-11-17", "2026-11-17T12:00"), [Wonton()])), HttpStatusCode.Conflict, "slot-unavailable");
        await ProblemAsync(await PostAsync(slug, Order(1, Scheduled("2026-11-17", "2026-11-17T12:45"), [Wonton()])), HttpStatusCode.Conflict, "slot-unavailable");
    }

    [Fact]
    public async Task Orders_sent_at_once_get_the_days_numbers_in_sequence()
    {
        var (shop, slug) = await CreateShopAsync();

        var answers = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => PostAsync(slug, Order(1, Asap(), [Wonton()], phone: $"09330000{i:00}"))));

        answers.ShouldAllBe(answer => answer.StatusCode == HttpStatusCode.Created);
        (await OrdersAsync(shop)).Select(order => order.Number).Order().ShouldBe([1, 2, 3, 4, 5, 6, 7, 8]);
    }

    [Fact]
    public async Task A_cart_of_an_older_menu_version_is_refused_with_the_current_one()
    {
        var (shop, slug) = await CreateShopAsync();
        await ImportAsync(shop, slug, tree => tree["menu"]!["categories"]![0]!["items"]![2]!["price"] = 75);

        var refused = await ProblemAsync(await PostAsync(slug, Order(1, Asap(), [Wonton()])), HttpStatusCode.Conflict, "menu-changed");

        refused["menuVersion"]!.GetValue<int>().ShouldBe(2);
        var order = (await SubmitAsync(slug, Order(2, Asap(), [Wonton()])))["order"]!;
        order["lines"]![0]!["unitPrice"]!.GetValue<int>().ShouldBe(75);
    }

    [Fact]
    public async Task Sold_out_and_stopped_items_and_a_category_outside_its_window_are_refused_by_line()
    {
        var (shop, slug) = await CreateShopAsync();
        await using (var dbContext = _host.Postgres.CreateDbContext(shop.Id))
        {
            var settings = await dbContext.OrderingShops.AsNoTracking().SingleAsync(CancellationToken);
            dbContext.OrderingItemStates.Add(OrderingItemState.SoldOutToday(shop.Id, "zhajiang", settings.Hours, _host.Clock.GetUtcNow()));
            dbContext.OrderingItemStates.Add(OrderingItemState.Unavailable(shop.Id, "cucumber", _host.Clock.GetUtcNow()));
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var lunch = Item("lunch-set", selections: [Choice("drink", "green-tea")]);
        var today = await ProblemAsync(
            await PostAsync(slug, Order(1, Scheduled("2026-11-17", "2026-11-17T17:30"), [Item("zhajiang", selections: [Choice("spicy", "mild")]), Item("cucumber"), lunch.DeepClone(), Wonton()])),
            HttpStatusCode.Conflict,
            "items-unavailable");
        today["lineErrors"]!.ToJsonString().ShouldBe(
            """[{"line":0,"code":"sold-out-today","path":""},{"line":1,"code":"unavailable","path":""},{"line":2,"code":"category-unavailable","path":""}]""");

        // Tomorrow's lunch: back from Wednesday's opening, and the lunch set is offered.
        await SubmitAsync(slug, Order(1, Scheduled("2026-11-18", "2026-11-18T12:00"), [Item("zhajiang", selections: [Choice("spicy", "mild")]), lunch]));
        (await OrdersAsync(shop)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_paused_shop_offers_no_time_and_takes_no_order()
    {
        var (shop, slug) = await CreateShopAsync();
        await using (var dbContext = _host.Postgres.CreateDbContext(shop.Id))
        {
            var settings = await dbContext.OrderingShops.SingleAsync(CancellationToken);
            settings.SetPaused(true, _host.Clock.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var info = await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}", CancellationToken));
        info["paused"]!.GetValue<bool>().ShouldBeTrue();
        info["asap"].ShouldBeNull();
        (await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/slots", CancellationToken)))["dates"]!.AsArray().ShouldBeEmpty();
        var refused = await ProblemAsync(await PostAsync(slug, Order(1, Asap(), [Wonton()])), HttpStatusCode.Conflict, "shop-paused");
        refused["message"]!.GetValue<string>().ShouldContain("06-123-4567");
        await ProblemAsync(await PostAsync(slug, Order(1, Scheduled("2026-11-17", "2026-11-17T13:00"), [Wonton()])), HttpStatusCode.Conflict, "shop-paused");
        (await OrdersAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_phone_has_at_most_three_pending_orders_and_a_cancelled_one_frees_a_place()
    {
        var (_, slug) = await CreateShopAsync();
        var tokens = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            tokens.Add((await SubmitAsync(slug, Order(1, Asap(), [Wonton()], phone: "+886 912 000 555")))["token"]!.GetValue<string>());
        }

        await ProblemAsync(await PostAsync(slug, Order(1, Asap(), [Wonton()], phone: "0912-000-555")), HttpStatusCode.TooManyRequests, "too-many-pending");
        (await _client.PostAsync($"/api/v1/public/ordering/{slug}/orders/{tokens[0]}/cancel", null, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SubmitAsync(slug, Order(1, Asap(), [Wonton()], phone: "0912000555"));
    }

    [Fact]
    public async Task The_same_key_sent_again_is_one_order_and_says_its_number()
    {
        var (shop, slug) = await CreateShopAsync();
        var body = Order(1, Asap(), [Wonton()]);
        await SubmitAsync(slug, body);

        var again = await ProblemAsync(await PostAsync(slug, body), HttpStatusCode.Conflict, "duplicate-submission");
        var upper = body.DeepClone();
        upper["submissionKey"] = body["submissionKey"]!.GetValue<string>().ToUpperInvariant();
        await ProblemAsync(await PostAsync(slug, upper), HttpStatusCode.Conflict, "duplicate-submission");

        again["orderNumber"]!.GetValue<string>().ShouldBe("#001");
        (await OrdersAsync(shop)).Count.ShouldBe(1);

        // Another shop may see the same key: it is per shop.
        var (_, other) = await CreateShopAsync();
        await SubmitAsync(other, body);
    }

    [Fact]
    public async Task A_pending_order_can_be_cancelled_once_and_frees_its_place()
    {
        var (shop, slug) = await CreateShopAsync(maxOrdersPerSlot: 1);
        var slot = Scheduled("2026-11-17", "2026-11-17T14:00");
        var token = (await SubmitAsync(slug, Order(1, slot, [Wonton()], phone: "0955000001")))["token"]!.GetValue<string>();

        var cancelled = await _client.PostAsync($"/api/v1/public/ordering/{slug}/orders/{token}/cancel", null, CancellationToken);
        var order = await JsonAsync(cancelled);
        (order["status"]!.GetValue<string>(), order["canCancel"]!.GetValue<bool>()).ShouldBe(("cancelled", false));
        Instant(order["cancelledAt"]).ShouldBe(_host.Clock.GetUtcNow(), TimeSpan.FromSeconds(5));
        cancelled.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);

        var twice = await ProblemAsync(
            await _client.PostAsync($"/api/v1/public/ordering/{slug}/orders/{token}/cancel", null, CancellationToken), HttpStatusCode.Conflict, "not-cancellable");
        twice["orderStatus"]!.GetValue<string>().ShouldBe("cancelled");
        (await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/orders/{token}", CancellationToken)))["status"]!.GetValue<string>()
            .ShouldBe("cancelled");
        var stored = (await OrdersAsync(shop)).ShouldHaveSingleItem();
        (stored.Status, stored.Version).ShouldBe((OrderingOrderStatus.Cancelled, 2));

        // The place is free again.
        await SubmitAsync(slug, Order(1, slot, [Wonton()], phone: "0955000002"));
    }

    [Fact]
    public async Task A_token_is_found_only_under_its_own_shop()
    {
        var (_, slug) = await CreateShopAsync();
        var (_, other) = await CreateShopAsync();
        var token = (await SubmitAsync(slug, Order(1, Asap(), [Wonton()])))["token"]!.GetValue<string>();

        foreach (var path in new[]
                 {
                     $"/api/v1/public/ordering/{other}/orders/{token}",
                     $"/api/v1/public/ordering/{slug}/orders/{OrderingOrder.NewStatusToken()}",
                     $"/api/v1/public/ordering/{slug}/orders/not-a-token",
                 })
        {
            await ProblemAsync(await _client.GetAsync(path, CancellationToken), HttpStatusCode.NotFound, "order-not-found");
        }

        await ProblemAsync(
            await _client.PostAsync($"/api/v1/public/ordering/{other}/orders/{token}/cancel", null, CancellationToken), HttpStatusCode.NotFound, "order-not-found");
        (await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/orders/{token}", CancellationToken)))["status"]!.GetValue<string>()
            .ShouldBe("pending");
    }

    [Fact]
    public async Task Another_sites_page_cannot_send_an_order()
    {
        var (shop, slug) = await CreateShopAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/public/ordering/{slug}/orders")
        {
            Content = JsonContent.Create(Order(1, Asap(), [Wonton()])),
        };
        request.Headers.Add("Origin", "https://evil.example");

        (await _client.SendAsync(request, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await OrdersAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Beyond_the_rate_limits_a_client_gets_429_and_sending_has_its_own_budget()
    {
        using var factory = _host.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PublicChannels:RateLimits:OrderingRequestsPerIpPerMinute", "2");
            builder.UseSetting("PublicChannels:RateLimits:OrderingSubmissionsPerIpPerMinute", "1");
        });
        using var client = factory.CreateClient();

        (await client.GetAsync("/api/v1/public/ordering/x", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/public/ordering/x/menu", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var refused = await client.GetAsync("/api/v1/public/ordering/x/slots", CancellationToken);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(60));
        refused.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await JsonAsync(refused, HttpStatusCode.TooManyRequests))["reason"]!.GetValue<string>().ShouldBe("rate-limited");
        (await client.PostAsJsonAsync("/api/v1/public/ordering/x/orders", Order(1, Asap(), [Wonton()]), CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound, "sending has its own budget");
        (await client.PostAsJsonAsync("/api/v1/public/ordering/x/orders", Order(1, Asap(), [Wonton()]), CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_customers_details_and_the_token_never_reach_a_log()
    {
        var (shop, slug) = await CreateShopAsync();
        var name = "林" + Guid.NewGuid().ToString("N")[..6];
        var email = $"log-{Guid.NewGuid():N}@example.com";
        var comment = "備註" + Guid.NewGuid().ToString("N")[..8];
        string token;

        _host.Log.Start();
        try
        {
            await PostAsync(slug, Order(1, Asap(), [Item("wonton-soup", comment: comment)], phone: "0933-221-100", name: name, email: "bad@" + email));
            var body = Order(1, Asap(), [Item("wonton-soup", comment: comment)], phone: "0933-221-100", name: name, email: email, comment: comment);
            token = (await SubmitAsync(slug, body))["token"]!.GetValue<string>();
            await PostAsync(slug, body);
            await _client.PostAsync($"/api/v1/public/ordering/{slug}/orders/{token}/cancel", null, CancellationToken);
        }
        finally
        {
            _host.Log.Stop();
        }

        var stored = (await OrdersAsync(shop)).ShouldHaveSingleItem();
        var lines = _host.Log.Lines;
        lines.ShouldContain(line => line.Contains(stored.Id.ToString(), StringComparison.Ordinal), "not vacuous: the order was logged");
        foreach (var secret in new[] { name, "0933221100", "0933-221-100", "221-100", email, comment })
        {
            lines.ShouldNotContain(line => line.Contains(secret, StringComparison.Ordinal), secret);
        }

        // The request path (a logging scope of the host) carries it; the ordering code's own messages never do.
        lines.Where(line => line.StartsWith("SmartAgri.Api.Ordering", StringComparison.Ordinal) && !line.Contains(" scope: ", StringComparison.Ordinal))
            .ShouldNotContain(line => line.Contains(token, StringComparison.Ordinal));
    }

    // --- Helpers ---------------------------------------------------------------------------------------------

    private static string NewSlug() => "shop-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>The example file with <paramref name="slug"/>, every day 11:00–14:00 and 17:00–21:00.</summary>
    private static string ShopFile(string slug, int? maxOrdersPerSlot = null, Action<JsonObject>? change = null)
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
        var slug = NewSlug();
        await ImportFileAsync(shop, ShopFile(slug, maxOrdersPerSlot));
        return (shop, slug);
    }

    private Task ImportAsync(Organization shop, string slug, Action<JsonObject> change) => ImportFileAsync(shop, ShopFile(slug, change: change));

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

    private static (string Service, string? Date, string? Slot) Asap() => ("takeout-asap", null, null);

    private static (string Service, string? Date, string? Slot) Scheduled(string? date, string? slot) => ("takeout-scheduled", date, slot);

    private static JsonObject Order(
        int menuVersion,
        (string Service, string? Date, string? Slot) pickup,
        IEnumerable<JsonNode> lines,
        string phone = "0912-345-678",
        string name = "陳小姐",
        string? email = "chen@example.com",
        string? comment = null,
        bool? tableware = null) =>
        new()
        {
            ["serviceType"] = pickup.Service,
            ["pickupDate"] = pickup.Date,
            ["pickupSlot"] = pickup.Slot,
            ["menuVersion"] = menuVersion,
            ["lines"] = new JsonArray([.. lines]),
            ["customer"] = new JsonObject { ["name"] = name, ["phone"] = phone, ["email"] = email },
            ["orderComment"] = comment,
            ["tableware"] = tableware,
            ["submissionKey"] = Guid.NewGuid().ToString("D"),
        };

    private static JsonObject Item(string itemId, int quantity = 1, IEnumerable<JsonNode>? selections = null, string? comment = null) =>
        new()
        {
            ["itemId"] = itemId,
            ["quantity"] = quantity,
            ["selections"] = selections is null ? null : new JsonArray([.. selections]),
            ["comment"] = comment,
        };

    private static JsonObject Choice(string groupId, string optionId, int count = 1, IEnumerable<JsonNode>? under = null) =>
        new()
        {
            ["groupId"] = groupId,
            ["optionId"] = optionId,
            ["count"] = count,
            ["selections"] = under is null ? null : new JsonArray([.. under]),
        };

    private static JsonObject Wonton(int quantity = 1) => Item("wonton-soup", quantity);

    /// <summary>Large, wide noodles, two eggs and the set with cucumber and winter melon tea: 270.</summary>
    private static JsonObject Beef() => Item(
        "beef-noodle",
        selections:
        [
            Choice("size", "large"),
            Choice("noodle", "wide"),
            Choice("toppings", "egg", 2),
            Choice("combo", "set", under: [Choice("side", "cucumber"), Choice("drink", "winter-melon")]),
        ],
        comment: "麵硬一點");

    private Task<HttpResponseMessage> PostAsync(string slug, JsonNode body) =>
        _client.PostAsync(
            $"/api/v1/public/ordering/{slug}/orders",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            CancellationToken);

    private async Task<JsonNode> SubmitAsync(string slug, JsonNode body) => await JsonAsync(await PostAsync(slug, body), HttpStatusCode.Created);

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
        problem["status"]!.GetValue<int>().ShouldBe((int)status);
        problem["message"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        return problem;
    }

    private static DateTimeOffset Instant(JsonNode? node) => DateTimeOffset.Parse(node!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);

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

public sealed class OrderingHostFixture : AuthHostFixture
{
    public CoordinatorTestLog Log { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(Log);
        services.Configure<LoggerFilterOptions>(options =>
            options.Rules.Add(new LoggerFilterRule(typeof(CoordinatorTestLog).FullName, categoryName: null, LogLevel.Trace, filter: null)));
    }
}
