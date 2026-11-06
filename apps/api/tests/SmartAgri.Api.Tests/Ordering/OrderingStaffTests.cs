using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using SmartAgri.Api.Ordering;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Coordinator;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ordering;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Ordering;

/// <summary>
/// The admin's online-ordering API (online-ordering Slice O3, #399) through the real endpoints and PostgreSQL: the
/// permission and the organization; every step of an order and what its customer then sees; refused steps with the order
/// as it is now; two members (or a member and the customer) at once — exactly one wins and the outcome seam runs once;
/// the lists and the summary; pausing, the ASAP band and item states as the public menu and submissions see them.
/// </summary>
/// <remarks>The clock stands at Tuesday 2026-11-17 12:00 in Taipei for every test; each shop takes orders every day
/// 11:00–14:00 and 17:00–21:00 (the example file), so the first time is 12:30.</remarks>
[Trait("Category", TestCategories.Docker)]
public sealed class OrderingStaffTests : IClassFixture<OrderingStaffHostFixture>
{
    private const string Password = "Staff-Password-1!";

    private const string TodayAt1230 = "2026-11-17T12:30";

    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.FromHours(8));

    private static int _phones;

    private readonly OrderingStaffHostFixture _host;
    private readonly HttpClient _client;

    public OrderingStaffTests(OrderingStaffHostFixture host)
    {
        _host = host;
        _host.Clock.Advance(Now - _host.Clock.GetUtcNow());
        _client = host.Factory.CreateClient();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Permission and organization -------------------------------------------------------------------------

    [Fact]
    public async Task Without_manage_orders_every_endpoint_is_the_same_403_as_another_shops_order()
    {
        var (shop, slug) = await CreateShopAsync();
        var outsider = await SignInAsync(shop, "outsider", AccountRole.InternalEmployee, AccountPermission.UseSharedAssistants);
        var order = await SubmitAsync(slug);
        var orderId = await OrderIdAsync(shop, order);

        var (other, _) = await CreateShopAsync();
        var otherStaff = await SignInAsync(other, "staff");

        var refusals = new List<HttpResponseMessage>
        {
            await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", outsider),
            await SendAsync(HttpMethod.Get, "/api/v1/ordering/orders?view=pending", outsider),
            await SendAsync(HttpMethod.Get, $"/api/v1/ordering/orders/{orderId}", outsider),
            await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{orderId}/accept", outsider, new { version = 1 }),
            await SendAsync(HttpMethod.Put, "/api/v1/ordering/pause", outsider, new { paused = true }),
            await SendAsync(HttpMethod.Get, "/api/v1/ordering/items", outsider),
            await SendAsync(HttpMethod.Put, "/api/v1/ordering/items/wonton-soup/state", outsider, new { state = "unavailable" }),
            // Another shop's member with the permission: the order is not theirs, the answer is the same bytes.
            await SendAsync(HttpMethod.Get, $"/api/v1/ordering/orders/{orderId}", otherStaff),
            await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{orderId}/accept", otherStaff, new { version = 1 }),
        };

        var first = await refusals[0].Content.ReadAsStringAsync(CancellationToken);
        JsonNode.Parse(first)!["reason"]!.GetValue<string>().ShouldBe("ordering");
        foreach (var refusal in refusals)
        {
            refusal.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await refusal.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(first);
        }

        (await StatusOfAsync(shop, orderId)).ShouldBe((OrderingOrderStatus.Pending, 1));
        (await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_organization_without_ordering_settings_is_404_ordering_not_configured()
    {
        var bare = await _host.CreateOrganizationAsync("還沒設定點餐的店");
        var staff = await SignInAsync(bare, "staff");

        foreach (var response in new[]
                 {
                     await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff),
                     await SendAsync(HttpMethod.Get, "/api/v1/ordering/orders?view=active", staff),
                     await SendAsync(HttpMethod.Put, "/api/v1/ordering/pause", staff, new { paused = true }),
                     await SendAsync(HttpMethod.Put, "/api/v1/ordering/asap-band", staff, new { band = "30-45" }),
                     await SendAsync(HttpMethod.Get, "/api/v1/ordering/items", staff),
                     await SendAsync(HttpMethod.Put, "/api/v1/ordering/items/wonton-soup/state", staff, new { state = "unavailable" }),
                     await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{Guid.NewGuid()}/accept", staff, new { version = 1 }),
                 })
        {
            await ProblemAsync(response, HttpStatusCode.NotFound, "ordering-not-configured");
        }
    }

    // --- The steps -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Accept_with_a_later_time_ready_and_complete_each_show_on_the_staff_list_and_the_customers_page()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff", displayName: "阿明");
        var submitted = await SubmitAsync(slug, comment: "請放門口");
        var token = submitted["token"]!.GetValue<string>();
        _host.Outcomes.Clear();

        var pending = (await ListAsync(staff, "pending"))["orders"]!.AsArray().Single()!;
        var id = pending["id"]!.GetValue<string>();
        (pending["status"]!.GetValue<string>(), pending["version"]!.GetValue<int>(), pending["displayNumber"]!.GetValue<string>())
            .ShouldBe(("pending", 1, "#001"));
        (pending["customerName"]!.GetValue<string>(), pending["customerPhone"]!.GetValue<string>(), pending["customerEmail"]!.GetValue<string>())
            .ShouldBe(("陳小姐", Phone(_phones), "chen@example.com"));
        pending["customerPhoneDisplay"]!.GetValue<string>().ShouldContain("-");
        pending["lines"]![0]!["comment"]!.GetValue<string>().ShouldBe("麵硬一點");
        pending["orderComment"]!.GetValue<string>().ShouldBe("請放門口");
        (pending["total"]!.GetValue<int>(), pending["promisedAt"]).ShouldBe((270, null));
        pending["actions"]!.ToJsonString().ShouldBe("""["accept","decline"]""");

        var later = new DateTimeOffset(2026, 11, 17, 12, 45, 0, TimeSpan.FromHours(8));
        var accepted = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/accept", staff, new { version = 1, promisedAt = later }));
        (accepted["status"]!.GetValue<string>(), accepted["version"]!.GetValue<int>()).ShouldBe(("accepted", 2));
        Instant(accepted["promisedAt"]).ShouldBe(later);
        accepted["accepted"]!["by"]!["displayName"]!.GetValue<string>().ShouldBe("阿明");
        accepted["actions"]!.ToJsonString().ShouldBe("""["ready","complete","cancel"]""");

        var customer = await JsonAsync(await GetOrderAsync(slug, token));
        (customer["status"]!.GetValue<string>(), customer["canCancel"]!.GetValue<bool>()).ShouldBe(("accepted", false));
        Instant(customer["promisedAt"]).ShouldBe(later);
        Instant(customer["pickupAt"]).ShouldBe(new DateTimeOffset(2026, 11, 17, 12, 30, 0, TimeSpan.FromHours(8)));
        await ProblemAsync(await CancelByCustomerAsync(slug, token), HttpStatusCode.Conflict, "not-cancellable");

        (await ListAsync(staff, "pending"))["orders"]!.AsArray().ShouldBeEmpty();
        (await ListAsync(staff, "active"))["orders"]!.AsArray().Single()!["id"]!.GetValue<string>().ShouldBe(id);

        var ready = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/ready", staff, new { version = 2 }));
        (ready["status"]!.GetValue<string>(), ready["version"]!.GetValue<int>()).ShouldBe(("ready", 3));
        (await JsonAsync(await GetOrderAsync(slug, token)))["status"]!.GetValue<string>().ShouldBe("ready");

        var completed = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/complete", staff, new { version = 3 }));
        (completed["status"]!.GetValue<string>(), completed["actions"]!.AsArray().Count).ShouldBe(("completed", 0));
        completed["completed"]!["by"]!["displayName"]!.GetValue<string>().ShouldBe("阿明");
        (await ListAsync(staff, "active"))["orders"]!.AsArray().ShouldBeEmpty();
        (await ListAsync(staff, "done"))["orders"]!.AsArray().Single()!["status"]!.GetValue<string>().ShouldBe("completed");
        (await JsonAsync(await GetOrderAsync(slug, token)))["status"]!.GetValue<string>().ShouldBe("completed");

        _host.Outcomes.Statuses(Guid.Parse(id)).ShouldBe([OrderingOrderStatus.Accepted], "ready and completed tell nobody");
    }

    [Fact]
    public async Task A_decline_shows_its_reason_to_the_customer_and_a_shop_cancel_records_who_and_why()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var declinedOrder = await SubmitAsync(slug);
        var declinedId = await OrderIdAsync(shop, declinedOrder);
        var cancelledOrder = await SubmitAsync(slug);
        var cancelledId = await OrderIdAsync(shop, cancelledOrder);
        var customerCancelled = await SubmitAsync(slug);
        _host.Outcomes.Clear();

        var declined = await JsonAsync(await SendAsync(
            HttpMethod.Post, $"/api/v1/ordering/orders/{declinedId}/decline", staff, new { version = 1, reason = "sold-out", note = "  牛肉麵賣完了  " }));
        (declined["status"]!.GetValue<string>(), declined["declined"]!["reason"]!.GetValue<string>(), declined["declined"]!["note"]!.GetValue<string>())
            .ShouldBe(("declined", "sold-out", "牛肉麵賣完了"));
        var seen = await JsonAsync(await GetOrderAsync(slug, declinedOrder["token"]!.GetValue<string>()));
        (seen["status"]!.GetValue<string>(), seen["declineReason"]!.GetValue<string>(), seen["declineNote"]!.GetValue<string>(), seen["canCancel"]!.GetValue<bool>())
            .ShouldBe(("declined", "sold-out", "牛肉麵賣完了", false));

        await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{cancelledId}/accept", staff, new { version = 1 }));
        var cancelled = await JsonAsync(await SendAsync(
            HttpMethod.Post, $"/api/v1/ordering/orders/{cancelledId}/cancel", staff, new { version = 2, reason = "customer-no-show" }));
        var cancellation = cancelled["cancelled"]!;
        (cancellation["by"]!.GetValue<string>(), cancellation["reason"]!.GetValue<string>(), cancellation["member"]!["id"]!.GetValue<string>())
            .ShouldBe(("shop", "customer-no-show", staff.AccountId.ToString()));
        var cancelledSeen = await JsonAsync(await GetOrderAsync(slug, cancelledOrder["token"]!.GetValue<string>()));
        (cancelledSeen["cancelledBy"]!.GetValue<string>(), cancelledSeen["cancelReason"]!.GetValue<string>(), cancelledSeen["cancelNote"])
            .ShouldBe(("shop", "customer-no-show", null));

        await JsonAsync(await CancelByCustomerAsync(slug, customerCancelled["token"]!.GetValue<string>()));
        var done = (await ListAsync(staff, "done"))["orders"]!.AsArray();
        done.Count.ShouldBe(3);
        done[0]!["cancelled"]!.ToJsonString().ShouldContain("\"by\":\"customer\",\"member\":null,\"reason\":null,\"note\":null");

        _host.Outcomes.Statuses(declinedId).ShouldBe([OrderingOrderStatus.Declined]);
        _host.Outcomes.Statuses(cancelledId).ShouldBe([OrderingOrderStatus.Accepted, OrderingOrderStatus.Cancelled]);
        _host.Outcomes.Statuses(await OrderIdAsync(shop, customerCancelled)).ShouldBe([OrderingOrderStatus.Cancelled]);
    }

    [Fact]
    public async Task A_stale_version_or_a_step_its_status_does_not_allow_is_409_with_the_order_as_it_is()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var id = await OrderIdAsync(shop, await SubmitAsync(slug));

        var notYet = await ProblemAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/ready", staff, new { version = 1 }), HttpStatusCode.Conflict, "action-not-allowed");
        (notYet["order"]!["status"]!.GetValue<string>(), notYet["order"]!["version"]!.GetValue<int>()).ShouldBe(("pending", 1));
        await ProblemAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/cancel", staff, new { version = 1, reason = "customer-request" }),
            HttpStatusCode.Conflict, "action-not-allowed");

        await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/decline", staff, new { version = 1, reason = "too-busy" }));

        var stale = await ProblemAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/accept", staff, new { version = 1 }), HttpStatusCode.Conflict, "order-changed");
        (stale["order"]!["status"]!.GetValue<string>(), stale["order"]!["version"]!.GetValue<int>()).ShouldBe(("declined", 2));
        stale["order"]!["customerName"]!.GetValue<string>().ShouldBe("陳小姐");
        await ProblemAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/accept", staff, new { version = 2 }), HttpStatusCode.Conflict, "action-not-allowed");
        await ProblemAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/complete", staff, new { version = 2 }), HttpStatusCode.Conflict, "action-not-allowed");

        (await StatusOfAsync(shop, id)).ShouldBe((OrderingOrderStatus.Declined, 2));
    }

    [Fact]
    public async Task Missing_fields_a_note_for_other_and_a_promised_time_out_of_range_are_422()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var id = await OrderIdAsync(shop, await SubmitAsync(slug));
        var path = $"/api/v1/ordering/orders/{id}";

        var errors = (await ProblemlessAsync(await SendAsync(HttpMethod.Post, $"{path}/decline", staff, new { reason = (string?)null })))["errors"]!.AsObject();
        errors.Select(error => error.Key).Order(StringComparer.Ordinal).ShouldBe(["reason", "version"]);
        (await ProblemlessAsync(await SendAsync(HttpMethod.Post, $"{path}/decline", staff, new { version = 1, reason = "other", note = " " })))["errors"]!
            .AsObject().Select(error => error.Key).ShouldBe(["note"]);
        (await ProblemlessAsync(await SendAsync(HttpMethod.Post, $"{path}/decline", staff, new { version = 1, reason = "closed", note = new string('字', 101) })))["errors"]!
            .AsObject().Select(error => error.Key).ShouldBe(["note"]);
        (await ProblemlessAsync(await SendAsync(HttpMethod.Post, $"{path}/accept", staff, new { })))["errors"]!.AsObject().Select(error => error.Key).ShouldBe(["version"]);
        var earlier = new DateTimeOffset(2026, 11, 17, 12, 15, 0, TimeSpan.FromHours(8));
        (await ProblemlessAsync(await SendAsync(HttpMethod.Post, $"{path}/accept", staff, new { version = 1, promisedAt = earlier })))["errors"]!
            .AsObject().Select(error => error.Key).ShouldBe(["promisedAt"]);
        (await ProblemlessAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/orders?view=everything", staff)))["errors"]!
            .AsObject().Select(error => error.Key).ShouldBe(["view"]);
        (await SendAsync(HttpMethod.Post, $"{path}/decline", staff, new { version = 1, reason = "bored" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await StatusOfAsync(shop, id)).ShouldBe((OrderingOrderStatus.Pending, 1));
    }

    // --- At the same moment ----------------------------------------------------------------------------------

    [Fact]
    public async Task Two_members_accepting_at_once_exactly_one_wins_and_the_outcome_runs_once()
    {
        var (shop, slug) = await CreateShopAsync();
        var first = await SignInAsync(shop, "first");
        var second = await SignInAsync(shop, "second");
        var ids = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            ids.Add(await OrderIdAsync(shop, await SubmitAsync(slug)));
        }

        _host.Outcomes.Clear();
        foreach (var id in ids)
        {
            var answers = await Task.WhenAll(
                SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/accept", first, new { version = 1 }),
                SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/accept", second, new { version = 1 }));

            answers.Count(answer => answer.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            var loser = answers.Single(answer => answer.StatusCode != HttpStatusCode.OK);
            var problem = await ProblemAsync(loser, HttpStatusCode.Conflict, "order-changed");
            (problem["order"]!["status"]!.GetValue<string>(), problem["order"]!["version"]!.GetValue<int>()).ShouldBe(("accepted", 2));
            (await StatusOfAsync(shop, id)).ShouldBe((OrderingOrderStatus.Accepted, 2));
            _host.Outcomes.Statuses(id).ShouldBe([OrderingOrderStatus.Accepted]);
        }
    }

    [Fact]
    public async Task The_shop_answering_and_the_customer_cancelling_at_once_exactly_one_wins()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        _host.Outcomes.Clear();

        foreach (var decline in new[] { false, true, false, true })
        {
            var submitted = await SubmitAsync(slug);
            var id = await OrderIdAsync(shop, submitted);
            object body = decline ? new { version = 1, reason = "too-busy" } : new { version = 1 };
            var answers = await Task.WhenAll(
                SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/{(decline ? "decline" : "accept")}", staff, body),
                CancelByCustomerAsync(slug, submitted["token"]!.GetValue<string>()));

            var shopWon = answers[0].StatusCode == HttpStatusCode.OK;
            var customerWon = answers[1].StatusCode == HttpStatusCode.OK;
            (shopWon ^ customerWon).ShouldBeTrue($"shop {answers[0].StatusCode}, customer {answers[1].StatusCode}");
            if (shopWon)
            {
                await ProblemAsync(answers[1], HttpStatusCode.Conflict, "not-cancellable");
            }
            else
            {
                await ProblemAsync(answers[0], HttpStatusCode.Conflict, "order-changed");
            }

            var expected = customerWon ? OrderingOrderStatus.Cancelled : decline ? OrderingOrderStatus.Declined : OrderingOrderStatus.Accepted;
            (await StatusOfAsync(shop, id)).ShouldBe((expected, 2));
            _host.Outcomes.Statuses(id).ShouldBe([expected]);
        }
    }

    // --- Lists and summary -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_summary_counts_pending_and_active_orders_and_moves_its_cursors_with_new_orders_and_changes()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");

        var empty = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff));
        (empty["pendingCount"]!.GetValue<int>(), empty["activeCount"]!.GetValue<int>(), empty["latestOrderAt"], empty["changedAt"])
            .ShouldBe((0, 0, null, null));
        (empty["businessDate"]!.GetValue<string>(), empty["paused"]!.GetValue<bool>(), empty["asapBand"]!.GetValue<string>(), empty["menuVersion"]!.GetValue<int>())
            .ShouldBe(("2026-11-17", false, "15-30", 1));
        empty["asap"]!.ToJsonString().ShouldBe("""{"minMinutes":15,"maxMinutes":30}""");

        var firstId = await OrderIdAsync(shop, await SubmitAsync(slug));
        var one = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff));
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await SubmitAsync(slug);
        var two = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff));
        two["pendingCount"]!.GetValue<int>().ShouldBe(2);
        (Instant(two["latestOrderAt"]) - Instant(one["latestOrderAt"])).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(1), "a new order");

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{firstId}/accept", staff, new { version = 1 }));
        var after = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff));
        (after["pendingCount"]!.GetValue<int>(), after["activeCount"]!.GetValue<int>()).ShouldBe((1, 1));
        Instant(after["latestOrderAt"]).ShouldBe(Instant(two["latestOrderAt"]), "no new order");
        (Instant(after["changedAt"]) - Instant(two["changedAt"])).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(1), "but a change");
    }

    [Fact]
    public async Task Done_lists_the_business_days_closed_orders_and_those_closed_since_it_began()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var today = await OrderIdAsync(shop, await SubmitAsync(slug));
        var tomorrow = await OrderIdAsync(shop, await SubmitAsync(slug, slot: "2026-11-18T11:00"));
        var open = await OrderIdAsync(shop, await SubmitAsync(slug));
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{today}/decline", staff, new { version = 1, reason = "too-busy" }));
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{tomorrow}/decline", staff, new { version = 1, reason = "closed" }));

        var done = (await ListAsync(staff, "done"))["orders"]!.AsArray().Select(order => order!["id"]!.GetValue<string>()).ToList();
        done.ShouldBe([tomorrow.ToString(), today.ToString()], ignoreOrder: true);
        done.ShouldNotContain(open.ToString());

        // The next day: yesterday's closed orders are gone; tomorrow's (now today's) decline stays.
        _host.Clock.Advance(TimeSpan.FromDays(1));
        staff = await SignInAsync(shop, "next-day");
        (await ListAsync(staff, "done"))["orders"]!.AsArray().Select(order => order!["id"]!.GetValue<string>()).ShouldBe([tomorrow.ToString()]);
        (await ListAsync(staff, "pending"))["orders"]!.AsArray().Select(order => order!["id"]!.GetValue<string>()).ShouldBe([open.ToString()]);
    }

    // --- The shop's switches ---------------------------------------------------------------------------------

    [Fact]
    public async Task Pausing_stops_submissions_and_the_asap_band_changes_the_estimate_each_raising_the_shop_version_once()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var before = (await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff)))["shopVersion"]!.GetValue<int>();

        var paused = await JsonAsync(await SendAsync(HttpMethod.Put, "/api/v1/ordering/pause", staff, new { paused = true }));
        (paused["paused"]!.GetValue<bool>(), paused["shopVersion"]!.GetValue<int>()).ShouldBe((true, before + 1));
        (await JsonAsync(await SendAsync(HttpMethod.Put, "/api/v1/ordering/pause", staff, new { paused = true })))["shopVersion"]!.GetValue<int>()
            .ShouldBe(before + 1, "already paused: nothing changes");
        (await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}", CancellationToken)))["paused"]!.GetValue<bool>().ShouldBeTrue();
        await ProblemAsync(await PostOrderAsync(slug, Order()), HttpStatusCode.Conflict, "shop-paused");

        await JsonAsync(await SendAsync(HttpMethod.Put, "/api/v1/ordering/pause", staff, new { paused = false }));
        await SubmitAsync(slug);

        var band = await JsonAsync(await SendAsync(HttpMethod.Put, "/api/v1/ordering/asap-band", staff, new { band = "45-60" }));
        (band["asapBand"]!.GetValue<string>(), band["shopVersion"]!.GetValue<int>()).ShouldBe(("45-60", before + 3));
        band["asap"]!.ToJsonString().ShouldBe("""{"minMinutes":45,"maxMinutes":60}""");
        (await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}", CancellationToken)))["asap"]!.ToJsonString()
            .ShouldBe("""{"minMinutes":45,"maxMinutes":60}""");
        (await ProblemlessAsync(await SendAsync(HttpMethod.Put, "/api/v1/ordering/asap-band", staff, new { band = (string?)null })))["errors"]!
            .AsObject().Select(error => error.Key).ShouldBe(["band"]);
        (await ProblemlessAsync(await SendAsync(HttpMethod.Put, "/api/v1/ordering/pause", staff, new { })))["errors"]!
            .AsObject().Select(error => error.Key).ShouldBe(["paused"]);
    }

    [Fact]
    public async Task Item_states_show_on_the_public_menu_and_stop_submissions_and_sold_out_today_comes_back_the_next_business_day()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var version = (await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff)))["shopVersion"]!.GetValue<int>();

        var items = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/items", staff));
        items["menuVersion"]!.GetValue<int>().ShouldBe(1);
        items["categories"]![0]!["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ShouldContain("beef-noodle");
        items["categories"]!.AsArray().SelectMany(category => category!["items"]!.AsArray())
            .ShouldAllBe(item => item!["availability"]!.GetValue<string>() == "available");

        var soldOut = await JsonAsync(await SendAsync(HttpMethod.Put, "/api/v1/ordering/items/beef-noodle/state", staff, new { state = "sold-out-today" }));
        soldOut["availability"]!.GetValue<string>().ShouldBe("sold-out-today");
        var tomorrowOpening = new DateTimeOffset(2026, 11, 18, 11, 0, 0, TimeSpan.FromHours(8));
        Instant(soldOut["resetsAt"]).ShouldBe(tomorrowOpening);
        (await JsonAsync(await SendAsync(HttpMethod.Put, "/api/v1/ordering/items/beef-noodle/state", staff, new { state = "sold-out-today" })))["resetsAt"]
            .ShouldNotBeNull();
        (await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff)))["shopVersion"]!.GetValue<int>().ShouldBe(version + 1);

        var beef = PublicItem(await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/menu", CancellationToken)), "beef-noodle");
        beef["state"]!["kind"]!.GetValue<string>().ShouldBe("sold-out-today");
        var refused = await ProblemAsync(await PostOrderAsync(slug, Order()), HttpStatusCode.Conflict, "items-unavailable");
        refused["lineErrors"]![0]!["code"]!.GetValue<string>().ShouldBe("sold-out-today");
        // Tomorrow's pickup is after it comes back.
        await SubmitAsync(slug, slot: "2026-11-18T11:00");

        await JsonAsync(await SendAsync(HttpMethod.Put, "/api/v1/ordering/items/wonton-soup/state", staff, new { state = "unavailable" }));
        var wontonRefused = await ProblemAsync(
            await PostOrderAsync(slug, Order(slot: "2026-11-18T11:30", item: "wonton-soup")), HttpStatusCode.Conflict, "items-unavailable");
        wontonRefused["lineErrors"]![0]!["code"]!.GetValue<string>().ShouldBe("unavailable");
        PublicItem(await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/menu", CancellationToken)), "wonton-soup")["state"]!["kind"]!
            .GetValue<string>().ShouldBe("unavailable");

        await JsonAsync(await SendAsync(HttpMethod.Put, "/api/v1/ordering/items/wonton-soup/state", staff, new { state = "available" }));
        PublicItem(await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/menu", CancellationToken)), "wonton-soup")["state"].ShouldBeNull();
        await SubmitAsync(slug, item: "wonton-soup");

        await ProblemAsync(
            await SendAsync(HttpMethod.Put, "/api/v1/ordering/items/no-such-item/state", staff, new { state = "unavailable" }), HttpStatusCode.NotFound, "item-not-found");

        // The next business day's ordering hours open: the sold-out item is back by itself.
        _host.Clock.Advance(tomorrowOpening - Now);
        staff = await SignInAsync(shop, "next-day");
        var back = (await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/items", staff)))["categories"]!.AsArray()
            .SelectMany(category => category!["items"]!.AsArray()).Single(item => item!["id"]!.GetValue<string>() == "beef-noodle")!;
        (back["availability"]!.GetValue<string>(), back["resetsAt"]).ShouldBe(("available", null));
        PublicItem(await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/menu", CancellationToken)), "beef-noodle")["state"].ShouldBeNull();
        await SubmitAsync(slug, slot: null);
    }

    [Fact]
    public async Task Staff_actions_never_log_the_customers_details()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var id = await OrderIdAsync(shop, await SubmitAsync(slug, comment: "放門口的祕密備註"));
        var phone = Phone(_phones);

        _host.Log.Start();
        try
        {
            await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/orders?view=pending", staff));
            await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{id}/decline", staff, new { version = 1, reason = "other", note = "給顧客的說明" }));
        }
        finally
        {
            _host.Log.Stop();
        }

        var lines = _host.Log.Lines;
        lines.ShouldContain(line => line.Contains(id.ToString(), StringComparison.Ordinal) && line.Contains("decline", StringComparison.Ordinal));
        foreach (var secret in new[] { "陳小姐", phone, phone[^6..], "chen@example.com", "放門口的祕密備註", "麵硬一點", "給顧客的說明" })
        {
            lines.ShouldAllBe(line => !line.Contains(secret, StringComparison.Ordinal), secret);
        }
    }

    // --- Helpers ---------------------------------------------------------------------------------------------

    private sealed record Staff(string Token, Guid AccountId);

    private static string NewSlug() => "shop-" + Guid.NewGuid().ToString("N")[..10];

    private static string Phone(int index) => "0911" + index.ToString("000000", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(Organization Shop, string Slug)> CreateShopAsync()
    {
        var shop = await _host.CreateOrganizationAsync("王記麵店");
        var slug = NewSlug();
        var tree = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "deploy", "ordering-shop.example.json")))!.AsObject();
        tree["shop"]!["slug"] = slug;
        tree["shop"]!["maxOrdersPerSlot"] = null;
        var hours = new JsonObject();
        foreach (var day in new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" })
        {
            hours[day] = JsonNode.Parse("""[{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }]""");
        }

        tree["shop"]!["orderingHours"] = hours;
        var parsed = OrderingShopJson.Parse(tree.ToJsonString());
        parsed.Errors.ShouldBeEmpty();
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<OrderingShops>().ImportAsync(shop.Id, parsed.Import!, CancellationToken)).Outcome
            .ShouldBe(OrderingImportOutcome.Created);
        return (shop, slug);
    }

    private async Task<Staff> SignInAsync(
        Organization organization, string loginName, AccountRole role = AccountRole.InternalEmployee, AccountPermission permission = AccountPermission.ManageOrders,
        string? displayName = null)
    {
        var account = await _host.CreateAccountAsync(organization, loginName, Password, role, displayName, permission);
        using var spa = _host.CreateSpaClient();
        var token = await spa.SignInAsync(organization.Code, loginName, Password);
        return new Staff(token.AccessToken, account.Id);
    }

    /// <summary>An order of one <paramref name="item"/> for the scheduled <paramref name="slot"/> (ASAP when
    /// <see langword="null"/>), from a new phone number.</summary>
    private static JsonObject Order(string? slot = TodayAt1230, string item = "beef-noodle", string? comment = null)
    {
        var phone = Phone(Interlocked.Increment(ref _phones));
        JsonNode line = item == "beef-noodle"
            ? JsonNode.Parse("""
                {"itemId":"beef-noodle","quantity":1,"comment":"麵硬一點","selections":[
                  {"groupId":"size","optionId":"large","count":1,"selections":null},
                  {"groupId":"noodle","optionId":"wide","count":1,"selections":null},
                  {"groupId":"toppings","optionId":"egg","count":2,"selections":null},
                  {"groupId":"combo","optionId":"set","count":1,"selections":[
                    {"groupId":"side","optionId":"cucumber","count":1,"selections":null},
                    {"groupId":"drink","optionId":"winter-melon","count":1,"selections":null}]}]}
                """)!
            : new JsonObject { ["itemId"] = item, ["quantity"] = 1, ["selections"] = null, ["comment"] = null };
        return new JsonObject
        {
            ["serviceType"] = slot is null ? "takeout-asap" : "takeout-scheduled",
            ["pickupDate"] = slot?[..10],
            ["pickupSlot"] = slot,
            ["menuVersion"] = 1,
            ["lines"] = new JsonArray(line),
            ["customer"] = new JsonObject { ["name"] = "陳小姐", ["phone"] = phone, ["email"] = "chen@example.com" },
            ["orderComment"] = comment,
            ["tableware"] = null,
            ["submissionKey"] = Guid.NewGuid().ToString("D"),
        };
    }

    private Task<HttpResponseMessage> PostOrderAsync(string slug, JsonNode body) =>
        _client.PostAsync(
            $"/api/v1/public/ordering/{slug}/orders",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            CancellationToken);

    private async Task<JsonNode> SubmitAsync(
        string slug, string? slot = TodayAt1230, string item = "beef-noodle", string? comment = null) =>
        await JsonAsync(await PostOrderAsync(slug, Order(slot, item, comment)), HttpStatusCode.Created);

    private async Task<Guid> OrderIdAsync(Organization shop, JsonNode submitted)
    {
        var hash = OrderingOrder.HashStatusToken(submitted["token"]!.GetValue<string>());
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.OrderingOrders.Where(order => order.StatusTokenHash == hash).Select(order => order.Id).SingleAsync(CancellationToken);
    }

    private async Task<(OrderingOrderStatus Status, int Version)> StatusOfAsync(Organization shop, Guid id)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        var order = await dbContext.OrderingOrders.AsNoTracking().SingleAsync(candidate => candidate.Id == id, CancellationToken);
        return (order.Status, order.Version);
    }

    private async Task<JsonNode> ListAsync(Staff staff, string view) =>
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/ordering/orders?view={view}", staff));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Staff? staff, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (staff is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staff.Token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private Task<HttpResponseMessage> GetOrderAsync(string slug, string token) =>
        SendWithTokenAsync(HttpMethod.Get, $"/api/v1/public/ordering/{slug}/orders/current", token);

    private Task<HttpResponseMessage> CancelByCustomerAsync(string slug, string token) =>
        SendWithTokenAsync(HttpMethod.Post, $"/api/v1/public/ordering/{slug}/orders/current/cancel", token);

    private async Task<HttpResponseMessage> SendWithTokenAsync(HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(OrderingPublicEndpoints.TokenHeader, token);
        return await _client.SendAsync(request, CancellationToken);
    }

    private static JsonNode PublicItem(JsonNode menu, string itemId) =>
        menu["categories"]!.AsArray().SelectMany(category => category!["items"]!.AsArray()).Single(item => item!["id"]!.GetValue<string>() == itemId)!;

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

    /// <summary>A <c>422</c> validation problem (no <c>reason</c>).</summary>
    private static async Task<JsonNode> ProblemlessAsync(HttpResponseMessage response) =>
        await JsonAsync(response, HttpStatusCode.UnprocessableEntity);

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

/// <summary>Records every <see cref="IOrderingOrderOutcomes.Decided"/> call: which order, in which status.</summary>
public sealed class RecordingOrderOutcomes : IOrderingOrderOutcomes
{
    private readonly ConcurrentQueue<(Guid OrderId, OrderingOrderStatus Status)> _calls = new();

    public void Decided(AppDbContext dbContext, OrderingOrder order, DateTimeOffset now) => _calls.Enqueue((order.Id, order.Status));

    public IReadOnlyList<OrderingOrderStatus> Statuses(Guid orderId) => [.. _calls.Where(call => call.OrderId == orderId).Select(call => call.Status)];

    public void Clear() => _calls.Clear();
}

public sealed class OrderingStaffHostFixture : AuthHostFixture
{
    public CoordinatorTestLog Log { get; } = new();

    public RecordingOrderOutcomes Outcomes { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(Log);
        services.Configure<LoggerFilterOptions>(options =>
            options.Rules.Add(new LoggerFilterRule(typeof(CoordinatorTestLog).FullName, categoryName: null, LogLevel.Trace, filter: null)));
        services.AddScoped<IOrderingOrderOutcomes>(_ => Outcomes);
    }
}
