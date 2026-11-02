using System.Globalization;
using System.Text.Json.Nodes;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// What the 統籌助理 says about commands (store-coordinator design §5 Slice 3, #374): fixed texts, the
/// questions with their quick replies (「今天」「明天」 and LINE's date or time picker), and the choices of
/// shop and of what to reopen. The preview card itself is <see cref="CoordinatorPreviewCard"/>.
/// </summary>
public static class CoordinatorCommandReplies
{
    public const string NotConfigured = "這家店還沒設定店務指令，請聯絡平台營運者。";

    public const string NoPreviewToEdit = "目前沒有待確認的預覽卡。";

    public const string EmptyEdit = "請在「改成：」後面寫新的公告內容。";

    public const string NoAnnouncementToEdit = "這張預覽卡沒有公告可以修改。";

    public const string ChooseShop = "哪一家？";

    public const string AllShops = "全部";

    public const string RetypeCommand = "請重新輸入指令。";

    public const string Expired = "已失效，請重新下指令。";

    public const string AlreadyConfirmed = "這張預覽卡已經確認過了。";

    public const string AlreadyCancelled = "這張預覽卡已經取消了。";

    public const string Outdated = "預覽卡已更新，請按最新的那張。";

    public const string AlreadyAnswered = "這個問題已經回答過了。";

    public const string NotAMember = "你沒有綁定這家店，不能操作這張預覽卡。";

    public const string Cancelled = "已取消。";

    public const string NothingToReopen = "目前沒有可以恢復的店休或營業時間調整。";

    public const string AlreadyReopened = "這次的店休或營業時間調整已經恢復過了。";

    public const string ChooseReopen = "要恢復哪一次？";

    public const string ModelUnavailable = "抱歉，現在無法理解指令，請稍後再試。";

    public const string QuotaExceeded = "抱歉，這家店本月的 AI 用量已達上限，暫時無法理解指令，請聯絡平台營運者。";

    /// <summary>Shown above the menu when an answer to a question turned out not to be one.</summary>
    public const string PendingDropped = "已取消上一個指令。";

    /// <summary>Under the announcement of a card (Q13).</summary>
    public const string EditHint = "要修改公告，回覆「改成：新的內容」";

    /// <summary>On every card that stops new orders (Q17).</summary>
    public const string OrdersStillToHandle = "已接的訂單仍需處理";

    /// <summary>The most shops 「全部」 is offered for: one reply holds at most this many messages, one per shop.</summary>
    public const int AllShopsMax = LineMessages.MaxPerRequest;

    /// <summary>「已確認：{店名} {日期} {情境}。」 (with the time for 提早打烊／延後開店): the alt text of the report
    /// sent on 〔確認〕 (<see cref="CoordinatorExecutionReport.Confirmation"/>, #375).</summary>
    public static string Confirmed(string storeName, ShopScenario scenario, DateOnly date, TimeOnly? time) =>
        $"已確認：{storeName} {ShopAnnouncements.FormatDate(date)} {Title(scenario, time)}。";

    /// <summary>「臨時店休」, 「提早打烊 16:00」, 「延後開店 12:00」, 「恢復營業」.</summary>
    public static string Title(ShopScenario scenario, TimeOnly? time) =>
        time is { } clock && CoordinatorCommand.NeedsTime(scenario)
            ? $"{ShopScenarioNames.Display(scenario)} {ShopAnnouncements.FormatTime(clock)}"
            : ShopScenarioNames.Display(scenario);

    public static string ScenarioDisabled(ShopScenario scenario) => $"這家店沒有開啟「{ShopScenarioNames.Display(scenario)}」。";

    public static string DayOff(DateOnly date) => $"{ShopAnnouncements.FormatDate(date)} 本來就是公休日。";

    public static string AskDate(ShopScenario scenario) => scenario switch
    {
        ShopScenario.CloseEarly => "哪一天要提早打烊？",
        ShopScenario.OpenLate => "哪一天要延後開店？",
        _ => "要休哪一天？",
    };

    public static string AskTime(ShopScenario scenario) =>
        scenario == ShopScenario.OpenLate ? "要延後到幾點開店？" : "要提早到幾點打烊？";

    public static string DatePassed(DateOnly date, ShopScenario scenario) =>
        $"{ShopAnnouncements.FormatDate(date)} 已經過了。{AskDate(scenario)}";

    public static string DateTooFar(DateOnly last, ShopScenario scenario) =>
        $"最遠只能排到 {ShopAnnouncements.FormatDate(last)}。{AskDate(scenario)}";

    public static string TimeOutsideHours(ShopScenario scenario, DateOnly date, IReadOnlyList<BusinessPeriod> periods)
    {
        var what = scenario == ShopScenario.OpenLate ? "新的開店時間" : "新的打烊時間";
        var first = ShopAnnouncements.FormatTime(periods.OrderBy(period => period.Open).First().Open);
        var last = ShopAnnouncements.FormatTime(LastClose(periods));
        return $"{ShopAnnouncements.FormatDate(date)} 的營業時間是 {string.Join("、", periods.Select(period => period.ToString()))}，"
            + $"{what}要在 {first} 之後、{last} 之前。{AskTime(scenario)}";
    }

    public static string TimePassed(ShopScenario scenario, DateOnly date, TimeOnly time) =>
        $"{ShopAnnouncements.FormatDate(date)} {ShopAnnouncements.FormatTime(time)} 已經過了。{AskTime(scenario)}";

    /// <summary>「【{店名}】{text}」: a text reply about one of several shops (「全部」).</summary>
    public static string ForShop(string storeName, string text) => $"【{storeName}】{text}";

    /// <summary>
    /// A date question about command <paramref name="commandId"/>: <paramref name="text"/> with quick replies
    /// 「今天」, 「明天」 (postbacks carrying the date) and a date picker from <paramref name="today"/> to
    /// <paramref name="last"/>.
    /// </summary>
    public static JsonObject DateQuestion(string text, Guid organizationId, Guid commandId, DateOnly today, DateOnly last)
    {
        var items = new JsonArray
        {
            QuickPostback("今天", CoordinatorMessageRouter.DatePostback(organizationId, commandId, today), "今天"),
            QuickPostback("明天", CoordinatorMessageRouter.DatePostback(organizationId, commandId, today.AddDays(1)), "明天"),
            new JsonObject
            {
                ["type"] = "action",
                ["action"] = new JsonObject
                {
                    ["type"] = "datetimepicker",
                    ["label"] = "選日期",
                    ["data"] = CoordinatorMessageRouter.DatePostback(organizationId, commandId),
                    ["mode"] = "date",
                    ["initial"] = Iso(today),
                    ["min"] = Iso(today),
                    ["max"] = Iso(last),
                },
            },
        };
        return WithQuickReply(text, items);
    }

    /// <summary>A time question about command <paramref name="commandId"/>: <paramref name="text"/> with a
    /// time picker.</summary>
    public static JsonObject TimeQuestion(string text, Guid organizationId, Guid commandId)
    {
        var items = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "action",
                ["action"] = new JsonObject
                {
                    ["type"] = "datetimepicker",
                    ["label"] = "選時間",
                    ["data"] = CoordinatorMessageRouter.TimePostback(organizationId, commandId),
                    ["mode"] = "time",
                },
            },
        };
        return WithQuickReply(text, items);
    }

    /// <summary>
    /// 「哪一家？」 with one button per shop (labels cut to <see cref="CoordinatorReplies.QuickReplyLabelMaxLength"/>)
    /// and, for at most <see cref="AllShopsMax"/> shops, 「全部」; at most
    /// <see cref="CoordinatorReplies.QuickReplyMaxItems"/> buttons.
    /// </summary>
    public static JsonObject ShopChoice(IReadOnlyList<(Guid OrganizationId, string StoreName)> shops)
    {
        ArgumentNullException.ThrowIfNull(shops);
        var offerAll = shops.Count <= AllShopsMax;
        var items = new JsonArray();
        foreach (var (organizationId, storeName) in shops.Take(CoordinatorReplies.QuickReplyMaxItems - (offerAll ? 1 : 0)))
        {
            items.Add(QuickPostback(CoordinatorReplies.Label(storeName), CoordinatorMessageRouter.ShopPostback(organizationId), storeName));
        }

        if (offerAll)
        {
            items.Add(QuickPostback(AllShops, CoordinatorMessageRouter.ShopPostback(null), AllShops));
        }

        return WithQuickReply(ChooseShop, items);
    }

    /// <summary>「要恢復哪一次？」 with one button per confirmed command that can be undone (at most
    /// <see cref="CoordinatorReplies.QuickReplyMaxItems"/>, earliest date first).</summary>
    public static JsonObject ReopenChoice(Guid organizationId, IReadOnlyList<(Guid CommandId, ShopScenario Scenario, DateOnly Date, TimeOnly? Time)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var items = new JsonArray();
        foreach (var candidate in candidates.Take(CoordinatorReplies.QuickReplyMaxItems))
        {
            var label = $"{ShopAnnouncements.FormatDate(candidate.Date)}{Title(candidate.Scenario, candidate.Time)}";
            items.Add(QuickPostback(CoordinatorReplies.Label(label), CoordinatorMessageRouter.ReopenPostback(organizationId, candidate.CommandId), $"恢復 {label}"));
        }

        return WithQuickReply(ChooseReopen, items);
    }

    private static JsonObject QuickPostback(string label, string data, string displayText) => new()
    {
        ["type"] = "action",
        ["action"] = new JsonObject
        {
            ["type"] = "postback",
            ["label"] = label,
            ["data"] = data,
            ["displayText"] = displayText.Length <= 300 ? displayText : displayText[..300],
        },
    };

    private static JsonObject WithQuickReply(string text, JsonArray items)
    {
        var message = LineMessages.Text(text);
        message["quickReply"] = new JsonObject { ["items"] = items };
        return message;
    }

    private static TimeOnly LastClose(IReadOnlyList<BusinessPeriod> periods)
    {
        // The period that closes last on the wall clock: an overnight one, else the latest closing.
        var overnight = periods.Where(period => period.IsOvernight).ToList();
        return overnight.Count > 0 ? overnight.Max(period => period.Close) : periods.Max(period => period.Close);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
