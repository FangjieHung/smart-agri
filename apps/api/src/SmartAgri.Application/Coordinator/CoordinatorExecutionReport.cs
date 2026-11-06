using System.Text.Json.Nodes;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>One action in a report: the row as it is now, and the scenario, date and time of its command (which
/// name it).</summary>
public sealed record CoordinatorReportLine(
    Guid ActionId,
    ShopActionKind Kind,
    string? Name,
    CoordinatorActionStep Step,
    ShopActionMode Mode,
    CoordinatorActionStatus Status,
    DateTimeOffset DueAt,
    string StepsText,
    string? ImageUrl,
    string? FailureCode,
    ShopScenario Scenario,
    DateOnly Date,
    TimeOnly? Time)
{
    /// <summary><paramref name="row"/> of <paramref name="command"/>.</summary>
    public static CoordinatorReportLine Of(CoordinatorCommandAction row, CoordinatorCommand command)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(command);
        return new CoordinatorReportLine(
            row.Id, row.Kind, row.Name, row.Step, row.Mode, row.Status, row.DueAt, row.StepsText, row.ImageUrl, row.FailureCode,
            command.Scenario, command.Date!.Value, command.Time);
    }

    /// <summary>What the line is about, e.g. 「Xurf 暫停接單」 (<see cref="CoordinatorExecutionReport.Label"/>).</summary>
    public string Label => CoordinatorExecutionReport.Label(Kind, Name, Step, Scenario, Date, Time);

    /// <summary>A LINE 群發 or FB 貼文 the owner posts by hand: they need the announcement.</summary>
    public bool NeedsAnnouncement =>
        Status == CoordinatorActionStatus.AwaitingManual
        && Step == CoordinatorActionStep.Apply
        && Kind is ShopActionKind.LineBroadcast or ShopActionKind.FacebookPost;
}

/// <summary>Everything a report shows: the command (<see cref="Undone"/> for a reopen), its lines in order, the
/// announcement to copy when a line needs it, and the shop's zone and now for the times.</summary>
public sealed record CoordinatorReportContent(
    Guid OrganizationId,
    string StoreName,
    TimeZoneInfo Zone,
    ShopScenario Scenario,
    DateOnly Date,
    TimeOnly? Time,
    IReadOnlyList<CoordinatorReportLine> Lines,
    string? Announcement,
    DateTimeOffset Now,
    (ShopScenario Scenario, DateOnly Date, TimeOnly? Time)? Undone = null)
{
    /// <summary>No announcement: it may hold a reason a person wrote.</summary>
    public override string ToString() =>
        $"{nameof(CoordinatorReportContent)} {{ Scenario = {Scenario}, Date = {Date}, Lines = {Lines.Count} }}";
}

/// <summary>
/// What the 統籌助理 says about carrying a command out (store-coordinator design §1, §3 Q8, Q18, Q25, §5 Slice 4, #375):
/// the report right after 〔確認〕, the follow-up once its automatic actions have run, and the reminder pushed when an
/// ordering system is due — LINE Flex messages built purely from their content.
/// </summary>
/// <remarks>
/// <para>
/// One line per action: ✅ done (automatically, or by hand), ❌ failed with the reason and 〔重試〕, ⏳ a reminder now
/// with its steps and 〔已完成〕, ⌛ 「執行中…」 while an automatic action runs, 🕒 when a later reminder comes
/// (「11/2（一）10:30 會提醒你關閉接單」), 🚫 dropped by a reopen. When a LINE 群發 or FB 貼文 is the owner's to post,
/// the announcement follows as a plain text message they can copy (with the image's address on the line).
/// </para>
/// <para>
/// LINE limits: <c>altText</c> at most 400 characters, button labels at most 20, postback data at most 300, every
/// text non-empty; a reply or push holds at most five messages (here at most two).
/// </para>
/// </remarks>
public static class CoordinatorExecutionReport
{
    public const string DoneLabel = "已完成";

    public const string RetryLabel = "重試";

    /// <summary>The first line of the report right after 〔確認〕.</summary>
    public const string ConfirmedTitle = "已確認，逐項結果：";

    /// <summary>The first line of a follow-up.</summary>
    public const string FollowUpTitle = "執行結果：";

    public const string Running = "執行中…";

    /// <summary>A report without lines (e.g. a reopen of a command whose only action was a LINE 群發 already sent).</summary>
    public const string NothingToDo = "・沒有需要執行的動作";

    public const string AlreadyRecorded = "這一項已經記錄完成了。";

    public const string ActionCancelled = "這一項已取消，不需要處理。";

    public const string NotManual = "這一項不需要手動處理。";

    public const string NotAMember = "你沒有綁定這家店，不能操作這一項。";

    public const string UnknownAction = "找不到這一項。";

    public const string AlreadySucceeded = "這一項已經完成，不需要重試。";

    public const string RetryRunning = "這一項正在執行中。";

    public const string RetryCancelled = "這一項已取消，不需要重試。";

    public const string RetryManual = "這一項已改成手動處理。";

    private const int AltTextMaxLength = CoordinatorPreviewCard.AltTextMaxLength;

    /// <summary>「已記錄：{項目} 已完成。」: the answer to 〔已完成〕.</summary>
    public static string Recorded(string label) => $"已記錄：{label} 已完成。";

    /// <summary>What a row is about: 「LINE 群發公告」, 「Google 特殊營業時間：11/2（一） 休息」, 「Xurf 暫停接單」,
    /// 「FB 粉專：刪除公告貼文」, … (the preview card's words).</summary>
    public static string Label(ShopActionKind kind, string? name, CoordinatorActionStep step, ShopScenario scenario, DateOnly date, TimeOnly? time)
    {
        var day = ShopAnnouncements.FormatDate(date);
        var at = time is { } clock ? ShopAnnouncements.FormatTime(clock) : string.Empty;
        return (kind, step) switch
        {
            (ShopActionKind.LineBroadcast, _) => scenario == ShopScenario.Reopen ? "LINE 群發更正公告" : "LINE 群發公告",
            (ShopActionKind.GoogleSpecialHours, CoordinatorActionStep.Undo) => $"Google 特殊營業時間：移除 {day} 的設定",
            (ShopActionKind.GoogleSpecialHours, _) => scenario switch
            {
                ShopScenario.CloseEarly => $"Google 特殊營業時間：{day} {at} 打烊",
                ShopScenario.OpenLate => $"Google 特殊營業時間：{day} {at} 開店",
                _ => $"Google 特殊營業時間：{day} 休息",
            },
            (ShopActionKind.FacebookPost, CoordinatorActionStep.Undo) => "FB 粉專：刪除公告貼文",
            (ShopActionKind.FacebookPost, _) => "FB 粉專貼文",
            (ShopActionKind.Ordering, CoordinatorActionStep.Pause) => $"{name} 暫停接單",
            (ShopActionKind.Ordering, _) => $"{name} 重新開啟接單",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an action kind."),
        };
    }

    /// <summary>The report right after 〔確認〕 (alt text 「已確認：{店名} {日期} {情境}。」), and the announcement to copy
    /// when the owner posts it by hand.</summary>
    public static IReadOnlyList<JsonObject> Confirmation(CoordinatorReportContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var altText = CoordinatorCommandReplies.Confirmed(content.StoreName, content.Scenario, content.Date, content.Time);
        return Messages(content, ConfirmedTitle, altText);
    }

    /// <summary>The results of the automatic actions that have run (alt text 「執行結果：{店名} {日期} {情境}」), and the
    /// announcement to copy when one of them became the owner's to post.</summary>
    public static IReadOnlyList<JsonObject> FollowUp(CoordinatorReportContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var altText = $"執行結果：{content.StoreName} {ShopAnnouncements.FormatDate(content.Date)} {CoordinatorCommandReplies.Title(content.Scenario, content.Time)}";
        return Messages(content, FollowUpTitle, altText);
    }

    /// <summary>
    /// The reminder pushed when <paramref name="line"/> is due (alt text 「提醒：{店名} {項目}」): the command, the steps,
    /// 〔已完成〕; for a LINE 群發 or FB 貼文 also the announcement to copy.
    /// </summary>
    public static IReadOnlyList<JsonObject> Reminder(Guid organizationId, string storeName, CoordinatorReportLine line, string? announcement)
    {
        ArgumentNullException.ThrowIfNull(storeName);
        ArgumentNullException.ThrowIfNull(line);
        var body = new JsonArray
        {
            Text($"{ShopAnnouncements.FormatDate(line.Date)} {CoordinatorCommandReplies.Title(line.Scenario, line.Time)}", "xs", color: "#666666"),
            Text($"⏳ {line.Label}", "sm", weight: "bold"),
            Text(line.StepsText, "sm"),
        };
        if (line.ImageUrl is { Length: > 0 } image)
        {
            body.Add(Text($"圖片：{image}", "xs", color: "#666666"));
        }

        var bubble = new JsonObject
        {
            ["type"] = "bubble",
            ["header"] = Box("vertical", new JsonArray { Text($"{storeName}｜提醒", "md", weight: "bold") }),
            ["body"] = Box("vertical", body, spacing: "sm"),
            ["footer"] = Box("vertical", new JsonArray { Button(DoneLabel, CoordinatorMessageRouter.CompletePostback(organizationId, line.ActionId), "primary") }),
        };
        var messages = new List<JsonObject> { Flex(Cut($"提醒：{storeName} {line.Label}"), bubble) };
        if (line.Step == CoordinatorActionStep.Apply && line.Kind is ShopActionKind.LineBroadcast or ShopActionKind.FacebookPost
            && announcement is { Length: > 0 })
        {
            messages.Add(LineMessages.Text(announcement));
        }

        return messages;
    }

    private static IReadOnlyList<JsonObject> Messages(CoordinatorReportContent content, string title, string altText)
    {
        var body = new JsonArray { Text(title, "sm", weight: "bold") };
        if (content.Undone is { } undone)
        {
            body.Add(Text($"取消 {ShopAnnouncements.FormatDate(undone.Date)} {CoordinatorCommandReplies.Title(undone.Scenario, undone.Time)}", "sm"));
        }

        var lines = new JsonArray();
        foreach (var line in content.Lines)
        {
            foreach (var node in Line(content, line))
            {
                lines.Add(node);
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(Text(NothingToDo, "sm"));
        }

        body.Add(Box("vertical", lines, spacing: "sm"));
        var bubble = new JsonObject
        {
            ["type"] = "bubble",
            ["header"] = Box("vertical", new JsonArray { Text(Header(content), "md", weight: "bold") }),
            ["body"] = Box("vertical", body, spacing: "md"),
        };

        var messages = new List<JsonObject> { Flex(Cut(altText), bubble) };
        if (content.Announcement is { Length: > 0 } announcement && content.Lines.Any(line => line.NeedsAnnouncement))
        {
            messages.Add(LineMessages.Text(announcement));
        }

        return messages;
    }

    private static string Header(CoordinatorReportContent content) =>
        $"{content.StoreName}｜{ShopAnnouncements.FormatDate(content.Date)}{CoordinatorCommandReplies.Title(content.Scenario, content.Time)}";

    private static IEnumerable<JsonObject> Line(CoordinatorReportContent content, CoordinatorReportLine line)
    {
        var label = line.Label;
        switch (line.Status)
        {
            case CoordinatorActionStatus.Succeeded:
                yield return Text($"✅ {label}", "sm");
                break;

            case CoordinatorActionStatus.DoneManually:
                yield return Text($"✅ {label}（已手動完成）", "sm");
                break;

            case CoordinatorActionStatus.Failed:
                yield return Text($"❌ {label}：{CoordinatorFailureCodes.Reason(line.FailureCode)}", "sm");
                yield return Button(RetryLabel, CoordinatorMessageRouter.RetryPostback(content.OrganizationId, line.ActionId), "secondary");
                break;

            case CoordinatorActionStatus.AwaitingManual:
                yield return Text($"⏳ {label}", "sm");
                yield return Text($"　{line.StepsText}", "xs", color: "#666666");
                if (line.ImageUrl is { Length: > 0 } image)
                {
                    yield return Text($"　圖片：{image}", "xs", color: "#666666");
                }

                yield return Button(DoneLabel, CoordinatorMessageRouter.CompletePostback(content.OrganizationId, line.ActionId), "secondary");
                break;

            case CoordinatorActionStatus.Scheduled when line.Mode == ShopActionMode.Auto && line.DueAt <= content.Now:
                yield return Text($"⌛ {label}：{Running}", "sm");
                break;

            case CoordinatorActionStatus.Scheduled:
                yield return Text($"🕒 {label}", "sm");
                var when = ShopAnnouncements.FormatDateTime(line.DueAt, content.Zone);
                yield return Text(line.Mode == ShopActionMode.Auto ? $"　{when} 會自動執行" : $"　{when} 會提醒你{What(line.Step)}", "xs", color: "#666666");
                break;

            case CoordinatorActionStatus.Cancelled:
                yield return Text($"🚫 {label}：已取消排程", "sm");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(line), line.Status, "Not an action status.");
        }
    }

    private static string What(CoordinatorActionStep step) => step switch
    {
        CoordinatorActionStep.Pause => "關閉接單",
        CoordinatorActionStep.Resume => "重新開啟接單",
        _ => "處理",
    };

    private static string Cut(string text) => text.Length <= AltTextMaxLength ? text : text[..AltTextMaxLength];

    private static JsonObject Flex(string altText, JsonObject bubble) => new()
    {
        ["type"] = "flex",
        ["altText"] = altText,
        ["contents"] = bubble,
    };

    private static JsonObject Box(string layout, JsonArray contents, string? spacing = null)
    {
        var box = new JsonObject { ["type"] = "box", ["layout"] = layout, ["contents"] = contents };
        if (spacing is not null)
        {
            box["spacing"] = spacing;
        }

        return box;
    }

    private static JsonObject Text(string text, string size, string? weight = null, string? color = null)
    {
        var node = new JsonObject { ["type"] = "text", ["text"] = text, ["wrap"] = true, ["size"] = size };
        if (weight is not null)
        {
            node["weight"] = weight;
        }

        if (color is not null)
        {
            node["color"] = color;
        }

        return node;
    }

    private static JsonObject Button(string label, string data, string style) => new()
    {
        ["type"] = "button",
        ["style"] = style,
        ["height"] = "sm",
        ["action"] = new JsonObject
        {
            ["type"] = "postback",
            ["label"] = label,
            ["data"] = data,
            ["displayText"] = label,
        },
    };
}
