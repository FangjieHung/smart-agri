using System.Text.Json.Nodes;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// One action line of a preview card: what will happen, how (<see cref="Mode"/>; <see langword="null"/> for an
/// informational line), when (<see cref="Detail"/>) and <see cref="Note"/>.
/// </summary>
/// <param name="Note">
/// Shown under the line when set: the LINE 群發 line's 「約 N 則（好友 F－封鎖 B），本月剩 M 則」 (Q14,
/// <see cref="CoordinatorLineBroadcast.Note"/>, #376), from <see cref="ICoordinatorActionNotes"/>.
/// </param>
public sealed record CoordinatorCardRow(string Label, ShopActionMode? Mode, string? Detail = null, string? Note = null);

/// <summary>What a reopen card undoes, and its correction broadcast (Q21).</summary>
/// <param name="CorrectionOffered">The shop has a reopen announcement and the undone command had a LINE 群發:
/// the card has the 「加發更正公告」 toggle.</param>
/// <param name="CorrectionBroadcast">The toggle is on (off by default).</param>
public sealed record CoordinatorReopenDetails(
    ShopScenario UndoneScenario, DateOnly UndoneDate, TimeOnly? UndoneTime, bool CorrectionOffered, bool CorrectionBroadcast);

/// <summary>Everything a preview card shows, for version <see cref="Version"/> of command <see cref="CommandId"/>.</summary>
/// <param name="Announcement">The announcement as it will be sent; for a reopen, the correction (shown only with
/// the toggle on).</param>
/// <param name="Reopen">Set exactly for a <see cref="ShopScenario.Reopen"/> card.</param>
/// <param name="Reservations">The day's reservations the command would cancel, or — a reopen — that stay cancelled
/// (#392); no line when there are none.</param>
public sealed record CoordinatorCardContent(
    Guid OrganizationId,
    Guid CommandId,
    int Version,
    string StoreName,
    ShopScenario Scenario,
    DateOnly Date,
    TimeOnly? Time,
    IReadOnlyList<CoordinatorCardRow> Rows,
    string? Announcement,
    CoordinatorReopenDetails? Reopen = null,
    CoordinatorCardReservations? Reservations = null)
{
    /// <summary>No announcement: it may hold a reason a person wrote.</summary>
    public override string ToString() =>
        $"{nameof(CoordinatorCardContent)} {{ CommandId = {CommandId}, Version = {Version}, Scenario = {Scenario}, Date = {Date} }}";
}

/// <summary>
/// The preview card (store-coordinator design §1, §3 Q4/Q13/Q14/Q17/Q18/Q21, §5 Slice 3, #374): a LINE Flex
/// message built purely from <see cref="CoordinatorCardContent"/>. Header 「{店名}｜{日期}{情境}」 (with the time
/// for 提早打烊／延後開店); one line per action with its mode (自動／提醒) and, for ordering systems, when the
/// reminders come; the announcement and how to edit it; 「已接的訂單仍需處理」; 〔確認〕〔取消〕 whose postbacks carry
/// the command id and card version. A reopen card says what it undoes and has the 「加發更正公告」 toggle when
/// offered. Under the actions, the online reservations of the day (#392): 「會取消 N 筆訂位（已接受 a 筆、待處理 b 筆）」
/// with the first <see cref="CoordinatorReservationClosures.CardListMax"/> as 「18:30 陳小姐」 and 「還有 M 筆」; on a
/// reopen card 「因店休取消的 N 筆訂位不會恢復，請視需要聯絡顧客」. No line when there are none.
/// </summary>
/// <remarks>
/// LINE limits: <c>altText</c> at most 400 characters, button labels at most 20 (the brief's limit), postback
/// data at most 300 (ids are 32 hexadecimal digits each), every text non-empty.
/// </remarks>
public static class CoordinatorPreviewCard
{
    public const int AltTextMaxLength = 400;

    public const string ConfirmLabel = "確認";

    public const string CancelLabel = "取消";

    public const string CorrectionOnLabel = "加發更正公告";

    public const string CorrectionOffLabel = "不加發更正公告";

    /// <summary>「自動」 or 「提醒」.</summary>
    public static string ModeName(ShopActionMode mode) => mode == ShopActionMode.Auto ? "自動" : "提醒";

    /// <summary>「{店名} {日期} {情境}預覽」.</summary>
    public static string AltText(CoordinatorCardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = $"{content.StoreName} {ShopAnnouncements.FormatDate(content.Date)} {CoordinatorCommandReplies.Title(content.Scenario, content.Time)}預覽";
        return text.Length <= AltTextMaxLength ? text : text[..AltTextMaxLength];
    }

    /// <summary>「{店名}｜{日期}{情境}」.</summary>
    public static string Header(CoordinatorCardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return $"{content.StoreName}｜{ShopAnnouncements.FormatDate(content.Date)}{CoordinatorCommandReplies.Title(content.Scenario, content.Time)}";
    }

    /// <summary>
    /// The lines of a card for <paramref name="scenario"/> on <paramref name="date"/>: one per action of the
    /// scenario's settings, in their order, with <paramref name="modes"/> (one per action, from
    /// <see cref="ICoordinatorActionModes"/>). Ordering systems say when they will be reminded (Q7, Q20).
    /// </summary>
    public static IReadOnlyList<CoordinatorCardRow> Rows(
        ShopSettingsDocument settings, ShopScenario scenario, DateOnly date, TimeOnly? time, IReadOnlyList<ShopActionMode> modes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modes);
        var actions = settings[scenario].Actions;
        CheckModes(actions, modes);
        var day = ShopAnnouncements.FormatDate(date);
        var at = time is { } clock ? ShopAnnouncements.FormatTime(clock) : string.Empty;
        var resumes = CoordinatorCommandPlanner.ResumesAt(settings, scenario, date, time) is { } instant
            ? ShopAnnouncements.FormatDateTime(instant, settings.TimeZone)
            : null;
        return
        [
            .. actions.Select((action, index) => action.Kind switch
            {
                ShopActionKind.LineBroadcast => new CoordinatorCardRow("LINE 群發公告", modes[index]),
                ShopActionKind.GoogleSpecialHours => new CoordinatorCardRow(scenario switch
                {
                    ShopScenario.CloseEarly => $"Google 特殊營業時間：{day} {at} 打烊",
                    ShopScenario.OpenLate => $"Google 特殊營業時間：{day} {at} 開店",
                    _ => $"Google 特殊營業時間：{day} 休息",
                }, modes[index]),
                ShopActionKind.FacebookPost => new CoordinatorCardRow("FB 粉專貼文", modes[index]),
                ShopActionKind.Ordering => new CoordinatorCardRow(
                    $"{action.Name} 暫停接單",
                    modes[index],
                    (scenario == ShopScenario.CloseEarly ? $"{day} {at} 前提醒關閉" : $"{day} 開店前提醒關閉")
                        + (resumes is null ? string.Empty : $"；{resumes} 前提醒重新開啟")),
                _ => throw new ArgumentOutOfRangeException(nameof(settings), action.Kind, "Not an action kind."),
            }),
        ];
    }

    /// <summary>
    /// The lines of a reopen card undoing a <paramref name="undone"/> command on <paramref name="date"/>: what each
    /// action of that scenario's settings does to undo itself (Q21), with <paramref name="modes"/> (one per
    /// action). LINE 群發 cannot be taken back: with <paramref name="correction"/> on it sends the correction
    /// announcement, otherwise the line only says so.
    /// </summary>
    public static IReadOnlyList<CoordinatorCardRow> ReopenRows(
        ShopSettingsDocument settings, ShopScenario undone, DateOnly date, IReadOnlyList<ShopActionMode> modes, bool correction)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ReopenRows(settings[undone].Actions, date, modes, correction);
    }

    /// <summary>The lines of a reopen card undoing <paramref name="actions"/> — what the undone command did, as
    /// recorded when it was confirmed (<see cref="CoordinatorActionSchedule.ActionsOf"/>, #375).</summary>
    public static IReadOnlyList<CoordinatorCardRow> ReopenRows(
        IReadOnlyList<ShopAction> actions, DateOnly date, IReadOnlyList<ShopActionMode> modes, bool correction)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(modes);
        CheckModes(actions, modes);
        var day = ShopAnnouncements.FormatDate(date);
        return
        [
            .. actions.Select((action, index) => action.Kind switch
            {
                ShopActionKind.LineBroadcast => correction
                    ? new CoordinatorCardRow("LINE 群發更正公告", modes[index])
                    : new CoordinatorCardRow("LINE 群發：已發出的公告無法收回", null),
                ShopActionKind.GoogleSpecialHours => new CoordinatorCardRow($"Google 特殊營業時間：移除 {day} 的設定", modes[index]),
                ShopActionKind.FacebookPost => new CoordinatorCardRow("FB 粉專：刪除公告貼文", modes[index]),
                ShopActionKind.Ordering => new CoordinatorCardRow($"{action.Name} 重新開啟接單", modes[index]),
                _ => throw new ArgumentOutOfRangeException(nameof(actions), action.Kind, "Not an action kind."),
            }),
        ];
    }

    /// <summary>The Flex message of <paramref name="content"/>.</summary>
    public static JsonObject Build(CoordinatorCardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if ((content.Scenario == ShopScenario.Reopen) != (content.Reopen is not null))
        {
            throw new ArgumentException("Exactly a reopen card has reopen details.", nameof(content));
        }

        var body = new JsonArray();
        if (content.Reopen is { } reopen)
        {
            body.Add(Text(
                $"取消 {ShopAnnouncements.FormatDate(reopen.UndoneDate)} {CoordinatorCommandReplies.Title(reopen.UndoneScenario, reopen.UndoneTime)}",
                size: "sm"));
        }

        var rows = new JsonArray();
        foreach (var row in content.Rows)
        {
            rows.Add(Text(row.Mode is { } mode ? $"・{row.Label}（{ModeName(mode)}）" : $"・{row.Label}", size: "sm"));
            if (!string.IsNullOrWhiteSpace(row.Detail))
            {
                rows.Add(Text($"　{row.Detail}", size: "xs", color: "#666666"));
            }

            if (!string.IsNullOrWhiteSpace(row.Note))
            {
                rows.Add(Text($"　{row.Note}", size: "xs", color: "#666666"));
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(Text("・沒有設定動作", size: "sm"));
        }

        body.Add(new JsonObject { ["type"] = "box", ["layout"] = "vertical", ["spacing"] = "xs", ["contents"] = rows });
        if (Reservations(content) is { Count: > 0 } reservations)
        {
            body.Add(new JsonObject { ["type"] = "box", ["layout"] = "vertical", ["spacing"] = "xs", ["contents"] = reservations });
        }

        var showAnnouncement = content.Announcement is { Length: > 0 }
            && (content.Reopen is null || content.Reopen.CorrectionBroadcast);
        if (showAnnouncement)
        {
            body.Add(new JsonObject { ["type"] = "separator" });
            body.Add(Text(content.Reopen is null ? "公告" : "更正公告", size: "sm", weight: "bold"));
            body.Add(Text(content.Announcement!, size: "sm"));
        }
        else if (content.Reopen is { CorrectionOffered: true })
        {
            body.Add(new JsonObject { ["type"] = "separator" });
            body.Add(Text("不加發更正公告", size: "sm", color: "#666666"));
        }

        if (content.Reopen is null)
        {
            body.Add(Text(CoordinatorCommandReplies.OrdersStillToHandle, size: "sm", weight: "bold", color: "#B3261E"));
        }

        if (showAnnouncement)
        {
            body.Add(Text(CoordinatorCommandReplies.EditHint, size: "xs", color: "#666666"));
        }

        var footer = new JsonArray();
        if (content.Reopen is { CorrectionOffered: true } offered)
        {
            footer.Add(Button(
                offered.CorrectionBroadcast ? CorrectionOffLabel : CorrectionOnLabel,
                CoordinatorMessageRouter.CorrectionPostback(content.OrganizationId, content.CommandId, content.Version),
                style: "secondary"));
        }

        footer.Add(new JsonObject
        {
            ["type"] = "box",
            ["layout"] = "horizontal",
            ["spacing"] = "sm",
            ["contents"] = new JsonArray
            {
                Button(CancelLabel, CoordinatorMessageRouter.CancelPostback(content.OrganizationId, content.CommandId, content.Version), style: "secondary"),
                Button(ConfirmLabel, CoordinatorMessageRouter.ConfirmPostback(content.OrganizationId, content.CommandId, content.Version), style: "primary"),
            },
        });

        return new JsonObject
        {
            ["type"] = "flex",
            ["altText"] = AltText(content),
            ["contents"] = new JsonObject
            {
                ["type"] = "bubble",
                ["header"] = new JsonObject
                {
                    ["type"] = "box",
                    ["layout"] = "vertical",
                    ["contents"] = new JsonArray { Text(Header(content), size: "md", weight: "bold") },
                },
                ["body"] = new JsonObject { ["type"] = "box", ["layout"] = "vertical", ["spacing"] = "md", ["contents"] = body },
                ["footer"] = new JsonObject { ["type"] = "box", ["layout"] = "vertical", ["spacing"] = "sm", ["contents"] = footer },
            },
        };
    }

    /// <summary>The reservation lines of <paramref name="content"/> (#392); empty when it has none to tell.</summary>
    private static JsonArray Reservations(CoordinatorCardContent content)
    {
        var lines = new JsonArray();
        switch (content.Reservations)
        {
            case { NotRestored: > 0 } kept when content.Reopen is not null:
                lines.Add(Text($"・{CoordinatorReservationClosures.NotRestored(kept.NotRestored)}", size: "sm"));
                break;

            case { Count: > 0 } taken when content.Reopen is null:
                lines.Add(Text($"・{CoordinatorReservationClosures.CardLine(taken.Accepted, taken.Pending)}", size: "sm", weight: "bold"));
                foreach (var listed in taken.Listed.Take(CoordinatorReservationClosures.CardListMax))
                {
                    lines.Add(Text($"　{listed}", size: "xs", color: "#666666"));
                }

                var more = taken.Count - Math.Min(taken.Listed.Count, CoordinatorReservationClosures.CardListMax);
                if (more > 0)
                {
                    lines.Add(Text($"　{CoordinatorReservationClosures.More(more)}", size: "xs", color: "#666666"));
                }

                break;
        }

        return lines;
    }

    private static void CheckModes(IReadOnlyList<ShopAction> actions, IReadOnlyList<ShopActionMode> modes)
    {
        if (modes.Count != actions.Count)
        {
            throw new ArgumentException("One mode per action.", nameof(modes));
        }
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
