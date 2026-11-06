using System.Globalization;
using System.Text.Json.Nodes;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// What the 統籌助理 says about a shop's online reservations (store-reservations design decisions R2, R3, R5, §3.1 points
/// 6–9, Slices R2 and R3, #390, #391): the new-reservation card pushed to each member, the answer to 〔接受〕／〔婉拒〕 (which
/// says whether the customer is told by Email or must be called), and the 「訂位」 list — LINE messages built purely from
/// the reservations and whether the platform can send Email.
/// </summary>
/// <remarks>
/// <para>
/// The cards show what the shop needs to seat and call the customer — date and time (as the reservation page shows
/// them, 「（隔天）」 after midnight), party size, name, phone, Email or 「沒有留 Email」 (on the new-reservation card
/// 「沒有留 Email，請來電確認」, and 「請來電確認」 on every one when the platform sends no Email), note — and nothing else: no LINE
/// id, no reservation id but in the buttons' postback data. 〔撥打電話〕 is a URI action <c>tel:</c> (LINE allows the
/// schemes <c>http</c>, <c>https</c>, <c>line</c> and <c>tel</c>); 〔接受〕 and 〔婉拒〕 are postbacks of
/// <see cref="CoordinatorMessageRouter.AcceptReservationPostback"/> and
/// <see cref="CoordinatorMessageRouter.DeclineReservationPostback"/>.
/// </para>
/// <para>
/// LINE limits: <c>altText</c> at most 400 characters, button labels at most 20, postback data at most 300, a carousel
/// at most <see cref="CarouselMaxBubbles"/> bubbles, every text non-empty; a reply holds at most five messages (here at
/// most two).
/// </para>
/// </remarks>
public static class CoordinatorReservationCards
{
    public const string CallLabel = "撥打電話";

    public const string AcceptLabel = "接受";

    public const string DeclineLabel = "婉拒";

    /// <summary>What the 「訂位」 list says when the customer left no Email address.</summary>
    public const string NoEmail = "沒有留 Email";

    /// <summary>What the new-reservation card says when the customer left no Email address (#391): nobody but the shop
    /// will tell them.</summary>
    public const string NoEmailCallToConfirm = "沒有留 Email，請來電確認";

    /// <summary>What the new-reservation card adds when the platform sends no Email at all (no SMTP, #391).</summary>
    public const string CallToConfirm = "請來電確認";

    /// <summary>How many business days 「訂位」 lists, today included.</summary>
    public const int ListDays = 7;

    /// <summary>LINE's limit on the bubbles of one carousel.</summary>
    public const int CarouselMaxBubbles = 12;

    public const string NoReservations = "接下來 7 天沒有訂位。";

    public const string ChooseShop = "要查看哪一家店的訂位？";

    /// <summary>A cancelled reservation whose reason is not known (#390); a closure's says why (<see cref="CancelledBecause"/>).</summary>
    public const string Cancelled = "這筆訂位已取消。";

    /// <summary>「這筆訂位因臨時店休已取消。」: a press on a reservation a closure cancelled (#392), <paramref name="reason"/>
    /// from <see cref="CoordinatorReservationClosures.Reason"/>.</summary>
    public static string CancelledBecause(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return $"這筆訂位因{reason}已取消。";
    }

    public const string Unknown = "找不到這筆訂位，可能已經刪除。";

    public const string NotAMember = "你沒有綁定這家店，不能處理這筆訂位。";

    /// <summary>What a member who left the shop is called in 「已由{誰}…」.</summary>
    public const string FormerMember = "其他成員";

    /// <summary>What the member who pressed is called in 「已由{誰}…」 about their own press.</summary>
    public const string You = "你";

    public const string PendingLabel = "待處理";

    public const string AcceptedLabel = "已接受";

    private const int AltTextMaxLength = CoordinatorPreviewCard.AltTextMaxLength;

    private const string Grey = "#666666";

    /// <summary>
    /// The card pushed to each member when <paramref name="reservation"/> arrives (alt text 「新訂位：{店名} {日期時間}
    /// {人數} 位」): header 「新訂位｜{店名}」, the details, 〔撥打電話〕〔接受〕〔婉拒〕. When the platform sends Email
    /// (<paramref name="emailConfigured"/>), a customer without an address is 「沒有留 Email，請來電確認」; when it does not,
    /// no Email row unless the customer left one, and 「請來電確認」 below the details.
    /// </summary>
    public static JsonObject NewReservation(string storeName, TimeZoneInfo zone, CoordinatorReservation reservation, bool emailConfigured)
    {
        ArgumentNullException.ThrowIfNull(storeName);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(reservation);
        var when = reservation.When(zone);
        var body = new JsonArray
        {
            Text(when, "lg", weight: "bold"),
            Text(Party(reservation.PartySize), "md"),
        };
        foreach (var row in Details(reservation, emailConfigured ? NoEmailCallToConfirm : null))
        {
            body.Add(row);
        }

        if (!emailConfigured)
        {
            body.Add(Text(CallToConfirm, "sm", weight: "bold"));
        }

        var bubble = new JsonObject
        {
            ["type"] = "bubble",
            ["header"] = Box("vertical", new JsonArray { Text($"新訂位｜{storeName}", "md", weight: "bold") }),
            ["body"] = Box("vertical", body, spacing: "sm"),
            ["footer"] = Footer(reservation, withDecision: true),
        };
        return Flex($"新訂位：{storeName} {when} {Party(reservation.PartySize)}", bubble);
    }

    /// <summary>
    /// 「已接受：{姓名} {日期時間} {人數} 位。」 or 「已婉拒：…」 and what happens to the customer (#391): 「會寄確認信給顧客。」／
    /// 「會寄婉拒信給顧客。」 (the customer left an address and <paramref name="emailConfigured"/>), 「這位顧客沒有留 Email，
    /// 請來電 {電話}。」, or 「平台沒有設定寄信，請來電 {電話}。」 (no SMTP) — what the member who pressed is told.
    /// </summary>
    public static string DecidedText(CoordinatorReservation reservation, TimeZoneInfo zone, bool emailConfigured)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(zone);
        var (verb, letter) = reservation.Status switch
        {
            CoordinatorReservationStatus.Accepted => ("已接受", "確認信"),
            CoordinatorReservationStatus.Declined => ("已婉拒", "婉拒信"),
            _ => throw new ArgumentException("Only an accepted or declined reservation is decided.", nameof(reservation)),
        };
        var phone = ReservationContact.FormatPhone(reservation.CustomerPhone);
        var customer = !emailConfigured ? $"平台沒有設定寄信，請來電 {phone}。"
            : reservation.CustomerEmail is null ? $"這位顧客沒有留 Email，請來電 {phone}。"
            : $"會寄{letter}給顧客。";
        return $"{verb}：{reservation.CustomerName} {reservation.When(zone)} {Party(reservation.PartySize)}。{customer}";
    }

    /// <summary>The reply to the press that accepted or declined <paramref name="reservation"/>: <see cref="DecidedText"/>
    /// (also the alt text) with 〔撥打電話〕.</summary>
    public static JsonObject Decided(CoordinatorReservation reservation, TimeZoneInfo zone, bool emailConfigured)
    {
        var text = DecidedText(reservation, zone, emailConfigured);
        var bubble = new JsonObject
        {
            ["type"] = "bubble",
            ["body"] = Box("vertical", new JsonArray { Text(text, "md") }),
            ["footer"] = Footer(reservation, withDecision: false),
        };
        return Flex(text, bubble);
    }

    /// <summary>「這筆訂位已由{誰}在 {時間} {接受／婉拒}。」: <paramref name="who"/> is the member's LINE name, 「老闆」/「店長」,
    /// <see cref="You"/> or <see cref="FormerMember"/>; <paramref name="handledAt"/> in <paramref name="zone"/>.</summary>
    public static string AlreadyHandled(CoordinatorReservationStatus status, string who, DateTimeOffset handledAt, TimeZoneInfo zone)
    {
        ArgumentException.ThrowIfNullOrEmpty(who);
        ArgumentNullException.ThrowIfNull(zone);
        var verb = status switch
        {
            CoordinatorReservationStatus.Accepted => "接受",
            CoordinatorReservationStatus.Declined => "婉拒",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Only an accepted or declined reservation was handled."),
        };
        return $"這筆訂位已由{who}在 {ShopAnnouncements.FormatDateTime(handledAt, zone)} {verb}。";
    }

    /// <summary>「這筆訂位已過期：訂位時間前 {N} 分鐘還沒有人處理。」 (「到訂位時間還沒有人處理」 for 0).</summary>
    public static string Expired(int pendingExpiryMinutes) =>
        pendingExpiryMinutes > 0
            ? string.Create(CultureInfo.InvariantCulture, $"這筆訂位已過期：訂位時間前 {pendingExpiryMinutes} 分鐘還沒有人處理。")
            : "這筆訂位已過期：到訂位時間還沒有人處理。";

    /// <summary><see cref="ChooseShop"/> with one quick-reply button per shop (at most
    /// <see cref="CoordinatorReplies.QuickReplyMaxItems"/>), each a postback of
    /// <see cref="CoordinatorMessageRouter.ReservationsPostback"/>.</summary>
    public static JsonObject ShopChoice(IReadOnlyList<(Guid OrganizationId, string StoreName)> shops)
    {
        ArgumentNullException.ThrowIfNull(shops);
        var items = new JsonArray();
        foreach (var (organizationId, storeName) in shops.Take(CoordinatorReplies.QuickReplyMaxItems))
        {
            items.Add(new JsonObject
            {
                ["type"] = "action",
                ["action"] = new JsonObject
                {
                    ["type"] = "postback",
                    ["label"] = CoordinatorReplies.Label(storeName),
                    ["data"] = CoordinatorMessageRouter.ReservationsPostback(organizationId),
                    ["displayText"] = Cut($"查看{storeName}的訂位", 300),
                },
            });
        }

        var message = LineMessages.Text(ChooseShop);
        message["quickReply"] = new JsonObject { ["items"] = items };
        return message;
    }

    /// <summary>
    /// The answer to 「訂位」 for one shop: <see cref="NoReservations"/>, or a carousel of the first
    /// <see cref="CarouselMaxBubbles"/> of <paramref name="reservations"/> (pending and accepted, earliest first; pending
    /// ones with 〔接受〕〔婉拒〕; alt text 「{店名} 接下來 7 天的訂位：待處理 a 筆、已接受 b 筆。」) and, when there are more,
    /// a text saying how many were left out.
    /// </summary>
    public static IReadOnlyList<JsonObject> List(string storeName, TimeZoneInfo zone, IReadOnlyList<CoordinatorReservation> reservations)
    {
        ArgumentNullException.ThrowIfNull(storeName);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(reservations);
        if (reservations.Count == 0)
        {
            return [LineMessages.Text($"{storeName}：{NoReservations}")];
        }

        var bubbles = new JsonArray();
        foreach (var reservation in reservations.Take(CarouselMaxBubbles))
        {
            bubbles.Add(ListBubble(storeName, zone, reservation));
        }

        var pending = reservations.Count(reservation => reservation.Status == CoordinatorReservationStatus.Pending);
        var accepted = reservations.Count(reservation => reservation.Status == CoordinatorReservationStatus.Accepted);
        var altText = string.Create(
            CultureInfo.InvariantCulture, $"{storeName} 接下來 {ListDays} 天的訂位：{PendingLabel} {pending} 筆、{AcceptedLabel} {accepted} 筆。");
        var messages = new List<JsonObject> { Flex(altText, new JsonObject { ["type"] = "carousel", ["contents"] = bubbles }) };
        if (reservations.Count > CarouselMaxBubbles)
        {
            messages.Add(LineMessages.Text(More(reservations.Count - CarouselMaxBubbles)));
        }

        return messages;
    }

    /// <summary>「另外還有 {N} 筆訂位沒有列出（只列最早的 12 筆）。」</summary>
    public static string More(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"另外還有 {count} 筆訂位沒有列出（只列最早的 {CarouselMaxBubbles} 筆）。");

    private static JsonObject ListBubble(string storeName, TimeZoneInfo zone, CoordinatorReservation reservation)
    {
        var pending = reservation.Status == CoordinatorReservationStatus.Pending;
        var header = new JsonArray
        {
            Text(storeName, "xs", color: Grey),
            new JsonObject
            {
                ["type"] = "box",
                ["layout"] = "horizontal",
                ["contents"] = new JsonArray
                {
                    Text(reservation.When(zone), "md", weight: "bold", flex: 4),
                    Text(pending ? PendingLabel : AcceptedLabel, "sm", weight: "bold", color: pending ? "#B3261E" : "#1E7D34", flex: 2, align: "end"),
                },
            },
        };
        var body = new JsonArray { Text(Party(reservation.PartySize), "md") };
        foreach (var row in Details(reservation, NoEmail))
        {
            body.Add(row);
        }

        return new JsonObject
        {
            ["type"] = "bubble",
            ["header"] = Box("vertical", header, spacing: "xs"),
            ["body"] = Box("vertical", body, spacing: "sm"),
            ["footer"] = Footer(reservation, withDecision: pending),
        };
    }

    /// <summary>姓名, 電話, Email (or <paramref name="noEmail"/>; no row when both are <see langword="null"/>) and, when there
    /// is one, 備註.</summary>
    private static IEnumerable<JsonObject> Details(CoordinatorReservation reservation, string? noEmail)
    {
        yield return Row("姓名", reservation.CustomerName);
        yield return Row("電話", ReservationContact.FormatPhone(reservation.CustomerPhone));
        if ((reservation.CustomerEmail ?? noEmail) is { } email)
        {
            yield return Row("Email", email);
        }

        if (reservation.Note is { Length: > 0 } note)
        {
            yield return Row("備註", note);
        }
    }

    private static JsonObject Footer(CoordinatorReservation reservation, bool withDecision)
    {
        var contents = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "button",
                ["style"] = "link",
                ["height"] = "sm",
                ["action"] = new JsonObject { ["type"] = "uri", ["label"] = CallLabel, ["uri"] = "tel:" + reservation.CustomerPhone },
            },
        };
        if (withDecision)
        {
            contents.Add(new JsonObject
            {
                ["type"] = "box",
                ["layout"] = "horizontal",
                ["spacing"] = "sm",
                ["contents"] = new JsonArray
                {
                    Button(AcceptLabel, CoordinatorMessageRouter.AcceptReservationPostback(reservation.OrganizationId, reservation.Id), "primary"),
                    Button(DeclineLabel, CoordinatorMessageRouter.DeclineReservationPostback(reservation.OrganizationId, reservation.Id), "secondary"),
                },
            });
        }

        return Box("vertical", contents, spacing: "sm");
    }

    private static string Party(int partySize) => string.Create(CultureInfo.InvariantCulture, $"{partySize} 位");

    private static JsonObject Row(string label, string value) => new()
    {
        ["type"] = "box",
        ["layout"] = "horizontal",
        ["spacing"] = "md",
        ["contents"] = new JsonArray
        {
            Text(label, "sm", color: Grey, flex: 2),
            Text(value, "sm", flex: 7),
        },
    };

    private static JsonObject Flex(string altText, JsonObject contents) => new()
    {
        ["type"] = "flex",
        ["altText"] = Cut(altText, AltTextMaxLength),
        ["contents"] = contents,
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

    private static JsonObject Text(string text, string size, string? weight = null, string? color = null, int? flex = null, string? align = null)
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

        if (flex is { } grow)
        {
            node["flex"] = grow;
        }

        if (align is not null)
        {
            node["align"] = align;
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

    /// <summary>At most <paramref name="max"/> UTF-16 code units, never splitting a surrogate pair.</summary>
    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var keep = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..keep];
    }
}
