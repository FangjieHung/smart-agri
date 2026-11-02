using System.Globalization;
using System.Text.Json.Nodes;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// What the 統籌助理 says (store-coordinator design §5 Slice 1, #372): fixed texts, and the message
/// objects for <see cref="ILineMessagingClient.ReplyAsync"/>. Plain text; the store choice adds LINE
/// quick-reply buttons.
/// </summary>
public static class CoordinatorReplies
{
    public const string Welcome = "歡迎使用統籌助理。請輸入老闆或平台營運者給你的綁定碼。";

    public const string AskForCode = "請輸入綁定碼。";

    public const string InvalidCode = "綁定碼無效或已過期，請向老闆或平台營運者索取新的綁定碼。";

    public const string TooManyAttempts = "嘗試太多次，請 15 分鐘後再試。";

    public const string OwnersOnly = "只有老闆可以產生店長綁定碼。";

    /// <summary>The capability menu (Q24): the reply to a bound member's message that is not a command.</summary>
    public const string Menu = "我目前可以幫你：臨時店休／恢復營業／提早打烊／延後開店";

    public const string ChooseStore = "要產生哪一家店的店長綁定碼？";

    /// <summary>LINE's limit on a quick-reply button's label (characters).</summary>
    public const int QuickReplyLabelMaxLength = 20;

    /// <summary>LINE's limit on quick-reply buttons per message.</summary>
    public const int QuickReplyMaxItems = 13;

    /// <summary>The time zone codes' expiry is shown in until shops have their own (Slice 2).</summary>
    public const string DefaultTimeZoneId = "Asia/Taipei";

    /// <summary>「已綁定 {店名}（{老闆/店長}）。」</summary>
    public static string Bound(string storeName, CoordinatorRole role) =>
        $"已綁定 {storeName}（{CoordinatorRoleNames.Display(role)}）。";

    /// <summary>「你已經綁定 {店名}（{老闆/店長}）。」: the code was valid and is left unused.</summary>
    public static string AlreadyBound(string storeName, CoordinatorRole role) =>
        $"你已經綁定 {storeName}（{CoordinatorRoleNames.Display(role)}）。";

    /// <summary>A new 店長 code for <paramref name="storeName"/>: the code as people type it
    /// (<see cref="BindingCodes.Format"/>), the store, the expiry in <paramref name="zone"/> and that it
    /// works once.</summary>
    public static string ManagerCode(string code, string storeName, DateTimeOffset expiresAt, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(expiresAt, zone);
        var zoneName = zone.Id == DefaultTimeZoneId ? "台北時間" : zone.Id;
        return $"店長綁定碼：{BindingCodes.Format(code)}\n"
            + $"店名：{storeName}\n"
            + $"有效期限：{local.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)}（{zoneName}）前\n"
            + "這組碼只能使用一次，請交給店長加入統籌助理後輸入。";
    }

    /// <summary>
    /// <see cref="ChooseStore"/> with one quick-reply button per store (at most
    /// <see cref="QuickReplyMaxItems"/>, labels shortened to <see cref="QuickReplyLabelMaxLength"/>
    /// characters), each a postback carrying <see cref="CoordinatorMessageRouter.ManagerCodePostback"/>.
    /// </summary>
    public static JsonObject StoreChoice(IReadOnlyList<(Guid OrganizationId, string StoreName)> stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        var items = new JsonArray();
        foreach (var (organizationId, storeName) in stores.Take(QuickReplyMaxItems))
        {
            items.Add(new JsonObject
            {
                ["type"] = "action",
                ["action"] = new JsonObject
                {
                    ["type"] = "postback",
                    ["label"] = Label(storeName),
                    ["data"] = CoordinatorMessageRouter.ManagerCodePostback(organizationId),
                    ["displayText"] = Shorten($"產生{storeName}的店長綁定碼", 300),
                },
            });
        }

        var message = LineMessages.Text(ChooseStore);
        message["quickReply"] = new JsonObject { ["items"] = items };
        return message;
    }

    /// <summary><paramref name="text"/> fitting a quick-reply label.</summary>
    public static string Label(string text) => Shorten(text, QuickReplyLabelMaxLength);

    /// <summary>At most <paramref name="max"/> UTF-16 code units, ending in 「…」 when shortened, never
    /// splitting a surrogate pair.</summary>
    private static string Shorten(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max)
        {
            return text;
        }

        var keep = max - 1;
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), "…");
    }
}
