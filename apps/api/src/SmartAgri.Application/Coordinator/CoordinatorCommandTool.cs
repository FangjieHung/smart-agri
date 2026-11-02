using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// What the model said a command is (<see cref="CoordinatorCommandTool.ParseCall"/>): the scenario
/// (<see langword="null"/> for <c>none</c> — not a command), and whatever it could tell for sure. The server
/// checks all of it again (<see cref="CoordinatorCommandPlanner"/>); nothing here is trusted.
/// </summary>
/// <param name="Reason">The reason the person gave, one line, at most <see cref="CoordinatorCommandTool.ReasonMaxLength"/>
/// characters; it only ever goes into the announcement.</param>
/// <param name="Question">The model's short follow-up question when the date or time is unclear.</param>
public sealed record CoordinatorCommandCall(ShopScenario? Scenario, DateOnly? Date, TimeOnly? Time, string? Reason, string? Question)
{
    /// <summary>Not a command (or no usable call at all).</summary>
    public static readonly CoordinatorCommandCall None = new(null, null, null, null, null);

    /// <summary>No text: the reason and question come from what a person wrote.</summary>
    public override string ToString() =>
        $"{nameof(CoordinatorCommandCall)} {{ Scenario = {Scenario}, Date = {Date}, Time = {Time} }}";
}

/// <summary>A command waiting for its date or time (<see cref="CoordinatorCommandTool.FollowUpPrompt"/>), and the
/// question it was asked when known.</summary>
public sealed record CoordinatorPendingDetails(ShopScenario Scenario, DateOnly? Date, TimeOnly? Time, string? Question = null)
{
    /// <summary>No question: it may quote what a person wrote.</summary>
    public override string ToString() =>
        $"{nameof(CoordinatorPendingDetails)} {{ Scenario = {Scenario}, Date = {Date}, Time = {Time} }}";
}

/// <summary>
/// The 統籌助理's one model tool (store-coordinator design §3 Q6/Q20, §5 Slice 3, #374):
/// <see cref="ToolName"/> returns a message's scenario, date, time and reason — or a question when the date
/// or time cannot be told for sure. Pure: the declaration, the prompts and parsing the reply; the call itself
/// (charged to the shop, purpose <c>coordinator-command</c>) is the Api's.
/// </summary>
/// <remarks>
/// The prompt gives the shop's local now (date, weekday, time), a 14-day calendar with weekdays, the week's
/// business hours and the four scenarios, and tells the model never to guess. The model's date is only a
/// reading of the calendar: the planner re-checks the range, the day off and the business hours.
/// </remarks>
public static partial class CoordinatorCommandTool
{
    public const string ToolName = "submit_store_command";

    public const string ScenarioParameter = "scenario";
    public const string DateParameter = "date";
    public const string TimeParameter = "time";
    public const string ReasonParameter = "reason";
    public const string QuestionParameter = "question";

    /// <summary><see cref="ScenarioParameter"/>'s value for "not a command".</summary>
    public const string NoScenario = "none";

    /// <summary>Longest reason kept (characters).</summary>
    public const int ReasonMaxLength = 50;

    /// <summary>Longest question kept (characters).</summary>
    public const int QuestionMaxLength = 100;

    /// <summary>Longest message sent to the model (characters); a command is a sentence.</summary>
    public const int MessageMaxLength = 500;

    /// <summary>Days in the prompt's calendar, today first.</summary>
    public const int CalendarDays = 14;

    private static readonly string[] WeekdayNames = ["日", "一", "二", "三", "四", "五", "六"];

    /// <summary>The tool, with every parameter required (null when unknown) and nothing else allowed.</summary>
    public static AIFunctionDeclaration Declaration()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [ScenarioParameter] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "情境：temporary-closure（臨時店休）、close-early（提早打烊）、open-late（延後開店）、reopen（恢復營業），不是這四種就填 none。",
                    ["enum"] = new JsonArray(
                        JsonValue.Create(WireNames<ShopScenario>.ToWire(ShopScenario.TemporaryClosure)),
                        JsonValue.Create(WireNames<ShopScenario>.ToWire(ShopScenario.Reopen)),
                        JsonValue.Create(WireNames<ShopScenario>.ToWire(ShopScenario.CloseEarly)),
                        JsonValue.Create(WireNames<ShopScenario>.ToWire(ShopScenario.OpenLate)),
                        JsonValue.Create(NoScenario)),
                },
                [DateParameter] = Nullable("店家當地的日期 yyyy-MM-dd；無法唯一確定就填 null。"),
                [TimeParameter] = Nullable("提早打烊的新打烊時間或延後開店的新開店時間，24 小時制 HH:mm；其他情境或無法唯一確定就填 null。"),
                [ReasonParameter] = Nullable($"使用者自己說的原因，{ReasonMaxLength} 字以內；沒說就填 null。"),
                [QuestionParameter] = Nullable("日期或時間無法唯一確定時，給使用者的一句簡短追問（繁體中文）；否則填 null。"),
            },
            ["required"] = new JsonArray(
                JsonValue.Create(ScenarioParameter),
                JsonValue.Create(DateParameter),
                JsonValue.Create(TimeParameter),
                JsonValue.Create(ReasonParameter),
                JsonValue.Create(QuestionParameter)),
            ["additionalProperties"] = false,
        };

        return AIFunctionFactory.CreateDeclaration(
            ToolName,
            "回報這句話是哪一種店務指令，以及能確定的日期、時間與原因。伺服器會再檢查所有內容，並依店家設定產生預覽卡。",
            JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString()));
    }

    /// <summary>The messages for a new command: the instructions with <paramref name="shop"/>'s facts at
    /// <paramref name="now"/>, then what the member wrote (at most <see cref="MessageMaxLength"/> characters).</summary>
    public static IReadOnlyList<ChatMessage> Prompt(ShopSettingsDocument shop, DateTimeOffset now, string text)
    {
        ArgumentNullException.ThrowIfNull(shop);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return [new ChatMessage(ChatRole.System, Instructions(shop, now)), new ChatMessage(ChatRole.User, Shorten(text.Trim()))];
    }

    /// <summary>The messages for an answer to the 統籌助理's question about <paramref name="pending"/>: the same
    /// instructions plus what is known and missing; the answer is the user message.</summary>
    public static IReadOnlyList<ChatMessage> FollowUpPrompt(ShopSettingsDocument shop, DateTimeOffset now, CoordinatorPendingDetails pending, string text)
    {
        ArgumentNullException.ThrowIfNull(shop);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var missing = pending.Date is null ? "日期" : "時間";
        var asked = string.IsNullOrWhiteSpace(pending.Question) ? string.Empty : $"你上一個追問是「{OneLine(pending.Question, QuestionMaxLength * 2)}」。";
        var context =
            $"\n\n這句話是在回答你上一個追問。{asked}上一個指令：{WireNames<ShopScenario>.ToWire(pending.Scenario)}（{ShopScenarioNames.Display(pending.Scenario)}）；" +
            $"已知日期：{(pending.Date is { } date ? Iso(date) : "未定")}；已知時間：{(pending.Time is { } time ? ShopAnnouncements.FormatTime(time) : "未定")}；缺少：{missing}。" +
            "請把這個回答和上一個指令、追問合起來呼叫工具（例如追問「下週要休哪一天？」時，「週四」是下週的週四）：scenario 沿用上一個指令（除非使用者明顯改成另一件事），已知的日期與時間照填，補上缺少的那一項；" +
            "回答仍然無法唯一確定時，那一項填 null 並再追問。如果使用者是在取消、說算了，或講的是無關的事，scenario 填 none。";
        return [new ChatMessage(ChatRole.System, Instructions(shop, now) + context), new ChatMessage(ChatRole.User, Shorten(text.Trim()))];
    }

    /// <summary>
    /// The first function call of <paramref name="response"/> when it is <see cref="ToolName"/>; anything
    /// else — no call, another tool, an unknown scenario — is <see cref="CoordinatorCommandCall.None"/>. A date
    /// that is not <c>yyyy-MM-dd</c> or a time that is not <c>HH:mm</c> is dropped (as if unknown); the reason
    /// and question are made one line and cut to their limits.
    /// </summary>
    public static CoordinatorCommandCall ParseCall(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().FirstOrDefault();
        if (call is null || !string.Equals(call.Name, ToolName, StringComparison.Ordinal))
        {
            return CoordinatorCommandCall.None;
        }

        var scenarioName = Argument(call, ScenarioParameter);
        if (scenarioName is null || scenarioName == NoScenario || !WireNames<ShopScenario>.All.Contains(scenarioName))
        {
            return CoordinatorCommandCall.None;
        }

        var scenario = WireNames<ShopScenario>.Parse(scenarioName);
        DateOnly? date = DateOnly.TryParseExact(Argument(call, DateParameter)?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
            ? parsedDate
            : null;
        TimeOnly? time = TimeOnly.TryParseExact(Argument(call, TimeParameter)?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime)
            ? parsedTime
            : null;
        return new CoordinatorCommandCall(
            scenario,
            date,
            CoordinatorCommand.NeedsTime(scenario) ? time : null,
            OneLine(Argument(call, ReasonParameter), ReasonMaxLength),
            OneLine(Argument(call, QuestionParameter), QuestionMaxLength));
    }

    /// <summary>The system message: the role, the shop's now, calendar and hours, the scenarios and the rules.</summary>
    private static string Instructions(ShopSettingsDocument shop, DateTimeOffset now)
    {
        var zone = shop.TimeZone;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var daysToMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        var nextMonday = today.AddDays(daysToMonday == 0 ? 7 : daysToMonday);
        var text = new StringBuilder();
        text.Append("你是「統籌助理」的指令判讀器，替店家「").Append(shop.StoreName)
            .Append("」理解老闆或店長在 LINE 傳來的一句話，只呼叫 ").Append(ToolName).Append(" 一次。\n");
        text.Append("現在是店家當地時間 ").Append(Iso(today)).Append("（週").Append(WeekdayNames[(int)today.DayOfWeek]).Append("）")
            .Append(local.ToString("HH:mm", CultureInfo.InvariantCulture)).Append("，時區 ").Append(zone.Id).Append("。\n");
        text.Append("接下來 ").Append(CalendarDays).Append(" 天的日曆（每週從週一開始；「下週」是 ").Append(Iso(nextMonday))
            .Append(" 到 ").Append(Iso(nextMonday.AddDays(6))).Append("）：\n");
        for (var offset = 0; offset < CalendarDays; offset++)
        {
            var day = today.AddDays(offset);
            text.Append("- ").Append(Iso(day)).Append("（週").Append(WeekdayNames[(int)day.DayOfWeek]).Append('）');
            if (offset is 0 or 1 or 2)
            {
                text.Append(offset switch { 0 => " 今天", 1 => " 明天", _ => " 後天" });
            }

            text.Append('\n');
        }

        text.Append("每週營業時間：");
        text.Append(string.Join("；", WeeklyHours.Days.Select(day =>
        {
            var periods = shop.WeeklyHours[day];
            return $"週{WeekdayNames[(int)day]} {(periods.Count == 0 ? "公休" : string.Join("、", periods.Select(period => period.ToString())))}";
        })));
        text.Append("（打烊時間比開店早表示到隔天）。\n");
        text.Append(
            "情境（scenario）：\n" +
            "- temporary-closure：臨時店休，那一天整天不營業（例如「今天臨時店休」「明天休息」）。\n" +
            "- close-early：提早打烊，那一天提早結束營業；time 是新的打烊時間。\n" +
            "- open-late：延後開店，那一天晚一點開門；time 是新的開店時間。\n" +
            "- reopen：恢復營業，取消先前說過的店休、提早打烊或延後開店；date 是要恢復的那一天，沒說就填 null，不用追問。\n" +
            "- none：不是以上任何一種（打招呼、問問題、說別的事）。\n" +
            "規則：\n" +
            "- date 用 yyyy-MM-dd，照上面的日曆換算「今天、明天、後天、下週一、11/15」這類說法；沒寫年份的日期取今天或之後最近的那一天。\n" +
            "- time 用 24 小時制 HH:mm，照營業時間判斷上午或下午（例如「四點打烊」在下午營業的店是 16:00，「中午」是 12:00）。\n" +
            "- 絕對不要猜：日期或時間無法唯一確定時（例如「下週休一天」、「改天」、沒說幾點的「提早打烊」），那一項填 null，" +
            "並在 question 寫一句簡短的繁體中文追問（例如「下週要休哪一天？」）。都能確定時 question 填 null。\n" +
            "- temporary-closure 與 reopen 的 time 填 null。\n" +
            "- reason 只填使用者自己說出的原因（例如「家裡有事」），沒說就填 null，不要自己編。\n" +
            "- 你不能執行任何動作，也不能寫公告；伺服器會再檢查日期與營業時間，並請使用者確認。");
        return text.ToString();
    }

    private static JsonObject Nullable(string description) => new()
    {
        ["type"] = new JsonArray(JsonValue.Create("string"), JsonValue.Create("null")),
        ["description"] = description,
    };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Shorten(string text) =>
        text.Length <= MessageMaxLength
            ? text
            : text[..(char.IsHighSurrogate(text[MessageMaxLength - 1]) ? MessageMaxLength - 1 : MessageMaxLength)];

    private static string? OneLine(string? text, int maxLength)
    {
        if (text is null)
        {
            return null;
        }

        var line = Whitespace().Replace(text, " ").Trim();
        if (line.Length > maxLength)
        {
            line = line[..(char.IsHighSurrogate(line[maxLength - 1]) ? maxLength - 1 : maxLength)].TrimEnd();
        }

        return line.Length == 0 || line is "null" ? null : line;
    }

    private static string? Argument(FunctionCallContent call, string name) =>
        call.Arguments is not null && call.Arguments.TryGetValue(name, out var value) ? AsText(value) : null;

    private static string? AsText(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonValue node when node.TryGetValue<string>(out var text) => text,
        _ => null,
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
