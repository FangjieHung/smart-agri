using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// The shops' settings (store-coordinator design §5 Slice 2, #373): the <c>coordinator-shop</c> command
/// against real PostgreSQL — validate everything, write nothing on any error, show what import takes, one
/// activity per change — and the 統籌助理 of Slice 1 using a shop's store name and time zone.
/// </summary>
public sealed partial class CoordinatorWebhookTests
{
    private const string ShopFile = """
        {
          "storeName": "王記麵店",
          "timeZone": "Asia/Taipei",
          "weeklyHours": {
            "mon": [],
            "tue": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "wed": [{ "open": "11:00", "close": "14:00" }],
            "thu": [{ "open": "11:00", "close": "14:00" }],
            "fri": [{ "open": "11:00", "close": "14:00" }],
            "sat": [{ "open": "18:00", "close": "02:00" }],
            "sun": [{ "open": "11:00", "close": "14:00" }]
          },
          "scenarios": {
            "temporary-closure": {
              "enabled": true,
              "announcement": { "template": "{店名} {日期} 臨時店休，{恢復營業} 恢復營業。\n原因：{原因}", "imageUrl": "https://example.com/closed.jpg" },
              "actions": [
                { "kind": "line-broadcast", "mode": "auto", "steps": "群發公告。" },
                { "kind": "google-special-hours", "mode": "reminder", "steps": "Google 商家設為休息。" },
                { "kind": "ordering", "name": "Xurf", "mode": "reminder", "pauseSteps": "關閉 Xurf 接單。", "resumeSteps": "打開 Xurf 接單。" }
              ]
            },
            "close-early": {
              "enabled": true,
              "announcement": { "template": "{店名} {日期} 提早到 {時間} 打烊。" },
              "actions": [{ "kind": "line-broadcast", "mode": "auto", "steps": "群發公告。" }]
            },
            "open-late": { "enabled": false },
            "reopen": { "enabled": true, "announcement": { "template": "{店名} {日期} 照常營業。" } }
          }
        }
        """;

    [Fact]
    public async Task An_invalid_file_is_refused_with_every_error_and_nothing_is_written()
    {
        var shop = await _host.CreateOrganizationAsync("檢查測試店");
        var tree = JsonNode.Parse(ShopFile)!.AsObject();
        tree["timeZone"] = "Mars/Olympus";
        tree["owner"] = "王老闆";
        tree["weeklyHours"]!["tue"]![1]!["open"] = "13:00";
        tree["weeklyHours"]!["sun"] = JsonNode.Parse("""[{ "open": "01:00", "close": "03:00" }]""");
        tree["weeklyHours"]!["wed"]![0]!["close"] = "11:00";
        var closure = tree["scenarios"]!["temporary-closure"]!;
        closure["announcement"]!["imageUrl"] = "http://example.com/closed.jpg";
        var actions = closure["actions"]!.AsArray();
        actions[1]!["kind"] = "instagram-post";
        actions[2]!["mode"] = "auto";
        actions.Add(JsonNode.Parse("""{ "kind": "line-broadcast", "mode": "reminder", "steps": "再群發一次。" }"""));
        tree["scenarios"]!["close-early"]!["announcement"]!["template"] = "{店名} {日期} 提早打烊，{恢復營業} 再見。";
        tree["scenarios"]!["reopen"]!["actions"] = new JsonArray();
        tree["scenarios"]!.AsObject().Remove("open-late");

        var result = await RunShopWithFileAsync(tree.ToJsonString(), "import", "--organization", shop.Code);

        result.Exit.ShouldBe(CoordinatorShopCommand.ExitUsage, result.Error);
        result.Output.ShouldBeEmpty();
        string[] expected =
        [
            "$.timeZone：不認得的時區「Mars/Olympus」",
            "$.owner：不認得的欄位",
            "$.weeklyHours.tue[1]：和 tue[0] 的 11:00–14:00 重疊",
            "$.weeklyHours.sun[0]：和 sat[0] 的 18:00–02:00（跨午夜） 重疊",
            "$.weeklyHours.wed[0]：開店與打烊時間相同",
            "$.scenarios['temporary-closure'].announcement.imageUrl：必須是完整的 https:// 網址",
            "$.scenarios['temporary-closure'].actions[1].kind：不認得的動作種類「instagram-post」",
            "$.scenarios['temporary-closure'].actions[2].mode：點餐系統沒有可以串接的 API，只能是 reminder（提醒）",
            "$.scenarios['temporary-closure'].actions[3].kind：這個情境已經有 line-broadcast（actions[0]）",
            "$.scenarios['close-early'].announcement.template：這個情境不能用 {恢復營業}",
            "$.scenarios['close-early'].announcement.template：必須包含 {時間}",
            "$.scenarios['open-late']：缺少這個情境",
            "$.scenarios.reopen.actions：恢復營業沒有自己的動作",
        ];
        foreach (var line in expected)
        {
            result.Error.ShouldContain(line, Case.Sensitive);
        }

        result.Error.ShouldContain($"設定檔有 {expected.Length} 個錯誤，沒有寫入任何設定。");
        (await ShopSettingsAsync(shop)).ShouldBeNull();
        (await ShopActivitiesAsync(shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_valid_import_is_stored_shown_in_the_same_format_and_only_changes_are_recorded()
    {
        var shop = await _host.CreateOrganizationAsync("匯入測試店");

        var imported = await RunShopWithFileAsync(ShopFile, "import", "--organization", shop.Code.ToUpperInvariant());

        imported.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, imported.Error);
        imported.Output.ShouldBe($"已匯入組織 {shop.Code}（王記麵店）的店家設定。\n", StringCompareShould.IgnoreLineEndings);
        var stored = (await ShopSettingsAsync(shop)).ShouldNotBeNull();
        stored.StoreName.ShouldBe("王記麵店");
        stored.TimeZoneId.ShouldBe("Asia/Taipei");
        stored.WeeklyHours[DayOfWeek.Saturday].ShouldHaveSingleItem().IsOvernight.ShouldBeTrue();
        stored.Scenarios[ShopScenario.TemporaryClosure].Actions.Count.ShouldBe(3);
        var created = (await ShopActivitiesAsync(shop)).ShouldHaveSingleItem();
        created.ActorAccountId.ShouldBeNull();
        (_host.Clock.GetUtcNow() - created.At).ShouldBeInRange(TimeSpan.Zero, TimeSpan.FromMinutes(1));
        using (var detail = JsonDocument.Parse(created.Detail!))
        {
            detail.RootElement.GetProperty("created").GetBoolean().ShouldBeTrue();
            detail.RootElement.GetProperty("changed").EnumerateArray().Select(name => name.GetString())
                .ShouldBe(["storeName", "timeZone", "weeklyHours", "scenarios"]);
        }

        created.Detail!.ShouldNotContain("王記");
        created.Detail!.ShouldNotContain("Xurf");

        // show prints the stored settings as a settings file, which imports again without a change.
        var shown = await RunShopAsync("show", "--organization", shop.Code);
        shown.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, shown.Error);
        var reread = ShopSettingsJson.Parse(shown.Output);
        reread.Errors.ShouldBeEmpty();
        ShopSettingsChange.Between(ShopSettingsJson.Parse(ShopFile).Document!, reread.Document!).IsEmpty.ShouldBeTrue();
        shown.Output.ShouldBe(ShopSettingsJson.Write(reread.Document!) + "\n", StringCompareShould.IgnoreLineEndings);
        JsonNode.DeepEquals(JsonNode.Parse(shown.Output), JsonNode.Parse(ShopFile)).ShouldBeFalse("open-late gains its empty actions");

        var same = await RunShopWithFileAsync(shown.Output, "import", "--organization", shop.Code);
        same.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, same.Error);
        same.Output.ShouldContain("沒有變更");
        (await ShopActivitiesAsync(shop)).Count.ShouldBe(1, "an identical import records nothing");

        // A changed file: one more activity, naming what changed.
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        var tree = JsonNode.Parse(ShopFile)!.AsObject();
        tree["weeklyHours"]!["mon"] = JsonNode.Parse("""[{ "open": "11:00", "close": "14:00" }]""");
        tree["scenarios"]!["reopen"]!["announcement"]!["template"] = "{店名} {日期} 照常營業，歡迎光臨！";
        var changed = await RunShopWithFileAsync(tree.ToJsonString(), "import", "--organization", shop.Code);

        changed.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, changed.Error);
        changed.Output.ShouldContain("已更新");
        changed.Output.ShouldContain("weeklyHours、scenarios（reopen）");
        var activities = await ShopActivitiesAsync(shop);
        activities.Count.ShouldBe(2);
        using (var detail = JsonDocument.Parse(activities[1].Detail!))
        {
            detail.RootElement.GetProperty("created").GetBoolean().ShouldBeFalse();
            detail.RootElement.GetProperty("changed").EnumerateArray().Select(name => name.GetString()).ShouldBe(["weeklyHours", "scenarios"]);
            detail.RootElement.GetProperty("scenarios").EnumerateArray().Select(name => name.GetString()).ShouldBe(["reopen"]);
        }

        var updated = (await ShopSettingsAsync(shop)).ShouldNotBeNull();
        updated.WeeklyHours[DayOfWeek.Monday].ShouldHaveSingleItem();
        (updated.UpdatedAt - created.At).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(5));
        updated.CreatedAt.ShouldBe(created.At);
    }

    [Fact]
    public async Task The_shop_command_refuses_unknown_shops_shops_without_settings_and_bad_arguments()
    {
        var shop = await _host.CreateOrganizationAsync("參數測試店");

        var none = await RunShopAsync("show", "--organization", shop.Code);
        none.Exit.ShouldBe(CoordinatorShopCommand.ExitFailed);
        none.Output.ShouldBeEmpty();
        none.Error.ShouldContain("還沒有店家設定");

        var unknown = await RunShopAsync("show", "--organization", "no-such-shop");
        unknown.Exit.ShouldBe(CoordinatorShopCommand.ExitFailed);
        unknown.Error.ShouldContain("找不到組織代碼");
        var unknownImport = await RunShopWithFileAsync(ShopFile, "import", "--organization", "no-such-shop");
        unknownImport.Exit.ShouldBe(CoordinatorShopCommand.ExitFailed);

        foreach (var args in new[]
                 {
                     Array.Empty<string>(), ["export"], ["show"], ["import", "--organization", shop.Code], ["show", "--organization"],
                     ["show", "--organization", shop.Code, "--file", "x.json"], ["import", "--organization", shop.Code, "--file"], ["show", "--nope"],
                 })
        {
            var result = await RunShopAsync(args);
            result.Exit.ShouldBe(CoordinatorShopCommand.ExitUsage, string.Join(' ', args));
            result.Error.ShouldContain("用法：coordinator-shop");
        }

        var missing = await RunShopAsync("import", "--organization", shop.Code, "--file", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        missing.Exit.ShouldBe(CoordinatorShopCommand.ExitUsage);
        missing.Error.ShouldContain("找不到設定檔");

        var notJson = await RunShopWithFileAsync("店名：王記", "import", "--organization", shop.Code);
        notJson.Exit.ShouldBe(CoordinatorShopCommand.ExitUsage);
        notJson.Error.ShouldContain("$：不是有效的 JSON");

        (await ShopSettingsAsync(shop)).ShouldBeNull();

        var help = await RunShopAsync("--help");
        help.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess);
        help.Output.ShouldStartWith("用法：coordinator-shop");
    }

    // --- Slice 1 with settings ------------------------------------------------------------------------

    [Fact]
    public async Task The_coordinator_names_a_shop_by_its_settings_store_name_and_shows_times_in_its_time_zone()
    {
        var shop = await _host.CreateOrganizationAsync("王氏餐飲有限公司");
        var tree = JsonNode.Parse(ShopFile)!.AsObject();
        tree["storeName"] = "王記麵店 東京店";
        tree["timeZone"] = "Asia/Tokyo";
        (await RunShopWithFileAsync(tree.ToJsonString(), "import", "--organization", shop.Code)).Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess);

        // The operator's command.
        var issued = await RunCommandAsync("--organization", shop.Code);
        issued.Exit.ShouldBe(CoordinatorBindingCodeCommand.ExitSuccess, issued.Error);
        issued.Output.ShouldContain($"組織 {shop.Code}（王記麵店 東京店）的老闆綁定碼");
        issued.Output.ShouldContain("（Asia/Tokyo）");
        var row = (await CodesAsync(shop)).ShouldHaveSingleItem();
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        issued.Output.ShouldContain(TimeZoneInfo.ConvertTime(row.ExpiresAt, tokyo).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));

        // Binding replies, and a 店長 code's expiry.
        var owner = NewUser();
        (await SendAsync(owner, CodeIn(issued.Output))).ShouldBe("已綁定 王記麵店 東京店（老闆）。");
        var reply = await SendAsync(owner, "產生店長綁定碼");
        reply.ShouldContain("店名：王記麵店 東京店");
        reply.ShouldContain("（Asia/Tokyo）前");
        var managerRow = (await CodesAsync(shop)).Single(code => code.UsedAt is null);
        reply.ShouldContain(TimeZoneInfo.ConvertTime(managerRow.ExpiresAt, tokyo).ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        var manager = NewUser();
        (await SendAsync(manager, CodeIn(reply))).ShouldBe("已綁定 王記麵店 東京店（店長）。");

        // The store choice of an owner of two shops: one by its settings, one by its organization name.
        var plain = await _host.CreateOrganizationAsync("沒有設定的小館");
        (await SendAsync(owner, await OwnerCodeAsync(plain))).ShouldBe("已綁定 沒有設定的小館（老闆）。");
        await PostAsync(Body(Bot.UserId, Text(owner, "產生店長綁定碼", "rt-choose-settings")), sign: true);
        var choice = await ReplyMessageAsync("rt-choose-settings");
        choice.GetProperty("quickReply").GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("action").GetProperty("label").GetString())
            .ShouldBe(["沒有設定的小館", "王記麵店 東京店"], ignoreOrder: true);

        // A shop without settings is in Taipei.
        await PostAsync(Body(Bot.UserId, Postback(owner, CoordinatorMessageRouter.ManagerCodePostback(plain.Id), "rt-plain")), sign: true);
        (await ReplyTextAsync("rt-plain")).ShouldContain("（台北時間）前");
        using var scope = _host.Factory.Services.CreateScope();
        var bindings = scope.ServiceProvider.GetRequiredService<CoordinatorBindings>();
        (await bindings.TimeZoneOfAsync(plain.Id, CancellationToken)).Id.ShouldBe("Asia/Taipei");
        (await bindings.TimeZoneOfAsync(shop.Id, CancellationToken)).Id.ShouldBe("Asia/Tokyo");
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private async Task<(int Exit, string Output, string Error)> RunShopAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CoordinatorShopCommand.RunAsync(_host.Factory.Services, args, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>Runs the command with <c>--file</c> pointing at <paramref name="content"/> in a temporary file.</summary>
    private async Task<(int Exit, string Output, string Error)> RunShopWithFileAsync(string content, params string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), $"coordinator-shop-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, content, CancellationToken);
        try
        {
            return await RunShopAsync([.. args, "--file", path]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<ShopSettings?> ShopSettingsAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorShopSettings.AsNoTracking().SingleOrDefaultAsync(CancellationToken);
    }

    private async Task<List<OrganizationActivity>> ShopActivitiesAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.OrganizationActivities
            .AsNoTracking()
            .Where(activity => activity.Action == OrganizationActivityAction.CoordinatorShopSettingsUpdated)
            .OrderBy(activity => activity.At)
            .ToListAsync(CancellationToken);
    }
}
