using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// The shop-settings file (store-coordinator design §5 Slice 2, #373): what a valid file becomes, that
/// <see cref="ShopSettingsJson.Write"/> writes what <see cref="ShopSettingsJson.Parse"/> reads, and that
/// every kind of invalid file is refused with each error at its JSON path.
/// </summary>
public partial class ShopSettingsJsonTests
{
    private const string Closure = "$.scenarios['temporary-closure']";

    [Fact]
    public void A_valid_file_becomes_the_shops_settings()
    {
        var settings = ShopSettingsSamples.ValidDocument();

        settings.StoreName.ShouldBe("王記麵店");
        settings.TimeZoneId.ShouldBe("Asia/Taipei");
        settings.WeeklyHours[DayOfWeek.Monday].ShouldBeEmpty();
        settings.WeeklyHours[DayOfWeek.Tuesday].ShouldBe(
            [new BusinessPeriod(new TimeOnly(11, 0), new TimeOnly(14, 0)), new BusinessPeriod(new TimeOnly(17, 0), new TimeOnly(21, 0))]);
        var saturday = settings.WeeklyHours[DayOfWeek.Saturday].ShouldHaveSingleItem();
        saturday.IsOvernight.ShouldBeTrue();
        saturday.Length.ShouldBe(TimeSpan.FromHours(8));

        var closure = settings[ShopScenario.TemporaryClosure];
        closure.Enabled.ShouldBeTrue();
        closure.Announcement.ShouldBe(new ShopAnnouncement("{店名} {日期} 臨時店休，{恢復營業} 恢復營業。\n原因：{原因}", "https://example.com/closed.jpg"));
        closure.Actions.Select(action => (action.Kind, action.Mode)).ShouldBe(
        [
            (ShopActionKind.LineBroadcast, ShopActionMode.Auto),
            (ShopActionKind.GoogleSpecialHours, ShopActionMode.Reminder),
            (ShopActionKind.FacebookPost, ShopActionMode.Reminder),
            (ShopActionKind.Ordering, ShopActionMode.Reminder),
        ]);
        closure.Actions[3].ShouldBe(new ShopAction(
            ShopActionKind.Ordering, ShopActionMode.Reminder, Name: "Xurf", PauseSteps: "關閉 Xurf 接單。", ResumeSteps: "打開 Xurf 接單。"));
        settings[ShopScenario.OpenLate].ShouldBe(new ShopScenarioSettings(false, null, []), new ScenarioComparer());
        settings[ShopScenario.Reopen].Announcement!.ImageUrl.ShouldBeNull();
        settings[ShopScenario.Reopen].Actions.ShouldBeEmpty();
    }

    [Fact]
    public void Without_a_time_zone_the_shop_is_in_Taipei()
    {
        var tree = ShopSettingsSamples.ValidTree();
        tree.Remove("timeZone");

        ShopSettingsSamples.Parse(tree.ToJsonString()).TimeZoneId.ShouldBe(ShopSettings.DefaultTimeZoneId);
    }

    [Fact]
    public void What_is_written_reads_back_the_same_and_writes_the_same_again()
    {
        var settings = ShopSettingsSamples.ValidDocument();

        var written = ShopSettingsJson.Write(settings);
        var again = ShopSettingsSamples.Parse(written);

        ShopSettingsJson.Write(again).ShouldBe(written);
        written.ShouldContain("\"storeName\": \"王記麵店\"", Case.Sensitive, "Chinese is written as it is");
        written.ShouldContain("恢復營業。\\n原因");
        written.ShouldNotContain("\r");
        ShopSettingsChange.Between(settings, again).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void The_example_file_in_deploy_is_valid_and_exactly_what_show_prints()
    {
        var file = File.ReadAllText(ShopSettingsSamples.RepositoryFile("deploy", "coordinator-shop.example.json")).ReplaceLineEndings("\n");

        var settings = ShopSettingsSamples.Parse(file);

        (ShopSettingsJson.Write(settings) + "\n").ShouldBe(file);
        Enum.GetValues<ShopScenario>().ShouldAllBe(scenario => settings[scenario].Enabled);
    }

    [Fact]
    public void The_settings_files_in_the_deploy_guide_are_valid()
    {
        var guide = File.ReadAllText(ShopSettingsSamples.RepositoryFile("deploy", "README.md"));

        var blocks = JsonBlock().Matches(guide).Select(match => match.Groups[1].Value).Where(block => block.Contains("\"weeklyHours\"", StringComparison.Ordinal)).ToList();

        blocks.ShouldNotBeEmpty();
        foreach (var block in blocks)
        {
            ShopSettingsSamples.Parse(block);
        }
    }

    [Fact]
    public void A_disabled_scenario_needs_neither_announcement_nor_actions_and_reopen_needs_no_announcement()
    {
        var tree = ShopSettingsSamples.ValidTree();
        tree["scenarios"]!["close-early"] = new JsonObject { ["enabled"] = false };
        tree["scenarios"]!["reopen"] = new JsonObject { ["enabled"] = true };

        var settings = ShopSettingsSamples.Parse(tree.ToJsonString());

        settings[ShopScenario.CloseEarly].ShouldBe(new ShopScenarioSettings(false, null, []), new ScenarioComparer());
        settings[ShopScenario.Reopen].ShouldBe(new ShopScenarioSettings(true, null, []), new ScenarioComparer());
    }

    [Fact]
    public void Several_ordering_systems_with_different_names_are_fine_and_periods_may_touch()
    {
        var tree = ShopSettingsSamples.ValidTree();
        Actions(tree, "temporary-closure").Add(JsonNode.Parse(
            """{ "kind": "ordering", "name": "foodpanda", "mode": "reminder", "pauseSteps": "暫停。", "resumeSteps": "恢復。" }"""));
        tree["weeklyHours"]!["tue"] = JsonNode.Parse("""[{ "open": "11:00", "close": "14:00" }, { "open": "14:00", "close": "21:00" }]""");
        tree["weeklyHours"]!["sun"] = JsonNode.Parse("""[{ "open": "02:00", "close": "14:00" }, { "open": "22:00", "close": "00:00" }]""");

        var settings = ShopSettingsSamples.Parse(tree.ToJsonString());

        settings[ShopScenario.TemporaryClosure].Actions.Count(action => action.Kind == ShopActionKind.Ordering).ShouldBe(2);
    }

    // --- Refused --------------------------------------------------------------------------------------

    private static readonly Dictionary<string, (Action<JsonObject> Change, string Path, string Message)> InvalidCases = new()
    {
        ["unknown action kind"] = (tree => Action(tree, "temporary-closure", 0)["kind"] = "instagram-post",
            $"{Closure}.actions[0].kind", "不認得的動作種類「instagram-post」"),
        ["unknown top-level field"] = (tree => tree["owner"] = "王老闆", "$.owner", "不認得的欄位"),
        ["unknown action field"] = (tree => Action(tree, "temporary-closure", 0)["url"] = "https://x", $"{Closure}.actions[0].url", "不認得的欄位"),
        ["ordering field on another kind"] = (tree => Action(tree, "temporary-closure", 0)["name"] = "LINE", $"{Closure}.actions[0].name", "只有 ordering"),
        ["steps on ordering"] = (tree => Action(tree, "temporary-closure", 3)["steps"] = "x", $"{Closure}.actions[3].steps", "pauseSteps"),
        ["unknown period field"] = (tree => Day(tree, "tue")[0]!["note"] = "午餐", "$.weeklyHours.tue[0].note", "不認得的欄位"),
        ["overlapping periods"] = (tree => Day(tree, "tue")[1]!["open"] = "13:00",
            "$.weeklyHours.tue[1]", "和 tue[0] 的 11:00–14:00 重疊"),
        ["an overnight period spilling into the next day's first"] = (tree => tree["weeklyHours"]!["sun"] = JsonNode.Parse("""[{ "open": "01:00", "close": "03:00" }]"""),
            "$.weeklyHours.sun[0]", "和 sat[0] 的 18:00–02:00（跨午夜） 重疊"),
        ["Sunday's overnight period spilling into Monday"] = (tree =>
            {
                tree["weeklyHours"]!["sun"] = JsonNode.Parse("""[{ "open": "22:00", "close": "03:00" }]""");
                tree["weeklyHours"]!["mon"] = JsonNode.Parse("""[{ "open": "02:00", "close": "05:00" }]""");
            },
            "$.weeklyHours.mon[0]", "和 sun[0] 的 22:00–03:00（跨午夜） 重疊"),
        ["open equal to close"] = (tree => Day(tree, "wed")[0]!["close"] = "11:00", "$.weeklyHours.wed[0]", "開店與打烊時間相同"),
        ["not HH:mm"] = (tree => Day(tree, "wed")[0]!["open"] = "9:30", "$.weeklyHours.wed[0].open", "HH:mm"),
        ["24:00"] = (tree => Day(tree, "wed")[1]!["close"] = "24:00", "$.weeklyHours.wed[1].close", "HH:mm"),
        ["five periods a day"] = (tree => tree["weeklyHours"]!["thu"] = JsonNode.Parse(
                """[{"open":"06:00","close":"07:00"},{"open":"08:00","close":"09:00"},{"open":"10:00","close":"11:00"},{"open":"12:00","close":"13:00"},{"open":"14:00","close":"15:00"}]"""),
            "$.weeklyHours.thu", "一天最多 4 個時段"),
        ["a missing day"] = (tree => tree["weeklyHours"]!.AsObject().Remove("fri"), "$.weeklyHours.fri", "缺少這一天"),
        ["no business period all week"] = (tree =>
            {
                foreach (var day in new[] { "tue", "wed", "thu", "fri", "sat", "sun" })
                {
                    tree["weeklyHours"]![day] = new JsonArray();
                }
            },
            "$.weeklyHours", "每週至少要有一個營業時段"),
        ["a template without a required variable"] = (tree => Template(tree, "close-early", "{店名} 今天提早打烊。"),
            "$.scenarios['close-early'].announcement.template", "必須包含 {時間}"),
        ["a variable another scenario has"] = (tree => Template(tree, "temporary-closure", "{日期} {時間} 店休"),
            $"{Closure}.announcement.template", "這個情境不能用 {時間}"),
        ["{恢復營業} outside temporary-closure"] = (tree => Template(tree, "reopen", "{日期} 恢復，{恢復營業}"),
            "$.scenarios.reopen.announcement.template", "這個情境不能用 {恢復營業}"),
        ["an unknown variable"] = (tree => Template(tree, "reopen", "{日期} 照常營業，地址：{地址}"), "$.scenarios.reopen.announcement.template", "不認得的變數 {地址}"),
        ["a lone brace"] = (tree => Template(tree, "reopen", "{日期} 照常營業 {"), "$.scenarios.reopen.announcement.template", "沒有對應的「}」"),
        ["{原因} on the line of a required variable"] = (tree => Template(tree, "reopen", "{日期} 照常營業（{原因}）"),
            "$.scenarios.reopen.announcement.template", "{原因} 要和 {日期} 分開放在不同行"),
        ["an http image"] = (tree => tree["scenarios"]!["temporary-closure"]!["announcement"]!["imageUrl"] = "http://example.com/a.jpg",
            $"{Closure}.announcement.imageUrl", "https://"),
        ["a relative image"] = (tree => tree["scenarios"]!["temporary-closure"]!["announcement"]!["imageUrl"] = "/images/a.jpg",
            $"{Closure}.announcement.imageUrl", "https://"),
        ["a too long image URL"] = (tree => tree["scenarios"]!["temporary-closure"]!["announcement"]!["imageUrl"] = "https://example.com/" + new string('a', 2000),
            $"{Closure}.announcement.imageUrl", "最多 2000 個字元"),
        ["ordering in auto"] = (tree => Action(tree, "temporary-closure", 3)["mode"] = "auto", $"{Closure}.actions[3].mode", "只能是 reminder"),
        ["an unknown mode"] = (tree => Action(tree, "temporary-closure", 0)["mode"] = "manual", $"{Closure}.actions[0].mode", "auto（自動）或 reminder（提醒）"),
        ["a second line-broadcast"] = (tree => Actions(tree, "temporary-closure").Add(JsonNode.Parse("""{ "kind": "line-broadcast", "mode": "reminder", "steps": "群發。" }""")),
            $"{Closure}.actions[4].kind", "已經有 line-broadcast（actions[0]）"),
        ["two ordering systems of one name"] = (tree => Actions(tree, "close-early").Add(JsonNode.Parse(
                """{ "kind": "ordering", "name": "xurf", "mode": "reminder", "pauseSteps": "a", "resumeSteps": "b" }""")),
            "$.scenarios['close-early'].actions[2].name", "和 actions[1] 的名稱相同"),
        ["actions under reopen"] = (tree => tree["scenarios"]!["reopen"]!["actions"] = new JsonArray(),
            "$.scenarios.reopen.actions", "恢復營業沒有自己的動作"),
        ["a time zone that does not exist"] = (tree => tree["timeZone"] = "Mars/Olympus", "$.timeZone", "不認得的時區「Mars/Olympus」"),
        ["a missing scenario"] = (tree => tree["scenarios"]!.AsObject().Remove("open-late"), "$.scenarios['open-late']", "缺少這個情境"),
        ["an unknown scenario"] = (tree => tree["scenarios"]!["holiday"] = new JsonObject { ["enabled"] = false }, "$.scenarios.holiday", "不認得的情境"),
        ["an enabled scenario without actions"] = (tree => tree["scenarios"]!["close-early"]!["actions"] = new JsonArray(),
            "$.scenarios['close-early'].actions", "至少要有一個動作"),
        ["an enabled scenario without an announcement"] = (tree => tree["scenarios"]!["close-early"]!.AsObject().Remove("announcement"),
            "$.scenarios['close-early'].announcement", "必須設定公告"),
        ["enabled not a boolean"] = (tree => tree["scenarios"]!["open-late"]!["enabled"] = "no", "$.scenarios['open-late'].enabled", "true 或 false"),
        ["a blank store name"] = (tree => tree["storeName"] = "  ", "$.storeName", "不能是空白"),
        ["a long store name"] = (tree => tree["storeName"] = new string('店', 51), "$.storeName", "最多 50 個字（目前 51 個）"),
        ["a store name with a line break"] = (tree => tree["storeName"] = "王記\n麵店", "$.storeName", "不能有換行"),
        ["a store name not a string"] = (tree => tree["storeName"] = 42, "$.storeName", "必須是字串"),
        ["a missing store name"] = (tree => tree.Remove("storeName"), "$.storeName", "缺少這個欄位"),
        ["long steps"] = (tree => Action(tree, "temporary-closure", 1)["steps"] = new string('步', 501), $"{Closure}.actions[1].steps", "最多 500 個字"),
        ["missing steps"] = (tree => Action(tree, "temporary-closure", 2).Remove("steps"), $"{Closure}.actions[2].steps", "缺少這個欄位"),
        ["a long ordering name"] = (tree => Action(tree, "temporary-closure", 3)["name"] = new string('X', 21), $"{Closure}.actions[3].name", "最多 20 個字"),
        ["missing resume steps"] = (tree => Action(tree, "temporary-closure", 3).Remove("resumeSteps"), $"{Closure}.actions[3].resumeSteps", "缺少這個欄位"),
        ["a long template"] = (tree => Template(tree, "reopen", "{日期}" + new string('好', 1000)), "$.scenarios.reopen.announcement.template", "最多 1000 個字"),
    };

    public static TheoryData<string> InvalidNames => [.. InvalidCases.Keys];

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void An_invalid_file_is_refused_with_the_error_at_its_path(string why)
    {
        var (change, path, message) = InvalidCases[why];
        var tree = ShopSettingsSamples.ValidTree();
        change(tree);

        var result = ShopSettingsJson.Parse(tree.ToJsonString());

        result.IsValid.ShouldBeFalse(why);
        result.Document.ShouldBeNull(why);
        result.Errors.ShouldContain(error => error.Path == path && error.Message.Contains(message, StringComparison.Ordinal), $"{why}:\n{string.Join('\n', result.Errors)}");
    }

    [Fact]
    public void Every_error_of_a_file_is_listed_at_once()
    {
        var tree = ShopSettingsSamples.ValidTree();
        tree["timeZone"] = "Mars/Olympus";
        Day(tree, "tue")[1]!["open"] = "13:00";
        Action(tree, "temporary-closure", 0)["kind"] = "instagram-post";
        Action(tree, "temporary-closure", 3)["mode"] = "auto";
        Template(tree, "close-early", "提早打烊");
        tree["scenarios"]!["reopen"]!["actions"] = new JsonArray();
        tree["scenarios"]!.AsObject().Remove("open-late");

        var result = ShopSettingsJson.Parse(tree.ToJsonString());

        result.Errors.Select(error => error.Path).ShouldBe(
            [
                "$.timeZone",
                "$.weeklyHours.tue[1]",
                $"{Closure}.actions[0].kind",
                $"{Closure}.actions[3].mode",
                "$.scenarios['close-early'].announcement.template",
                "$.scenarios['open-late']",
                "$.scenarios.reopen.actions",
            ],
            ignoreOrder: true);
        result.Errors.ShouldAllBe(error => error.ToString() == error.Path + "：" + error.Message);
    }

    [Fact]
    public void Text_that_is_not_JSON_and_JSON_that_is_not_an_object_are_refused()
    {
        var broken = ShopSettingsJson.Parse("{ \"storeName\": ");
        broken.Errors.ShouldHaveSingleItem().Path.ShouldBe("$");
        broken.Errors[0].Message.ShouldStartWith("不是有效的 JSON");

        ShopSettingsJson.Parse("[]").Errors.ShouldHaveSingleItem().ShouldBe(new ShopSettingsError("$", "必須是物件（{ … }）。"));
        ShopSettingsJson.Parse("{ \"storeName\": \"a\", \"storeName\": \"b\" }").Errors.ShouldContain(new ShopSettingsError("$.storeName", "欄位重複。"));
    }

    [Fact]
    public void Stored_sections_are_read_back_without_the_import_rules()
    {
        // Five periods a day, overlapping: refused on import, still readable once stored.
        var stored = """{"mon":[{"open":"06:00","close":"07:00"},{"open":"06:30","close":"09:00"},{"open":"10:00","close":"11:00"},{"open":"12:00","close":"13:00"},{"open":"14:00","close":"15:00"}],"tue":[],"wed":[],"thu":[],"fri":[],"sat":[],"sun":[]}""";

        var hours = ShopSettingsJson.ReadWeeklyHours(stored);

        hours[DayOfWeek.Monday].Count.ShouldBe(5);
        ShopSettingsJson.WriteWeeklyHours(hours).ShouldBe(stored);
        var settings = ShopSettingsSamples.ValidDocument();
        ShopSettingsJson.ReadScenarios(ShopSettingsJson.WriteScenarios(settings.Scenarios)).Keys.ShouldBe(Enum.GetValues<ShopScenario>(), ignoreOrder: true);
        Should.Throw<FormatException>(() => ShopSettingsJson.ReadWeeklyHours("""{"mon":[]}"""));
    }

    [Fact]
    public void A_change_names_the_sections_and_scenarios_that_changed()
    {
        var before = ShopSettingsSamples.ValidDocument();
        var tree = ShopSettingsSamples.ValidTree();
        Day(tree, "tue")[1]!["close"] = "21:30";
        Template(tree, "reopen", "{日期} 照常營業！");

        var change = ShopSettingsChange.Between(before, ShopSettingsSamples.Parse(tree.ToJsonString()));

        change.Sections.ShouldBe(["weeklyHours", "scenarios"]);
        change.Scenarios.ShouldBe([ShopScenario.Reopen]);
        ShopSettingsChange.Between(before, ShopSettingsSamples.ValidDocument()).IsEmpty.ShouldBeTrue();
    }

    private static JsonArray Day(JsonObject tree, string day) => tree["weeklyHours"]![day]!.AsArray();

    private static JsonArray Actions(JsonObject tree, string scenario) => tree["scenarios"]![scenario]!["actions"]!.AsArray();

    private static JsonObject Action(JsonObject tree, string scenario, int index) => Actions(tree, scenario)[index]!.AsObject();

    private static void Template(JsonObject tree, string scenario, string template) =>
        tree["scenarios"]![scenario]!["announcement"]!["template"] = template;

    [GeneratedRegex("```json\\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();

    /// <summary>Records holding lists compare them by reference; this compares a scenario by value.</summary>
    private sealed class ScenarioComparer : IEqualityComparer<ShopScenarioSettings>
    {
        public bool Equals(ShopScenarioSettings? x, ShopScenarioSettings? y) =>
            x is not null && y is not null && x.Enabled == y.Enabled && x.Announcement == y.Announcement && x.Actions.SequenceEqual(y.Actions);

        public int GetHashCode(ShopScenarioSettings obj) => obj.Enabled.GetHashCode();
    }
}
