using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// Announcement templates (store-coordinator design §3 Q13, §5 Slice 2, #373): filling one in — dates,
/// times, the reopening in the shop's time zone, the optional reason's line — and which variables each
/// scenario may and must use.
/// </summary>
public class ShopAnnouncementsTests
{
    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    [Fact]
    public void A_temporary_closure_with_a_reason()
    {
        var settings = ShopSettingsSamples.ValidDocument();
        var closed = new DateOnly(2026, 11, 3);

        var text = ShopAnnouncements.Render(
            settings,
            ShopScenario.TemporaryClosure,
            new AnnouncementValues(closed, Reopening: settings.Hours.FirstOpeningAfter(closed), Reason: "  家中有事  "));

        text.ShouldBe("王記麵店 11/3（二） 臨時店休，11/4（三）11:00 恢復營業。\n原因：家中有事");
    }

    [Fact]
    public void Without_a_reason_the_reason_line_is_left_out()
    {
        var settings = ShopSettingsSamples.ValidDocument();
        var values = new AnnouncementValues(new DateOnly(2026, 11, 8), Reopening: new DateTimeOffset(2026, 11, 10, 11, 0, 0, Taipei));

        var expected = "王記麵店 11/8（日） 臨時店休，11/10（二）11:00 恢復營業。";
        ShopAnnouncements.Render(settings, ShopScenario.TemporaryClosure, values).ShouldBe(expected);
        ShopAnnouncements.Render(settings, ShopScenario.TemporaryClosure, values with { Reason = " " }).ShouldBe(expected);
    }

    [Fact]
    public void Close_early_fills_in_the_new_closing_time()
    {
        var settings = ShopSettingsSamples.ValidDocument();

        ShopAnnouncements.Render(
                settings, ShopScenario.CloseEarly, new AnnouncementValues(new DateOnly(2026, 11, 2), Time: new TimeOnly(16, 0), Reason: "食材用完"))
            .ShouldBe("王記麵店 11/2（一） 提早到 16:00 打烊。\n原因：食材用完");
    }

    [Fact]
    public void The_reopening_is_shown_in_the_shops_time_zone()
    {
        var tree = ShopSettingsSamples.ValidTree();
        tree["timeZone"] = "Asia/Tokyo";
        var settings = ShopSettingsSamples.Parse(tree.ToJsonString());

        // 02:00 UTC is 11:00 in Tokyo.
        var text = ShopAnnouncements.Render(
            settings,
            ShopScenario.TemporaryClosure,
            new AnnouncementValues(new DateOnly(2026, 11, 3), Reopening: new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero)));

        text.ShouldContain("11/4（三）11:00 恢復營業");
    }

    [Fact]
    public void Values_are_put_in_once_as_they_are_and_lines_and_spacing_are_kept()
    {
        var tree = ShopSettingsSamples.ValidTree();
        tree["scenarios"]!["reopen"]!["announcement"]!["template"] = "【{店名}】\n\n{日期} 照常營業\n原因：{原因}（{原因}）\n— {店名}";
        var settings = ShopSettingsSamples.Parse(tree.ToJsonString());

        ShopAnnouncements.Render(settings, ShopScenario.Reopen, new AnnouncementValues(new DateOnly(2026, 11, 2), Reason: "{店名}"))
            .ShouldBe("【王記麵店】\n\n11/2（一） 照常營業\n原因：{店名}（{店名}）\n— 王記麵店");
        ShopAnnouncements.Render(settings, ShopScenario.Reopen, new AnnouncementValues(new DateOnly(2026, 11, 2)))
            .ShouldBe("【王記麵店】\n\n11/2（一） 照常營業\n— 王記麵店");
    }

    [Fact]
    public void A_missing_value_or_announcement_is_the_callers_mistake()
    {
        var settings = ShopSettingsSamples.ValidDocument();

        Should.Throw<ArgumentException>(() =>
            ShopAnnouncements.Render(settings, ShopScenario.CloseEarly, new AnnouncementValues(new DateOnly(2026, 11, 2))));
        Should.Throw<ArgumentException>(() =>
            ShopAnnouncements.Render(settings, ShopScenario.TemporaryClosure, new AnnouncementValues(new DateOnly(2026, 11, 2))));
        Should.Throw<InvalidOperationException>(() =>
            ShopAnnouncements.Render(settings, ShopScenario.OpenLate, new AnnouncementValues(new DateOnly(2026, 11, 2), Time: new TimeOnly(12, 0))));
    }

    [Fact]
    public void Dates_and_times_are_written_the_Taiwanese_way()
    {
        string[] week = ["11/1（日）", "11/2（一）", "11/3（二）", "11/4（三）", "11/5（四）", "11/6（五）", "11/7（六）"];
        for (var day = 0; day < 7; day++)
        {
            ShopAnnouncements.FormatDate(new DateOnly(2026, 11, 1 + day)).ShouldBe(week[day]);
        }

        ShopAnnouncements.FormatDate(new DateOnly(2027, 1, 10)).ShouldBe("1/10（日）");
        ShopAnnouncements.FormatTime(new TimeOnly(9, 5)).ShouldBe("09:05");
        ShopAnnouncements.FormatDateTime(new DateTimeOffset(2026, 11, 2, 16, 30, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"))
            .ShouldBe("11/3（二）00:30");
    }

    [Fact]
    public void Each_scenario_has_its_required_and_allowed_variables()
    {
        ShopAnnouncements.Problems(ShopScenario.TemporaryClosure, "{店名}{日期}{恢復營業}\n{原因}").ShouldBeEmpty();
        ShopAnnouncements.Problems(ShopScenario.CloseEarly, "{店名}{日期}{時間}\n{原因}").ShouldBeEmpty();
        ShopAnnouncements.Problems(ShopScenario.OpenLate, "{時間} 開店").ShouldBeEmpty();
        ShopAnnouncements.Problems(ShopScenario.Reopen, "{日期}").ShouldBeEmpty();

        ShopAnnouncements.Problems(ShopScenario.TemporaryClosure, "今天店休").ShouldBe(["必須包含 {日期}。"]);
        ShopAnnouncements.Problems(ShopScenario.OpenLate, "{日期} 延後開店").ShouldBe(["必須包含 {時間}。"]);
        ShopAnnouncements.Problems(ShopScenario.CloseEarly, "{時間} {恢復營業}").ShouldBe(["這個情境不能用 {恢復營業}；可用：{店名}、{日期}、{時間}、{原因}。"]);
        ShopAnnouncements.Problems(ShopScenario.Reopen, "{日期} {時間}").ShouldBe(["這個情境不能用 {時間}；可用：{店名}、{日期}、{原因}。"]);
        ShopAnnouncements.Problems(ShopScenario.Reopen, "{日期} {date}").ShouldBe(["不認得的變數 {date}；可用：{店名}、{日期}、{原因}。"]);
        ShopAnnouncements.Problems(ShopScenario.Reopen, "{日期} }").ShouldBe(["第 1 行：「}」沒有對應的「{」；大括號只能用來寫變數，例如 {日期}。"]);
        ShopAnnouncements.Problems(ShopScenario.Reopen, "{日期}\n{}").ShouldBe(["第 2 行：「{}」裡沒有變數名稱。"]);
        ShopAnnouncements.Problems(ShopScenario.Reopen, "{日期\n}").Count.ShouldBe(3, "two lone braces, and no {日期}");
        ShopAnnouncements.Problems(ShopScenario.CloseEarly, "{時間} 打烊，{原因}")
            .ShouldBe(["第 1 行：{原因} 要和 {時間} 分開放在不同行；沒有原因時含 {原因} 的整行會省略，{時間} 也會跟著不見。"]);
    }

    [Fact]
    public void The_full_width_braces_of_Chinese_text_are_plain_text()
    {
        var tree = ShopSettingsSamples.ValidTree();
        tree["scenarios"]!["reopen"]!["announcement"] = new JsonObject { ["template"] = "｛公告｝{日期} 照常營業" };

        ShopAnnouncements.Render(ShopSettingsSamples.Parse(tree.ToJsonString()), ShopScenario.Reopen, new AnnouncementValues(new DateOnly(2026, 11, 2)))
            .ShouldBe("｛公告｝11/2（一） 照常營業");
    }
}
