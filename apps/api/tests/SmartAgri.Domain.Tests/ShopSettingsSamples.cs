using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>Shop settings files for the tests of #373: a small valid one to change, and the repository's
/// files.</summary>
internal static class ShopSettingsSamples
{
    /// <summary>A valid file: closed Mondays, lunch and dinner Tuesday to Sunday, Saturday's dinner until
    /// 02:00; every scenario on.</summary>
    public const string Valid = """
        {
          "storeName": "王記麵店",
          "timeZone": "Asia/Taipei",
          "weeklyHours": {
            "mon": [],
            "tue": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "wed": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "thu": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "fri": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "sat": [{ "open": "18:00", "close": "02:00" }],
            "sun": [{ "open": "11:00", "close": "14:00" }]
          },
          "scenarios": {
            "temporary-closure": {
              "enabled": true,
              "announcement": {
                "template": "{店名} {日期} 臨時店休，{恢復營業} 恢復營業。\n原因：{原因}",
                "imageUrl": "https://example.com/closed.jpg"
              },
              "actions": [
                { "kind": "line-broadcast", "mode": "auto", "steps": "到 LINE 官方帳號後台群發公告。" },
                { "kind": "google-special-hours", "mode": "reminder", "steps": "到 Google 商家把這天設為休息。" },
                { "kind": "facebook-post", "mode": "reminder", "steps": "到粉專發公告。" },
                { "kind": "ordering", "name": "Xurf", "mode": "reminder", "pauseSteps": "關閉 Xurf 接單。", "resumeSteps": "打開 Xurf 接單。" }
              ]
            },
            "close-early": {
              "enabled": true,
              "announcement": { "template": "{店名} {日期} 提早到 {時間} 打烊。\n原因：{原因}" },
              "actions": [
                { "kind": "line-broadcast", "mode": "auto", "steps": "到 LINE 官方帳號後台群發公告。" },
                { "kind": "ordering", "name": "Xurf", "mode": "reminder", "pauseSteps": "關閉 Xurf 接單。", "resumeSteps": "打開 Xurf 接單。" }
              ]
            },
            "open-late": {
              "enabled": false
            },
            "reopen": {
              "enabled": true,
              "announcement": { "template": "{店名} {日期} 照常營業。" }
            }
          }
        }
        """;

    /// <summary><see cref="Valid"/> as a JSON tree to change.</summary>
    public static JsonObject ValidTree() => JsonNode.Parse(Valid)!.AsObject();

    /// <summary><see cref="Valid"/>, parsed.</summary>
    public static ShopSettingsDocument ValidDocument() => Parse(Valid);

    /// <summary>Parses <paramref name="json"/>, which must be valid.</summary>
    public static ShopSettingsDocument Parse(string json)
    {
        var result = ShopSettingsJson.Parse(json);
        result.Errors.ShouldBeEmpty(string.Join('\n', result.Errors));
        return result.Document.ShouldNotBeNull();
    }

    /// <summary>A file of the repository, by its path from the root (the directory with <c>nx.json</c>).</summary>
    public static string RepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "nx.json")))
            {
                return Path.Combine([directory.FullName, .. path]);
            }
        }

        throw new InvalidOperationException("Could not find the repository root (nx.json) above " + AppContext.BaseDirectory);
    }
}
