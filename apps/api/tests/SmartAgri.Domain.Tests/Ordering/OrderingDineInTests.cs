using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Domain.Ordering;
using static SmartAgri.Domain.Tests.Ordering.OrderingSamples;

namespace SmartAgri.Domain.Tests.Ordering;

/// <summary>
/// 內用掃碼 (online-ordering design D11, Slice O6, #402): the import file's <c>dineIn</c> section and its rules; a table's QR
/// code token across imports (kept, deactivated, new when added back, regenerated); a table session opening, taking orders and
/// closing; and a dine-in order — no time, no phone, optional 稱呼, expiring a timeout after it was sent, accepted
/// automatically.
/// </summary>
public class OrderingDineInTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();

    private static readonly Guid Member = Guid.CreateVersion7();

    private static readonly DateTimeOffset Noon = AtTaipei(11, 10, 12);

    [Fact]
    public void The_example_file_has_five_tables_staff_accepted_on_the_takeout_hours()
    {
        var shop = Example().Shop;

        shop.Services.DineIn.ShouldBeTrue();
        var dineIn = shop.DineIn.ShouldNotBeNull();
        dineIn.Tables.ShouldBe(
            [new DiningTable("a1", "A1"), new DiningTable("a2", "A2"), new DiningTable("a3", "A3"), new DiningTable("window-1", "窗邊 1"), new DiningTable("window-2", "窗邊 2")]);
        (dineIn.AutoAccept, dineIn.OrderingHours, dineIn.MaxPendingPerTable).ShouldBe((false, null, 5));
        dineIn.Note.ShouldBe("用餐後請至櫃台結帳，可用現金或 LINE Pay。");
        dineIn.Table("window-1")!.Name.ShouldBe("窗邊 1");

        var stored = OrderingShopJson.WriteDineIn(dineIn);
        OrderingShopJson.WriteDineIn(OrderingShopJson.ReadDineIn(stored)).ShouldBe(stored);
        OrderingShopJson.WriteDineIn(null).ShouldBe("null");
    }

    [Fact]
    public void A_dine_in_section_with_its_own_hours_auto_accept_and_limit_reads_as_written()
    {
        var tree = ExampleTree();
        tree["shop"]!["dineIn"] = JsonNode.Parse("""
            { "tables": [{ "id": "t1", "name": "1 號桌" }], "autoAccept": true, "maxPendingPerTable": 20,
              "orderingHours": { "mon": [], "tue": [{ "open": "10:00", "close": "15:00" }], "wed": [], "thu": [], "fri": [], "sat": [], "sun": [] } }
            """);

        var dineIn = Valid(tree.ToJsonString()).Shop.DineIn.ShouldNotBeNull();

        (dineIn.AutoAccept, dineIn.MaxPendingPerTable, dineIn.Note).ShouldBe((true, 20, null));
        dineIn.OrderingHours.ShouldNotBeNull()[DayOfWeek.Tuesday][0].ToString().ShouldBe("10:00–15:00");
        OrderingShopJson.ReadDineIn(OrderingShopJson.WriteDineIn(dineIn)).OrderingHours.ShouldNotBeNull()[DayOfWeek.Monday].ShouldBeEmpty();
    }

    [Fact]
    public void Every_mistake_in_the_dine_in_section_is_listed_with_its_path()
    {
        var tree = ExampleTree();
        tree["shop"]!["dineIn"] = JsonNode.Parse("""
            { "tables": [{ "id": "a1", "name": "A1" }, { "id": "a1", "name": "A1 again" }, { "id": "Bad Id", "name": "B" },
                         { "id": "c1", "name": "這是一個超過二十個字的桌號名稱一二三四五六七" }, { "id": "c2", "name": " " }, { "id": "c3" }],
              "autoAccept": "yes", "maxPendingPerTable": 0, "note": "{{NOTE}}", "seats": 4 }
            """.Replace("{{NOTE}}", new string('說', 301), StringComparison.Ordinal));

        var errors = OrderingShopJson.Parse(tree.ToJsonString()).Errors.Select(error => error.Path).ToList();

        errors.ShouldBe(
            [
                "$.shop.dineIn.seats",
                "$.shop.dineIn.tables[1].id",
                "$.shop.dineIn.tables[2].id",
                "$.shop.dineIn.tables[3].name",
                "$.shop.dineIn.tables[4].name",
                "$.shop.dineIn.tables[5].name",
                "$.shop.dineIn.autoAccept",
                "$.shop.dineIn.note",
                "$.shop.dineIn.maxPendingPerTable",
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Dine_in_needs_its_tables_one_to_two_hundred_and_may_be_the_only_service()
    {
        var withoutSection = ExampleTree();
        withoutSection["shop"]!.AsObject().Remove("dineIn");
        OrderingShopJson.Parse(withoutSection.ToJsonString()).Errors.Select(error => error.ToString())
            .ShouldBe(["$.shop.dineIn：開放內用時必填：桌號清單，例如 { \"tables\": [{ \"id\": \"a1\", \"name\": \"A1\" }] }。"]);

        var noTables = ExampleTree();
        noTables["shop"]!["dineIn"] = JsonNode.Parse("""{ "tables": [] }""");
        OrderingShopJson.Parse(noTables.ToJsonString()).Errors.Select(error => error.ToString())
            .ShouldBe(["$.shop.dineIn.tables：至少要有一張桌子；不提供內用請把 services.dineIn 設成 false。"]);

        var tooMany = ExampleTree();
        tooMany["shop"]!["dineIn"] = new JsonObject
        {
            ["tables"] = new JsonArray([.. Enumerable.Range(1, 201).Select(n => (JsonNode)new JsonObject { ["id"] = $"t{n}", ["name"] = $"{n}" })]),
        };
        OrderingShopJson.Parse(tooMany.ToJsonString()).Errors.Select(error => error.ToString())
            .ShouldBe(["$.shop.dineIn.tables：最多 200 張桌子（目前 201 張）。"]);

        var dineInOnly = ExampleTree();
        dineInOnly["shop"]!["services"] = JsonNode.Parse("""{ "takeoutScheduled": false, "takeoutAsap": false, "dineIn": true }""");
        dineInOnly["shop"]!.AsObject().Remove("delivery");
        Valid(dineInOnly.ToJsonString()).Shop.Services.ShouldBe(new OrderingServices(false, false, DineIn: true));

        var kept = ExampleTree();
        kept["shop"]!["services"] = JsonNode.Parse("""{ "takeoutScheduled": true, "takeoutAsap": true }""");
        kept["shop"]!.AsObject().Remove("delivery");
        Valid(kept.ToJsonString()).Shop.DineIn.ShouldNotBeNull("kept while dine-in is off");
    }

    [Fact]
    public void A_change_of_the_tables_is_a_change_of_dine_in()
    {
        var import = Example();
        var renamed = import.Shop with { DineIn = import.Shop.DineIn! with { Tables = [new DiningTable("a1", "吧台 1")] } };

        OrderingShopChange.Between(import.Shop, renamed).ShouldBe(["dineIn"]);
        OrderingShopChange.Between(import.Shop, import.Shop with { Services = import.Shop.Services with { DineIn = false } }).ShouldBe(["services"]);
    }

    [Fact]
    public void A_table_keeps_its_token_while_it_stays_in_the_file_and_gets_a_new_one_when_added_back()
    {
        var settings = new OrderingDineInSettings([new DiningTable("a1", "A1"), new DiningTable("a2", "A2")]);
        var tables = OrderingTable.Sync(Organization, [], settings, Noon).ToList();

        tables.Select(table => (table.TableId, table.Name, table.IsActive)).ShouldBe([("a1", "A1", true), ("a2", "A2", true)]);
        tables.ShouldAllBe(table => OrderingTable.IsToken(table.Token) && table.OrganizationId == Organization);
        tables[0].Token.ShouldNotBe(tables[1].Token);
        var a1Token = tables[0].Token;

        // Renamed and a table added: the kept one keeps its token, its name follows the file.
        var added = OrderingTable.Sync(
            Organization, tables, settings with { Tables = [new DiningTable("a1", "窗邊 1"), new DiningTable("a2", "A2"), new DiningTable("a3", "A3")] }, Noon.AddDays(1));
        added.Select(table => table.TableId).ShouldBe(["a3"]);
        (tables[0].Token, tables[0].Name).ShouldBe((a1Token, "窗邊 1"));
        tables.AddRange(added);

        // Removed: deactivated, its token no longer an active one.
        OrderingTable.Sync(Organization, tables, settings with { Tables = [new DiningTable("a2", "A2"), new DiningTable("a3", "A3")] }, Noon.AddDays(2))
            .ShouldBeEmpty();
        (tables[0].IsActive, tables[0].DeactivatedAt).ShouldBe((false, Noon.AddDays(2)));
        tables[0].Token.ShouldBe(a1Token, "the deactivated row keeps the old token, which no longer works");

        // Added back: a new row and a new token.
        var back = OrderingTable.Sync(Organization, [.. tables.Where(table => table.IsActive)], settings with
        {
            Tables = [new DiningTable("a1", "A1"), new DiningTable("a2", "A2"), new DiningTable("a3", "A3")],
        }, Noon.AddDays(3)).ShouldHaveSingleItem();
        back.TableId.ShouldBe("a1");
        back.Token.ShouldNotBe(a1Token);

        // No section: every table is deactivated.
        var active = tables.Where(table => table.IsActive).Append(back).ToList();
        OrderingTable.Sync(Organization, active, null, Noon.AddDays(4)).ShouldBeEmpty();
        active.ShouldAllBe(table => !table.IsActive);
    }

    [Fact]
    public void A_regenerated_token_replaces_the_old_one_and_a_deactivated_table_gets_none()
    {
        var table = OrderingTable.Create(Organization, new DiningTable("a1", "A1"), Noon);
        var first = table.Token;

        table.Regenerate(Noon.AddHours(1));

        table.Token.ShouldNotBe(first);
        OrderingTable.IsToken(table.Token).ShouldBeTrue();
        (table.CreatedAt, table.TokenIssuedAt).ShouldBe((Noon, Noon.AddHours(1)));
        OrderingTable.Sync(Organization, [table], null, Noon.AddHours(2));
        Should.Throw<InvalidOperationException>(() => table.Regenerate(Noon.AddHours(3)));
        OrderingTable.IsToken(first[..42]).ShouldBeFalse();
        OrderingTable.IsToken(first[..42] + "=").ShouldBeFalse();
        OrderingTable.IsToken(null).ShouldBeFalse();
    }

    [Fact]
    public void A_session_opens_with_the_first_order_counts_every_order_that_joins_and_closes_once()
    {
        var table = OrderingTable.Create(Organization, new DiningTable("a1", "A1"), Noon);
        var session = OrderingTableSession.Open(table, Noon);

        (session.TableId, session.TableName, session.Version, session.IsOpen, session.OpenedAt).ShouldBe(("a1", "A1", 1, true, Noon));
        var first = DineIn(session, Noon);
        session.Join(Noon.AddMinutes(10));
        var second = DineIn(session, Noon.AddMinutes(10));
        (session.Version, session.UpdatedAt).ShouldBe((2, Noon.AddMinutes(10)));

        // A pending order blocks the close.
        OrderingTableSession.CanClose([first.Status, second.Status]).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => session.Close(Member, [first, second], Noon.AddMinutes(20)));
        session.IsOpen.ShouldBeTrue();

        first.Accept(Member, null, Noon.AddMinutes(1));
        first.MarkReady(Member, Noon.AddMinutes(15));
        second.Decline(Member, OrderingDeclineReason.SoldOut, null, Noon.AddMinutes(11));
        OrderingTableSession.Total([first, second]).ShouldBe(first.Total, "a declined order is not paid");

        var completed = session.Close(Member, [first, second], Noon.AddMinutes(40));

        completed.ShouldBe([first]);
        (first.Status, first.CompletedByAccountId).ShouldBe((OrderingOrderStatus.Completed, Member));
        second.Status.ShouldBe(OrderingOrderStatus.Declined);
        (session.IsOpen, session.ClosedAt, session.ClosedByAccountId, session.Version).ShouldBe((false, Noon.AddMinutes(40), Member, 3));
        OrderingTableSession.Total([first, second]).ShouldBe(first.Total, "a completed order still counts");
        Should.Throw<InvalidOperationException>(() => session.Close(Member, [first, second], Noon.AddMinutes(41)));
        Should.Throw<InvalidOperationException>(() => session.Join(Noon.AddMinutes(42)));
    }

    [Fact]
    public void A_session_closes_only_its_own_orders_and_a_deactivated_table_opens_none()
    {
        var table = OrderingTable.Create(Organization, new DiningTable("a1", "A1"), Noon);
        var session = OrderingTableSession.Open(table, Noon);
        var other = OrderingTableSession.Open(OrderingTable.Create(Organization, new DiningTable("a2", "A2"), Noon), Noon);
        var order = DineIn(other, Noon);
        order.Accept(Member, null, Noon);

        Should.Throw<ArgumentException>(() => session.Close(Member, [order], Noon.AddMinutes(5)));
        session.Close(Member, [], Noon.AddMinutes(5)).ShouldBeEmpty("a session whose orders were all declined closes empty");

        OrderingTable.Sync(Organization, [table], null, Noon);
        Should.Throw<InvalidOperationException>(() => OrderingTableSession.Open(table, Noon));
    }

    [Fact]
    public void A_dine_in_order_is_picked_up_when_sent_without_phone_and_expires_a_timeout_later()
    {
        var session = OrderingTableSession.Open(OrderingTable.Create(Organization, new DiningTable("a1", "A1"), Noon), Noon);

        var order = DineIn(session, Noon, name: string.Empty);

        (order.ServiceType, order.PickupAt, order.ExpiresAt).ShouldBe((OrderingServiceType.DineIn, Noon, Noon.AddMinutes(15)));
        order.Table.ShouldBe(new OrderingOrderTable("a1", "A1", session.Id));
        (order.CustomerName, order.CustomerPhone, order.CustomerEmail, order.AsapMinMinutes).ShouldBe((string.Empty, string.Empty, null, null));
        DineIn(session, Noon, name: "王先生").CustomerName.ShouldBe("王先生");
        order.PromisedAtProblem(Noon.AddMinutes(30), Noon).ShouldBe("內用訂單不需要指定時間。");
        order.PromisedAtProblem(null, Noon).ShouldBeNull();

        order.Accept(Member, null, Noon.AddMinutes(2));
        order.PromisedAt.ShouldBe(Noon, "the pickup it was sent for");
        order.AutoAccepted.ShouldBeFalse();
    }

    [Fact]
    public void A_dine_in_order_has_its_table_and_no_phone_and_only_it_has_a_table()
    {
        var session = OrderingTableSession.Open(OrderingTable.Create(Organization, new DiningTable("a1", "A1"), Noon), Noon);
        var hours = Shop(Week()).Hours;

        Should.Throw<ArgumentException>(() => OrderingOrder.Submit(
            Organization, OrderingServiceType.DineIn, new DateOnly(2026, 11, 10), 1, Noon, null, string.Empty, string.Empty, null, null, null,
            Lines(), 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), hours, 15, Noon));
        Should.Throw<ArgumentException>(() => OrderingOrder.Submit(
            Organization, OrderingServiceType.DineIn, new DateOnly(2026, 11, 10), 1, Noon, null, string.Empty, "0912345678", null, null, null,
            Lines(), 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), hours, 15, Noon, table: new OrderingOrderTable("a1", "A1", session.Id)));
        Should.Throw<ArgumentException>(() => OrderingOrder.Submit(
            Organization, OrderingServiceType.TakeoutScheduled, new DateOnly(2026, 11, 10), 1, Noon, null, "陳小姐", "0912345678", null, null, null,
            Lines(), 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), hours, 15, Noon, table: new OrderingOrderTable("a1", "A1", session.Id)));
    }

    [Fact]
    public void Auto_accept_accepts_a_dine_in_order_by_no_member_and_only_a_dine_in_one()
    {
        var session = OrderingTableSession.Open(OrderingTable.Create(Organization, new DiningTable("a1", "A1"), Noon), Noon);
        var order = DineIn(session, Noon);

        order.AcceptAutomatically(Noon);

        (order.Status, order.Version, order.AutoAccepted, order.AcceptedByAccountId, order.AcceptedAt, order.PromisedAt)
            .ShouldBe((OrderingOrderStatus.Accepted, 2, true, null, Noon, Noon));
        order.AllowedActions.ShouldBe([OrderingOrderAction.MarkReady, OrderingOrderAction.Complete, OrderingOrderAction.Cancel]);
        Should.Throw<InvalidOperationException>(() => order.AcceptAutomatically(Noon));

        var takeout = OrderingOrder.Submit(
            Organization, OrderingServiceType.TakeoutScheduled, new DateOnly(2026, 11, 10), 1, Noon.AddMinutes(30), null, "陳小姐", "0912345678", null,
            null, null, Lines(), 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), Shop(Week()).Hours, 15, Noon);
        Should.Throw<InvalidOperationException>(() => takeout.AcceptAutomatically(Noon));
    }

    [Fact]
    public void Dine_in_orders_are_taken_inside_the_dine_in_hours_of_a_shop_that_is_not_paused()
    {
        var dineIn = new OrderingDineInSettings(
            [new DiningTable("a1", "A1")], OrderingHours: Hours([(DayOfWeek.Tuesday, "10:00", "15:00")]));
        var shop = Shop(Week(), dineIn: dineIn);

        (shop.DineInOffered, shop.DineInHours[DayOfWeek.Tuesday][0].ToString()).ShouldBe((true, "10:00–15:00"));
        OrderingDineIn.CanOrder(shop, AtTaipei(11, 10, 10, 30)).ShouldBeTrue("Tuesday 10:30, before takeout opens at 11:00");
        OrderingDineIn.CanOrder(shop, AtTaipei(11, 10, 15)).ShouldBeFalse();
        OrderingDineIn.CanOrder(shop, AtTaipei(11, 11, 12)).ShouldBeFalse("Wednesday has no dine-in hours");
        shop.SetPaused(true, Noon);
        OrderingDineIn.CanOrder(shop, AtTaipei(11, 10, 10, 30)).ShouldBeFalse();

        var takeoutHours = Shop(Week(), dineIn: new OrderingDineInSettings([new DiningTable("a1", "A1")]));
        OrderingDineIn.CanOrder(takeoutHours, AtTaipei(11, 15, 0, 30)).ShouldBeTrue("Saturday's 17:00–01:00 past midnight");
        Shop(Week()).DineInOffered.ShouldBeFalse();
        OrderingDineIn.CanOrder(Shop(Week()), Noon).ShouldBeFalse();
    }

    private static OrderingOrder DineIn(OrderingTableSession session, DateTimeOffset at, string name = "王先生") => OrderingOrder.Submit(
        Organization, OrderingServiceType.DineIn, new DateOnly(2026, 11, 10), 1, default, null, name, string.Empty, null, null, null,
        Lines(), 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), Shop(Week()).Hours, 15, at,
        table: new OrderingOrderTable(session.TableId, session.TableName, session.Id));

    private static IReadOnlyList<OrderingOrderLine> Lines() =>
        OrderingCart.Price(Example().Menu, [new OrderingLineInput("wonton-soup", 1, null, null)]).Lines;
}
