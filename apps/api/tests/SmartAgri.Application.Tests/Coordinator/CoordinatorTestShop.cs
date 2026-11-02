using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// The shop the command tests use (#374): 王記麵店 in Taipei, open 11:00–14:00 and 17:00–21:00, Wednesday off,
/// Saturday's dinner until 01:00; every scenario offered. 「Now」 is Monday 2026-11-02 10:00 in Taipei.
/// </summary>
internal static class CoordinatorTestShop
{
    public const string Json = """
        {
          "storeName": "王記麵店",
          "timeZone": "Asia/Taipei",
          "weeklyHours": {
            "mon": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "tue": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "wed": [],
            "thu": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "fri": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "sat": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "01:00" }],
            "sun": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }]
          },
          "scenarios": {
            "temporary-closure": {
              "enabled": true,
              "announcement": { "template": "【{店名}】{日期} 臨時店休，{恢復營業} 恢復營業。\n原因：{原因}" },
              "actions": [
                { "kind": "line-broadcast", "mode": "auto", "steps": "群發公告。" },
                { "kind": "google-special-hours", "mode": "reminder", "steps": "Google 設為休息。" },
                { "kind": "facebook-post", "mode": "auto", "steps": "FB 發文。" },
                { "kind": "ordering", "name": "Xurf", "mode": "reminder", "pauseSteps": "關閉 Xurf。", "resumeSteps": "打開 Xurf。" },
                { "kind": "ordering", "name": "foodpanda", "mode": "reminder", "pauseSteps": "暫停。", "resumeSteps": "恢復。" }
              ]
            },
            "close-early": {
              "enabled": true,
              "announcement": { "template": "【{店名}】{日期} 提早到 {時間} 打烊。\n原因：{原因}" },
              "actions": [
                { "kind": "line-broadcast", "mode": "auto", "steps": "群發公告。" },
                { "kind": "google-special-hours", "mode": "reminder", "steps": "Google 改時間。" },
                { "kind": "ordering", "name": "Xurf", "mode": "reminder", "pauseSteps": "關閉 Xurf。", "resumeSteps": "打開 Xurf。" }
              ]
            },
            "open-late": {
              "enabled": true,
              "announcement": { "template": "【{店名}】{日期} 延後到 {時間} 開店。" },
              "actions": [
                { "kind": "line-broadcast", "mode": "auto", "steps": "群發公告。" },
                { "kind": "ordering", "name": "Xurf", "mode": "reminder", "pauseSteps": "關閉 Xurf。", "resumeSteps": "打開 Xurf。" }
              ]
            },
            "reopen": { "enabled": true, "announcement": { "template": "【{店名}】{日期} 照常營業。" } }
          }
        }
        """;

    /// <summary>Monday 2026-11-02 10:00 in Taipei.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8));

    public static ShopSettingsDocument Settings(string json = Json)
    {
        var parsed = ShopSettingsJson.Parse(json);
        parsed.Errors.ShouldBeEmpty();
        return parsed.Document!;
    }
}
