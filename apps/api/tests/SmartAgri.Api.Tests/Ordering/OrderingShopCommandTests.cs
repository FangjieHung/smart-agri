using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Ordering;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Ordering;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Ordering;

/// <summary>
/// The <c>ordering-shop</c> command (online-ordering Slice O1, #397) against real PostgreSQL: the example file imports and
/// <c>show</c> summarizes it; a broken file lists every error and writes nothing; the same menu again adds no version, a
/// changed one the next; a slug another shop has is refused; the staff's switches survive an import.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class OrderingShopCommandTests : IClassFixture<AuthHostFixture>
{
    private readonly AuthHostFixture _host;

    public OrderingShopCommandTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_example_file_imports_with_one_activity_and_show_summarizes_it()
    {
        var shop = await _host.CreateOrganizationAsync("王記麵店");
        var file = WithSlug(ExampleTree(), Slug()).ToJsonString();

        var imported = await RunWithFileAsync(file, "import", "--organization", shop.Code);

        imported.Exit.ShouldBe(OrderingShopCommand.ExitSuccess, imported.Error);
        imported.Error.ShouldBeEmpty();
        imported.Output.ShouldBe($"已匯入組織 {shop.Code}（王記麵店）的線上點餐設定，菜單第 1 版。\n");
        await using (var dbContext = _host.Postgres.CreateDbContext(shop.Id))
        {
            var stored = await dbContext.OrderingShops.AsNoTracking().SingleAsync(CancellationToken);
            (stored.Name, stored.Version, stored.Paused, stored.AsapPrepBand, stored.MaxOrdersPerSlot).ShouldBe(("王記麵店", 1, false, AsapPrepBand.From15To30, (int?)8));
            stored.OrderingHours[DayOfWeek.Saturday].Count.ShouldBe(2);
            var menu = await dbContext.OrderingMenus.AsNoTracking().SingleAsync(CancellationToken);
            menu.Version.ShouldBe(1);
            OrderingShopJson.WriteMenu(menu.Document).ShouldBe(OrderingShopJson.WriteMenu(OrderingShopJson.Parse(file).Import!.Menu));
            var activity = await dbContext.OrganizationActivities.AsNoTracking()
                .Where(row => row.Action == OrganizationActivityAction.OrderingShopUpdated)
                .SingleAsync(CancellationToken);
            using var detail = JsonDocument.Parse(activity.Detail!);
            detail.RootElement.GetProperty("created").GetBoolean().ShouldBeTrue();
            detail.RootElement.GetProperty("menuVersion").GetInt32().ShouldBe(1);
            activity.Detail!.ShouldNotContain("王記");
            activity.ActorAccountId.ShouldBeNull();
        }

        var shown = await RunAsync(TextReader.Null, "show", "--organization", shop.Code.ToUpperInvariant());

        shown.Exit.ShouldBe(OrderingShopCommand.ExitSuccess, shown.Error);
        shown.Output.ShouldContain($"組織 {shop.Code}（王記麵店）的線上點餐設定\n");
        shown.Output.ShouldContain("取餐方式：外帶預約（開放）、外帶即時（開放）\n");
        shown.Output.ShouldContain("  週一：不開放\n");
        shown.Output.ShouldContain("  週六：11:00–14:00、17:00–01:00（跨午夜）\n");
        shown.Output.ShouldContain("時段：每 30 分鐘，可預約 3 天（含今天），最短準備 20 分鐘，每時段上限 8 筆\n");
        shown.Output.ShouldContain("即時單準備時間：15–30 分鐘\n");
        shown.Output.ShouldContain("暫停接單：否\n");
        shown.Output.ShouldEndWith("菜單：第 1 版，3 個分類、6 個品項\n");
    }

    [Fact]
    public async Task A_broken_file_lists_every_error_and_writes_nothing()
    {
        var shop = await _host.CreateOrganizationAsync("檢查測試店");
        var tree = WithSlug(ExampleTree(), Slug());
        tree["shop"]!["phone"] = "123";
        tree["shop"]!["owner"] = "王老闆";
        var beef = tree["menu"]!["categories"]![0]!["items"]![0]!;
        beef["price"] = -1;
        beef["optionGroups"]![0]!["min"] = 3;
        beef["optionGroups"]![3]!["options"]![0]!["optionGroups"]![0]!["options"]![0]!["optionGroups"] =
            JsonNode.Parse(Levels(3));

        var result = await RunWithFileAsync(tree.ToJsonString(), "import", "--organization", shop.Code);

        result.Exit.ShouldBe(OrderingShopCommand.ExitUsage, result.Error);
        result.Output.ShouldBeEmpty();
        var lines = result.Error.TrimEnd('\n').Split('\n');
        string[] expected =
        [
            "$.shop.owner：不認得的欄位",
            "$.shop.phone：必須是店家的電話號碼",
            "$.menu.categories[0].items[0].price：不能是負數（目前 -1）",
            "$.menu.categories[0].items[0].optionGroups[0]：min（3）不能大於 max（1）",
            "$.menu.categories[0].items[0].optionGroups[0].min：不能重複選的群組，min（3）不能大於選項數（2）",
            "$.menu.categories[0].items[0].optionGroups[3].options[0].optionGroups[0].options[0].optionGroups[0].options[0].optionGroups[0]"
                + ".options[0].optionGroups：選項群組最多 4 層，這裡是第 5 層。",
            "設定檔有 6 個錯誤，沒有寫入任何設定。",
        ];
        lines.Length.ShouldBe(expected.Length, result.Error);
        for (var i = 0; i < expected.Length; i++)
        {
            lines[i].ShouldStartWith(expected[i]);
        }

        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        (await dbContext.OrderingShops.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.OrderingMenus.CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.OrganizationActivities.CountAsync(CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task The_same_menu_adds_no_version_and_a_changed_one_the_next_keeping_the_staffs_switches()
    {
        var shop = await _host.CreateOrganizationAsync("版本測試店");
        var tree = WithSlug(ExampleTree(), Slug());
        (await RunWithFileAsync(tree.ToJsonString(), "import", "--organization", shop.Code)).Exit.ShouldBe(OrderingShopCommand.ExitSuccess);
        await using (var dbContext = _host.Postgres.CreateDbContext(shop.Id))
        {
            var stored = await dbContext.OrderingShops.SingleAsync(CancellationToken);
            stored.SetPaused(true, _host.Clock.GetUtcNow());
            stored.SetAsapPrepBand(AsapPrepBand.From30To45, _host.Clock.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        // The same file, indented differently and from standard input.
        var same = await RunAsync(new StringReader(tree.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), "import", "--organization", shop.Code);

        same.Exit.ShouldBe(OrderingShopCommand.ExitSuccess, same.Error);
        same.Output.ShouldBe($"組織 {shop.Code}（王記麵店）的線上點餐設定與菜單和檔案相同，沒有變更（菜單第 1 版）。\n");
        (await ActivitiesAsync(shop)).Count.ShouldBe(1);

        tree["menu"]!["categories"]![0]!["items"]![2]!["price"] = 75;
        var priced = await RunWithFileAsync(tree.ToJsonString(), "import", "--organization", shop.Code);

        priced.Exit.ShouldBe(OrderingShopCommand.ExitSuccess, priced.Error);
        priced.Output.ShouldBe($"已更新組織 {shop.Code}（王記麵店）的線上點餐設定，變更：menu（菜單第 2 版）。\n");

        tree["shop"]!["name"] = "王記麵店二店";
        var renamed = await RunWithFileAsync(tree.ToJsonString(), "import", "--organization", shop.Code);

        renamed.Output.ShouldBe($"已更新組織 {shop.Code}（王記麵店二店）的線上點餐設定，變更：name。\n");
        await using (var dbContext = _host.Postgres.CreateDbContext(shop.Id))
        {
            (await dbContext.OrderingMenus.OrderBy(menu => menu.Version).Select(menu => menu.Version).ToListAsync(CancellationToken)).ShouldBe([1, 2]);
            var latest = await dbContext.OrderingMenus.AsNoTracking().SingleAsync(menu => menu.Version == 2, CancellationToken);
            latest.Document.FindItem("wonton-soup")!.Value.Item.Price.ShouldBe(75);
            var stored = await dbContext.OrderingShops.AsNoTracking().SingleAsync(CancellationToken);
            (stored.Name, stored.Paused, stored.AsapPrepBand, stored.Version).ShouldBe(("王記麵店二店", true, AsapPrepBand.From30To45, 4));
        }

        var details = (await ActivitiesAsync(shop)).Select(activity => activity.Detail).ToList();
        details.Count.ShouldBe(3);
        // jsonb reorders keys and adds spaces: compare the content.
        Detail(details[1]).ShouldBe((false, "menu", "2"));
        Detail(details[2]).ShouldBe((false, "name", "null"));
    }

    [Fact]
    public async Task A_slug_another_shop_has_is_refused_and_nothing_is_written()
    {
        var first = await _host.CreateOrganizationAsync("先來的店");
        var second = await _host.CreateOrganizationAsync("後到的店");
        var slug = Slug();
        (await RunWithFileAsync(WithSlug(ExampleTree(), slug).ToJsonString(), "import", "--organization", first.Code)).Exit
            .ShouldBe(OrderingShopCommand.ExitSuccess);

        var taken = await RunWithFileAsync(WithSlug(ExampleTree(), slug).ToJsonString(), "import", "--organization", second.Code);

        taken.Exit.ShouldBe(OrderingShopCommand.ExitUsage, taken.Error);
        taken.Output.ShouldBeEmpty();
        taken.Error.ShouldBe($"$.shop.slug：「{slug}」已經是另一家店的點餐網址代號，請換一個。\n設定檔有 1 個錯誤，沒有寫入任何設定。\n");
        await using (var dbContext = _host.Postgres.CreateDbContext(second.Id))
        {
            (await dbContext.OrderingShops.CountAsync(CancellationToken)).ShouldBe(0);
            (await dbContext.OrderingMenus.CountAsync(CancellationToken)).ShouldBe(0);
            (await dbContext.OrganizationActivities.CountAsync(CancellationToken)).ShouldBe(0);
        }

        // The first shop keeps importing its own slug.
        (await RunWithFileAsync(WithSlug(ExampleTree(), slug).ToJsonString(), "import", "--organization", first.Code)).Exit
            .ShouldBe(OrderingShopCommand.ExitSuccess);
    }

    [Fact]
    public async Task Bad_arguments_unknown_organizations_and_a_shop_without_settings_are_reported()
    {
        var shop = await _host.CreateOrganizationAsync("尚未設定的店");

        (await RunAsync(TextReader.Null)).Exit.ShouldBe(OrderingShopCommand.ExitUsage);
        (await RunAsync(TextReader.Null, "delete", "--organization", shop.Code)).Exit.ShouldBe(OrderingShopCommand.ExitUsage);
        (await RunAsync(TextReader.Null, "show")).Exit.ShouldBe(OrderingShopCommand.ExitUsage);
        (await RunAsync(TextReader.Null, "show", "--organization", shop.Code, "--file", "x.json")).Exit.ShouldBe(OrderingShopCommand.ExitUsage);
        (await RunAsync(TextReader.Null, "import", "--organization", shop.Code)).Error.ShouldStartWith("沒有從標準輸入讀到設定檔");
        (await RunAsync(TextReader.Null, "import", "--organization", shop.Code, "--file", "/nonexistent/ordering.json")).Exit
            .ShouldBe(OrderingShopCommand.ExitUsage);
        (await RunAsync(new StringReader("{ nope"), "import", "--organization", shop.Code)).Error.ShouldStartWith("$：不是有效的 JSON");

        var none = await RunAsync(TextReader.Null, "show", "--organization", shop.Code);
        none.Exit.ShouldBe(OrderingShopCommand.ExitFailed);
        none.Error.ShouldContain("還沒有線上點餐設定");
        var unknown = await RunWithFileAsync(WithSlug(ExampleTree(), Slug()).ToJsonString(), "import", "--organization", "no-such-shop");
        unknown.Exit.ShouldBe(OrderingShopCommand.ExitFailed);
        unknown.Error.ShouldBe("找不到組織代碼「no-such-shop」。\n");
        var help = await RunAsync(TextReader.Null, "--help");
        (help.Exit, help.Output.StartsWith("用法：ordering-shop import", StringComparison.Ordinal)).ShouldBe((OrderingShopCommand.ExitSuccess, true));
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static (bool Created, string Changed, string MenuVersion) Detail(string? json)
    {
        var detail = JsonNode.Parse(json!)!.AsObject();
        detail.Select(property => property.Key).Order(StringComparer.Ordinal).ShouldBe(["changed", "created", "menuVersion"]);
        return (
            detail["created"]!.GetValue<bool>(),
            string.Join(',', detail["changed"]!.AsArray().Select(name => name!.GetValue<string>())),
            detail["menuVersion"]?.ToJsonString() ?? "null");
    }

    private static string Slug() => "shop-" + Guid.NewGuid().ToString("N")[..10];

    private static JsonObject ExampleTree() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "deploy", "ordering-shop.example.json")))!.AsObject();

    private static JsonObject WithSlug(JsonObject tree, string slug)
    {
        tree["shop"]!["slug"] = slug;
        return tree;
    }

    /// <summary><paramref name="levels"/> nested option groups as an <c>optionGroups</c> array.</summary>
    private static string Levels(int levels) => levels == 0
        ? "[]"
        : $$"""[{ "id": "g{{levels}}", "name": "層{{levels}}", "min": 0, "max": 1, "options": [{ "id": "o", "name": "選項", "optionGroups": {{Levels(levels - 1)}} }] }]""";

    private async Task<List<OrganizationActivity>> ActivitiesAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.OrganizationActivities.AsNoTracking()
            .Where(activity => activity.Action == OrganizationActivityAction.OrderingShopUpdated)
            .OrderBy(activity => activity.Id)
            .ToListAsync(CancellationToken);
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(TextReader input, params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var exit = await OrderingShopCommand.RunAsync(_host.Factory.Services, args, input, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>Runs the command with <c>--file</c> pointing at <paramref name="content"/> in a temporary file.</summary>
    private async Task<(int Exit, string Output, string Error)> RunWithFileAsync(string content, params string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ordering-shop-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, content, CancellationToken);
        try
        {
            return await RunAsync(TextReader.Null, [.. args, "--file", path]);
        }
        finally
        {
            File.Delete(path);
        }
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
