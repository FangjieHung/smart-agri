using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Ordering;
using SmartAgri.Api.Organizations;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Ordering;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Ordering;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Ordering;

/// <summary>
/// 內用掃碼 (online-ordering design D11, Slice O6, #402) through the real endpoints and PostgreSQL: a table's QR code token
/// in the <c>X-Ordering-Table</c> header only (never a path or query), stable across imports, dead once the table is removed
/// or regenerated, another shop's token unknown; dine-in orders with an optional 稱呼 joining the table's open session, the
/// per-table pending limit, 「自動接單」; 〔已結帳〕 refused while an order is pending and completing the rest; a close racing a
/// new order never leaving an order in a closed session; the <c>ordering-shop tables</c> command; the 90-day clearing.
/// </summary>
/// <remarks>The clock stands at Tuesday 2026-11-17 12:00 in Taipei; ordering (and so dine-in) every day 11:00–14:00 and
/// 17:00–21:00. The example file's five tables a1, a2, a3, window-1, window-2.</remarks>
[Trait("Category", TestCategories.Docker)]
public sealed class OrderingDineInTests : IClassFixture<OrderingFollowUpHostFixture>
{
    private const string Password = "Staff-Password-1!";

    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    private static readonly DateTimeOffset Noon = new(2026, 11, 17, 12, 0, 0, Taipei);

    private readonly OrderingFollowUpHostFixture _host;
    private readonly HttpClient _client;

    public OrderingDineInTests(OrderingFollowUpHostFixture host)
    {
        _host = host;
        _host.Clock.Advance(Noon - _host.Clock.GetUtcNow());
        _client = host.Factory.CreateClient();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_table_orders_twice_into_one_session_and_the_staff_see_it_merged()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var a1 = await TokenAsync(shop, "a1");

        var info = await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}", CancellationToken));
        info["services"]!["dineIn"]!.GetValue<bool>().ShouldBeTrue();
        ShouldBeJson(info["dineIn"]!["note"], "\"用餐後請至櫃台結帳，可用現金或 LINE Pay。\"");
        (info["dineIn"]!["canOrder"]!.GetValue<bool>(), info["dineIn"]!["autoAccept"]!.GetValue<bool>()).ShouldBe((true, false));
        var table = await JsonAsync(await TableAsync(slug, a1));
        ShouldBeJson(table, """{"name":"A1","canOrder":true,"session":null}""");

        var first = await JsonAsync(await PostAsync(slug, DineIn([Wonton(2)], name: "王先生", comment: "不要香菜"), a1), HttpStatusCode.Created);
        _host.Clock.Advance(TimeSpan.FromMinutes(10));
        var second = await JsonAsync(await PostAsync(slug, DineIn([Wonton()], name: null), a1), HttpStatusCode.Created);

        var order = first["order"]!;
        (order["serviceType"]!.GetValue<string>(), order["status"]!.GetValue<string>(), order["customerName"]!.GetValue<string>(),
                order["phoneLastDigits"]!.GetValue<string>(), order["autoAccepted"]!.GetValue<bool>(), order["total"]!.GetValue<int>())
            .ShouldBe(("dine-in", "pending", "王先生", string.Empty, false, 140));
        ShouldBeJson(order["table"], """{"name":"A1"}""");
        order["pickupAt"]!.GetValue<string>().ShouldStartWith("2026-11-17T04:00:00");
        (order["estimate"], order["delivery"], order["tableware"]).ShouldBe((null, null, null));
        second["order"]!["customerName"]!.GetValue<string>().ShouldBe(string.Empty);

        var orders = await OrdersAsync(shop);
        orders.Select(stored => stored.TableSessionId).Distinct().ShouldHaveSingleItem().ShouldNotBeNull();
        orders.Select(stored => (stored.Number, stored.TableId, stored.TableName, stored.CustomerPhone, stored.OrderComment))
            .ShouldBe([(1, "a1", "A1", string.Empty, "不要香菜"), (2, "a1", "A1", string.Empty, null)]);
        // The test clock ticks a little with every reading: compare to the second.
        (orders[0].ExpiresAt - orders[0].CreatedAt).ShouldBe(TimeSpan.FromMinutes(15), "a timeout after it was sent, not capped by its pickup (now)");
        orders[0].PickupAt.ShouldBe(orders[0].CreatedAt);
        (await JobsAsync(shop, OrderingExpireOrderJob.Kind)).Count.ShouldBe(2);

        table = await JsonAsync(await TableAsync(slug, a1));
        var session = table["session"]!;
        (session["orderCount"]!.GetValue<int>(), session["total"]!.GetValue<int>()).ShouldBe((2, 210));
        var latest = session["orders"]![1]!.AsObject();
        latest["createdAt"]!.GetValue<string>().ShouldStartWith("2026-11-17T04:10:");
        latest.Remove("createdAt");
        ShouldBeJson(latest, """{"displayNumber":"#002","status":"pending","total":70,"items":[{"name":"鮮肉餛飩湯","quantity":1}]}""");
        session.ToJsonString().ShouldNotContain("王先生");

        var tables = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/tables", staff));
        var staffSession = tables["sessions"]!.AsArray().ShouldHaveSingleItem()!;
        (staffSession["tableName"]!.GetValue<string>(), staffSession["total"]!.GetValue<int>(), staffSession["pendingCount"]!.GetValue<int>(),
                staffSession["version"]!.GetValue<int>())
            .ShouldBe(("A1", 210, 2, 2));
        staffSession["orders"]!.AsArray().Select(entry => entry!["displayNumber"]!.GetValue<string>()).ShouldBe(["#001", "#002"]);
        ShouldBeJson(staffSession["orders"]![0]!["table"], $$"""{"id":"a1","name":"A1","sessionId":"{{orders[0].TableSessionId}}"}""");
        var pending = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/orders?view=pending", staff));
        pending["orders"]!.AsArray().Count.ShouldBe(2);
        var summary = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff));
        (summary["openTableCount"]!.GetValue<int>(), summary["pendingCount"]!.GetValue<int>(), summary["services"]!["dineIn"]!.GetValue<bool>())
            .ShouldBe((1, 2, true));

        // 〔接單〕 takes no time for dine-in.
        var refused = await SendAsync(
            HttpMethod.Post, $"/api/v1/ordering/orders/{orders[0].Id}/accept", staff, new { version = 1, promisedAt = Noon.AddMinutes(40) });
        (await JsonAsync(refused, HttpStatusCode.UnprocessableEntity))["errors"]!["promisedAt"]![0]!.GetValue<string>().ShouldBe("內用訂單不需要指定時間。");
        var accepted = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{orders[0].Id}/accept", staff, new { version = 1 }));
        (accepted["status"]!.GetValue<string>(), accepted["autoAccepted"]!.GetValue<bool>()).ShouldBe(("accepted", false));
        accepted["accepted"]!["by"]!["id"]!.GetValue<Guid>().ShouldBe(staff.AccountId);
        (await EmailJobsAsync(shop)).ShouldBeEmpty("dine-in has no Email");
    }

    [Fact]
    public async Task Auto_accept_accepts_a_dine_in_order_as_it_is_sent_with_no_expiry()
    {
        var (shop, slug) = await CreateShopAsync(dineIn => dineIn["autoAccept"] = true);
        var staff = await SignInAsync(shop, "staff");

        var submitted = await JsonAsync(await PostAsync(slug, DineIn([Wonton()]), await TokenAsync(shop, "a2")), HttpStatusCode.Created);

        (submitted["order"]!["status"]!.GetValue<string>(), submitted["order"]!["autoAccepted"]!.GetValue<bool>(), submitted["order"]!["canCancel"]!.GetValue<bool>())
            .ShouldBe(("accepted", true, false));
        var stored = (await OrdersAsync(shop)).ShouldHaveSingleItem();
        (stored.Status, stored.AutoAccepted, stored.AcceptedByAccountId, stored.AcceptedAt, stored.PromisedAt, stored.Version)
            .ShouldBe((OrderingOrderStatus.Accepted, true, null, stored.CreatedAt, stored.CreatedAt, 2));
        (await JobsAsync(shop, OrderingExpireOrderJob.Kind)).ShouldBeEmpty();
        (await EmailJobsAsync(shop)).ShouldBeEmpty();
        var staffView = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/ordering/orders/{stored.Id}", staff));
        (staffView["autoAccepted"]!.GetValue<bool>(), staffView["accepted"]!["by"]).ShouldBe((true, null));
        staffView["actions"]!.AsArray().Select(action => action!.GetValue<string>()).ShouldBe(["ready", "complete", "cancel"]);
        var info = await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}", CancellationToken));
        info["dineIn"]!["autoAccept"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task The_table_token_counts_only_in_the_header_never_in_a_path_or_query()
    {
        var (shop, slug) = await CreateShopAsync();
        var token = await TokenAsync(shop, "a1");

        await ProblemAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/table?t={token}", CancellationToken), HttpStatusCode.NotFound, "table-not-found");
        await ProblemAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}/table?table={token}", CancellationToken), HttpStatusCode.NotFound, "table-not-found");
        (await _client.GetAsync($"/api/v1/public/ordering/{slug}/table/{token}", CancellationToken)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        await ProblemAsync(
            await _client.PostAsync(
                $"/api/v1/public/ordering/{slug}/orders?t={token}",
                new StringContent(DineIn([Wonton()]).ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                CancellationToken),
            HttpStatusCode.NotFound,
            "table-not-found");
        await ProblemAsync(await TableAsync(slug, "not-a-token"), HttpStatusCode.NotFound, "table-not-found");
        (await OrdersAsync(shop)).ShouldBeEmpty();

        (await TableAsync(slug, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(slug, DineIn([Wonton()]), token)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_removed_table_stops_working_its_id_added_back_gets_a_new_token_and_kept_tables_keep_theirs()
    {
        var (shop, slug) = await CreateShopAsync();
        var a1 = await TokenAsync(shop, "a1");
        var a2 = await TokenAsync(shop, "a2");

        await ImportAsync(shop, slug, tree => tree["shop"]!["dineIn"]!["tables"] = JsonNode.Parse("""[{ "id": "a1", "name": "吧台 1" }]"""));

        (await TokenAsync(shop, "a1")).ShouldBe(a1);
        ShouldBeJson((await JsonAsync(await TableAsync(slug, a1)))["name"], "\"吧台 1\"");
        await ProblemAsync(await TableAsync(slug, a2), HttpStatusCode.NotFound, "table-not-found");
        await ProblemAsync(await PostAsync(slug, DineIn([Wonton()]), a2), HttpStatusCode.NotFound, "table-not-found");

        await ImportAsync(shop, slug, tree => tree["shop"]!["dineIn"]!["tables"] = JsonNode.Parse("""[{ "id": "a1", "name": "A1" }, { "id": "a2", "name": "A2" }]"""));
        var again = await TokenAsync(shop, "a2");
        again.ShouldNotBe(a2);
        await ProblemAsync(await TableAsync(slug, a2), HttpStatusCode.NotFound, "table-not-found");
        (await TableAsync(slug, again)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TokenAsync(shop, "a1")).ShouldBe(a1);

        // Dine-in switched off: the tables are kept but their QR codes answer as unknown until it is on again.
        await ImportAsync(shop, slug, tree => tree["shop"]!["services"]!["dineIn"] = false);
        await ProblemAsync(await TableAsync(slug, a1), HttpStatusCode.NotFound, "table-not-found");
        await ProblemAsync(await PostAsync(slug, DineIn([Wonton()]), a1), HttpStatusCode.NotFound, "table-not-found");
        (await JsonAsync(await _client.GetAsync($"/api/v1/public/ordering/{slug}", CancellationToken)))["dineIn"].ShouldBeNull();
        (await TokenAsync(shop, "a1")).ShouldBe(a1);
    }

    [Fact]
    public async Task Another_shops_table_token_is_unknown_here_and_its_session_is_not_ours_to_close()
    {
        var (shop, slug) = await CreateShopAsync();
        var (other, otherSlug) = await CreateShopAsync();
        var otherToken = await TokenAsync(other, "a1");
        (await PostAsync(otherSlug, DineIn([Wonton()]), otherToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var otherSession = (await OrdersAsync(other)).Single().TableSessionId!.Value;

        await ProblemAsync(await TableAsync(slug, otherToken), HttpStatusCode.NotFound, "table-not-found");
        await ProblemAsync(await PostAsync(slug, DineIn([Wonton()]), otherToken), HttpStatusCode.NotFound, "table-not-found");

        var staff = await SignInAsync(shop, "staff");
        var forbidden = await SendAsync(HttpMethod.Post, $"/api/v1/ordering/tables/sessions/{otherSession}/close", staff, new { version = 1 });
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/tables", staff)))["sessions"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_table_has_at_most_its_limit_of_pending_orders_and_other_tables_are_not_held_up()
    {
        var (shop, slug) = await CreateShopAsync(dineIn => dineIn["maxPendingPerTable"] = 2);
        var a1 = await TokenAsync(shop, "a1");
        (await PostAsync(slug, DineIn([Wonton()]), a1)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostAsync(slug, DineIn([Wonton()]), a1)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var problem = await ProblemAsync(await PostAsync(slug, DineIn([Wonton()]), a1), HttpStatusCode.TooManyRequests, "too-many-pending");

        problem["message"]!.GetValue<string>().ShouldBe("這桌已經有 2 筆等待店家確認的訂單，請等店家確認後再加點，或洽店員。");
        (await PostAsync(slug, DineIn([Wonton()]), await TokenAsync(shop, "a2"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        var staff = await SignInAsync(shop, "staff");
        var first = (await OrdersAsync(shop)).First();
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{first.Id}/accept", staff, new { version = 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(slug, DineIn([Wonton()]), a1)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Outside_the_dine_in_hours_or_paused_no_dine_in_order_is_taken()
    {
        var (shop, slug) = await CreateShopAsync();
        var a1 = await TokenAsync(shop, "a1");
        _host.Clock.Advance(TimeSpan.FromHours(3));

        (await JsonAsync(await TableAsync(slug, a1)))["canOrder"]!.GetValue<bool>().ShouldBeFalse();
        var closed = await ProblemAsync(await PostAsync(slug, DineIn([Wonton()]), a1), HttpStatusCode.Conflict, "service-unavailable");
        closed["message"]!.GetValue<string>().ShouldBe("現在不是內用點餐的時間，請洽店員。");

        _host.Clock.Advance(TimeSpan.FromHours(3));
        var staff = await SignInAsync(shop, "staff");
        (await SendAsync(HttpMethod.Put, "/api/v1/ordering/pause", staff, new { paused = true })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ProblemAsync(await PostAsync(slug, DineIn([Wonton()]), a1), HttpStatusCode.Conflict, "shop-paused");
        (await OrdersAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Paid_closes_the_session_once_nothing_is_pending_and_the_same_qr_code_starts_a_new_one()
    {
        var (shop, slug) = await CreateShopAsync();
        var staff = await SignInAsync(shop, "staff");
        var a1 = await TokenAsync(shop, "a1");
        await JsonAsync(await PostAsync(slug, DineIn([Wonton(2)]), a1), HttpStatusCode.Created);
        await JsonAsync(await PostAsync(slug, DineIn([Wonton()]), a1), HttpStatusCode.Created);
        await JsonAsync(await PostAsync(slug, DineIn([Wonton()]), a1), HttpStatusCode.Created);
        // Another table's session is not touched.
        await JsonAsync(await PostAsync(slug, DineIn([Wonton()]), await TokenAsync(shop, "a2")), HttpStatusCode.Created);
        var orders = (await OrdersAsync(shop)).Where(order => order.TableId == "a1").ToList();
        var sessionId = orders[0].TableSessionId!.Value;
        var before = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff));

        var pending = await JsonAsync(await CloseAsync(staff, sessionId, 3), HttpStatusCode.Conflict);
        (pending["reason"]!.GetValue<string>(), pending["session"]!["pendingCount"]!.GetValue<int>()).ShouldBe(("table-has-pending", 3));

        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        await AcceptAsync(staff, orders[0].Id, 1);
        await AcceptAsync(staff, orders[1].Id, 1);
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{orders[1].Id}/ready", staff, new { version = 2 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{orders[2].Id}/decline", staff, new { version = 1, reason = "sold-out" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var stale = await JsonAsync(await CloseAsync(staff, sessionId, 1), HttpStatusCode.Conflict);
        (stale["reason"]!.GetValue<string>(), stale["session"]!["version"]!.GetValue<int>(), stale["session"]!["total"]!.GetValue<int>())
            .ShouldBe(("session-changed", 3, 210));

        // Paid after the meal (and after the first member's 30-minute sign-in ran out).
        _host.Clock.Advance(TimeSpan.FromMinutes(30));
        staff = await SignInAsync(shop, "cashier");
        var closed = await JsonAsync(await CloseAsync(staff, sessionId, 3));

        closed["closedAt"]!.GetValue<string>().ShouldStartWith("2026-11-17T04:35:");
        (closed["closedBy"]!["id"]!.GetValue<Guid>(), closed["total"]!.GetValue<int>()).ShouldBe((staff.AccountId, 210));
        closed["orders"]!.AsArray().Select(order => order!["status"]!.GetValue<string>()).ShouldBe(["completed", "completed", "declined"]);
        (await OrdersAsync(shop)).Where(order => order.TableId == "a1").Select(order => order.CompletedByAccountId)
            .ShouldBe([staff.AccountId, staff.AccountId, null]);
        var after = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/summary", staff));
        after["openTableCount"]!.GetValue<int>().ShouldBe(1, "a2 is still open");
        after["changedAt"]!.GetValue<DateTimeOffset>().ShouldBeGreaterThan(before["changedAt"]!.GetValue<DateTimeOffset>());
        (await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/ordering/tables", staff)))["sessions"]!.AsArray()
            .Select(session => session!["tableId"]!.GetValue<string>()).ShouldBe(["a2"]);
        (await JsonAsync(await TableAsync(slug, a1)))["session"].ShouldBeNull();
        (await JsonAsync(await CloseAsync(staff, sessionId, 4), HttpStatusCode.Conflict))["reason"]!.GetValue<string>().ShouldBe("session-changed");

        // The same QR code starts a new session.
        await JsonAsync(await PostAsync(slug, DineIn([Wonton()]), a1), HttpStatusCode.Created);
        var newest = (await OrdersAsync(shop)).Last();
        newest.TableSessionId.ShouldNotBe(sessionId);
        (await JsonAsync(await TableAsync(slug, a1)))["session"]!["orderCount"]!.GetValue<int>().ShouldBe(1);
        (await CloseAsync(staff, Guid.CreateVersion7(), 1)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/tables/sessions/{sessionId}/close", staff, new { })).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_close_racing_a_new_order_never_leaves_an_order_in_a_closed_session()
    {
        var (shop, slug) = await CreateShopAsync(dineIn => dineIn["autoAccept"] = true);
        var staff = await SignInAsync(shop, "staff");
        foreach (var id in new[] { "a1", "a2", "a3", "window-1", "window-2" })
        {
            var token = await TokenAsync(shop, id);
            await JsonAsync(await PostAsync(slug, DineIn([Wonton()]), token), HttpStatusCode.Created);
            var session = (await OrdersAsync(shop)).Last().TableSessionId!.Value;

            var close = CloseAsync(staff, session, 1);
            var order = PostAsync(slug, DineIn([Wonton()]), token);
            await Task.WhenAll(close, order);

            (await order).StatusCode.ShouldBe(HttpStatusCode.Created, "a new order is never lost");
            var closeStatus = (await close).StatusCode;
            closeStatus.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
            var mine = (await OrdersAsync(shop)).Where(stored => stored.TableId == id).ToList();
            mine.Count.ShouldBe(2);
            if (closeStatus == HttpStatusCode.OK)
            {
                mine[0].Status.ShouldBe(OrderingOrderStatus.Completed);
                mine[1].TableSessionId.ShouldNotBe(session, "the order after the close opened a new session");
                mine[1].Status.ShouldBe(OrderingOrderStatus.Accepted);
            }
            else
            {
                mine.ShouldAllBe(stored => stored.TableSessionId == session && stored.Status == OrderingOrderStatus.Accepted);
            }
        }

        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        var closedSessions = await dbContext.OrderingTableSessions.AsNoTracking().Where(session => session.ClosedAt != null)
            .Select(session => session.Id).ToListAsync(CancellationToken);
        (await dbContext.OrderingOrders.AsNoTracking()
                .Where(order => order.TableSessionId != null && closedSessions.Contains(order.TableSessionId.Value))
                .AllAsync(order => order.Status == OrderingOrderStatus.Completed, CancellationToken))
            .ShouldBeTrue("every order of a closed session was paid");
        (await dbContext.OrderingTableSessions.AsNoTracking().CountAsync(session => session.ClosedAt == null, CancellationToken)).ShouldBe(5);
    }

    [Fact]
    public async Task The_tables_command_prints_the_urls_regenerates_a_token_and_writes_the_printable_qr_codes()
    {
        var (shop, slug) = await CreateShopAsync();
        await ImportAsync(shop, slug, tree => tree["shop"]!["dineIn"]!["tables"] = JsonNode.Parse(
            """[{ "id": "a1", "name": "A1" }, { "id": "a2", "name": "A2" }, { "id": "a3", "name": "A3" }, { "id": "x1", "name": "<b>吧台</b>" }]"""));
        await ImportAsync(shop, slug, tree => tree["shop"]!["dineIn"]!["tables"] = JsonNode.Parse(
            """[{ "id": "a1", "name": "A1" }, { "id": "a2", "name": "A2" }, { "id": "x1", "name": "<b>吧台</b>" }]"""));
        var a1 = await TokenAsync(shop, "a1");
        var removed = await TokenOfInactiveAsync(shop, "a3");

        var listed = await RunAsync("tables", "--organization", shop.Code);

        listed.Exit.ShouldBe(OrderingShopCommand.ExitSuccess, listed.Error);
        listed.Output.ShouldStartWith($"組織 {shop.Code}（王記麵店）的內用桌號（3 桌）：\n  A1（a1）：{OrderingFollowUpHostFixture.PublicBaseUrl}/order/{slug}/table#{a1}\n");
        listed.Output.ShouldNotContain(removed);

        var regenerated = await RunAsync("tables", "--organization", shop.Code, "--regenerate", "a1");
        regenerated.Exit.ShouldBe(OrderingShopCommand.ExitSuccess, regenerated.Error);
        regenerated.Output.ShouldStartWith("已重新產生桌號 A1（a1）的 QR code，舊的 QR code 已失效，請重新列印。\n");
        var fresh = await TokenAsync(shop, "a1");
        fresh.ShouldNotBe(a1);
        regenerated.Output.ShouldContain($"/order/{slug}/table#{fresh}");
        await ProblemAsync(await TableAsync(slug, a1), HttpStatusCode.NotFound, "table-not-found");
        (await TableAsync(slug, fresh)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RunAsync("tables", "--organization", shop.Code, "--regenerate", "a3")).Exit.ShouldBe(OrderingShopCommand.ExitFailed);

        var path = Path.Combine(Path.GetTempPath(), $"tables-{Guid.NewGuid():N}.html");
        try
        {
            var printed = await RunAsync("tables", "--organization", shop.Code, "--html", path);
            printed.Exit.ShouldBe(OrderingShopCommand.ExitSuccess, printed.Error);
            var html = await File.ReadAllTextAsync(path, CancellationToken);
            html.ShouldStartWith("<!doctype html>");
            System.Text.RegularExpressions.Regex.Count(html, "<svg ").ShouldBe(3, "one QR code per active table");
            html.ShouldContain("桌號 A1");
            html.ShouldContain("桌號 &lt;b&gt;吧台&lt;/b&gt;");
            html.ShouldNotContain("桌號 A3");
            html.ShouldNotContain("<script");
            html.ShouldContain(OrderingTablePrint.Instruction);
            html.ShouldContain("@page { size: A4;");
        }
        finally
        {
            File.Delete(path);
        }

        (await RunAsync("tables", "--organization", shop.Code, "--file", "x.json")).Exit.ShouldBe(OrderingShopCommand.ExitUsage);
        var none = await _host.CreateOrganizationAsync("沒有點餐的店");
        (await RunAsync("tables", "--organization", none.Code)).Exit.ShouldBe(OrderingShopCommand.ExitFailed);
    }

    [Fact]
    public async Task A_dine_in_name_and_comments_are_cleared_after_ninety_days_and_the_table_stays()
    {
        var (shop, slug) = await CreateShopAsync();
        await JsonAsync(await PostAsync(slug, DineIn([Wonton()], name: "王先生", comment: "不要香菜", lineComment: "湯多"), await TokenAsync(shop, "a1")), HttpStatusCode.Created);

        (await ClearAsync(shop, Noon.AddDays(90).AddMinutes(-1))).ShouldBe(0);
        (await ClearAsync(shop, Noon.AddDays(90).AddMinutes(1))).ShouldBe(1);

        var cleared = (await OrdersAsync(shop)).Single();
        (cleared.CustomerName, cleared.CustomerPhone, cleared.OrderComment, cleared.Lines[0].Comment).ShouldBe((string.Empty, string.Empty, null, null));
        (cleared.TableId, cleared.TableName, cleared.Total).ShouldBe(("a1", "A1", 70));
    }

    // --- Helpers ---------------------------------------------------------------------------------------------

    private sealed record Staff(string Token, Guid AccountId);

    /// <summary>Compares JSON by content: <see cref="JsonNode.ToJsonString"/> escapes Chinese as <c>\uXXXX</c>.</summary>
    private static void ShouldBeJson(JsonNode? actual, string expected) =>
        JsonNode.DeepEquals(actual, JsonNode.Parse(expected)).ShouldBeTrue(actual?.ToJsonString());

    private static JsonObject DineIn(IEnumerable<JsonNode> lines, string? name = null, string? comment = null, string? lineComment = null)
    {
        var lineArray = new JsonArray([.. lines]);
        if (lineComment is not null)
        {
            lineArray[0]!["comment"] = lineComment;
        }

        return new JsonObject
        {
            ["serviceType"] = "dine-in",
            ["menuVersion"] = 1,
            ["lines"] = lineArray,
            ["customer"] = new JsonObject { ["name"] = name, ["phone"] = null, ["email"] = null },
            ["orderComment"] = comment,
            ["tableware"] = true,
            ["submissionKey"] = Guid.NewGuid().ToString("D"),
        };
    }

    private static JsonNode Wonton(int quantity = 1) =>
        new JsonObject { ["itemId"] = "wonton-soup", ["quantity"] = quantity, ["selections"] = null, ["comment"] = null };

    private static string ShopFile(string slug, Action<JsonObject>? change = null)
    {
        var tree = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "deploy", "ordering-shop.example.json")))!.AsObject();
        tree["shop"]!["slug"] = slug;
        var hours = new JsonObject();
        foreach (var day in new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" })
        {
            hours[day] = JsonNode.Parse("""[{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }]""");
        }

        tree["shop"]!["orderingHours"] = hours;
        change?.Invoke(tree);
        return tree.ToJsonString();
    }

    private async Task<(Organization Shop, string Slug)> CreateShopAsync(Action<JsonObject>? dineIn = null)
    {
        var shop = await _host.CreateOrganizationAsync("王記麵店");
        var slug = "shop-" + Guid.NewGuid().ToString("N")[..10];
        await ImportFileAsync(shop, ShopFile(slug, tree => dineIn?.Invoke(tree["shop"]!["dineIn"]!.AsObject())));
        return (shop, slug);
    }

    private Task ImportAsync(Organization shop, string slug, Action<JsonObject> change) => ImportFileAsync(shop, ShopFile(slug, change));

    private async Task ImportFileAsync(Organization shop, string file)
    {
        var parsed = OrderingShopJson.Parse(file);
        parsed.Errors.ShouldBeEmpty();
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<OrderingShops>().ImportAsync(shop.Id, parsed.Import!, CancellationToken);
        result.Outcome.ShouldBeOneOf(OrderingImportOutcome.Created, OrderingImportOutcome.Updated);
    }

    /// <summary>The active table's token, read where the operator's command reads it.</summary>
    private async Task<string> TokenAsync(Organization shop, string tableId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return (await dbContext.OrderingTables.AsNoTracking().SingleAsync(table => table.TableId == tableId && table.DeactivatedAt == null, CancellationToken)).Token;
    }

    private async Task<string> TokenOfInactiveAsync(Organization shop, string tableId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return (await dbContext.OrderingTables.AsNoTracking().SingleAsync(table => table.TableId == tableId && table.DeactivatedAt != null, CancellationToken)).Token;
    }

    private async Task<List<OrderingOrder>> OrdersAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.OrderingOrders.AsNoTracking().OrderBy(order => order.Number).ToListAsync(CancellationToken);
    }

    private async Task<List<BackgroundJob>> JobsAsync(Organization shop, string kind)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.BackgroundJobs.AsNoTracking().Where(job => job.Kind == kind).ToListAsync(CancellationToken);
    }

    private Task<List<BackgroundJob>> EmailJobsAsync(Organization shop) => JobsAsync(shop, OrderingOrderEmailJob.Kind);

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

    private async Task AcceptAsync(Staff staff, Guid orderId, int version) =>
        (await SendAsync(HttpMethod.Post, $"/api/v1/ordering/orders/{orderId}/accept", staff, new { version })).StatusCode.ShouldBe(HttpStatusCode.OK);

    private Task<HttpResponseMessage> CloseAsync(Staff staff, Guid sessionId, int version) =>
        SendAsync(HttpMethod.Post, $"/api/v1/ordering/tables/sessions/{sessionId}/close", staff, new { version });

    private async Task<HttpResponseMessage> TableAsync(string slug, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/public/ordering/{slug}/table");
        request.Headers.Add(OrderingPublicEndpoints.TableHeader, token);
        return await _client.SendAsync(request, CancellationToken);
    }

    private async Task<HttpResponseMessage> PostAsync(string slug, JsonNode body, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/public/ordering/{slug}/orders")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(OrderingPublicEndpoints.TableHeader, token);
        return await _client.SendAsync(request, CancellationToken);
    }

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

    private async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await OrderingShopCommand.RunAsync(_host.Factory.Services, args, TextReader.Null, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
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
