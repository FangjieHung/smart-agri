using System.Globalization;
using System.Text;
using SmartAgri.Domain.Notifications;

namespace SmartAgri.Application.Notifications;

/// <summary>
/// The payload of a <see cref="Kind"/> background job (#355): one 轉真人 notification Email, for the
/// handoff <paramref name="HandoffId"/>. Only the id: the recipient and their choice are read when the
/// job runs, so opting out also stops Emails still in the queue.
/// </summary>
public sealed record SendHandoffEmailJob(Guid HandoffId)
{
    public const string Kind = "notifications.handoff-email";
}

/// <summary>
/// The 轉真人 notification Email (#355, owner decision 2026-10-13): only what staff need to act —
/// the organization, the assistant, the channel, the time and the admin link. Never anything the
/// visitor wrote or the assistant answered (the handoff does not have it either).
/// </summary>
public static class HandoffEmail
{
    /// <summary>The admin page that lists handoffs.</summary>
    public const string AdminPath = "/app/handoffs";

    public static string ChannelLabel(VisitorHandoffChannel channel) => channel switch
    {
        VisitorHandoffChannel.Website => "官網",
        VisitorHandoffChannel.Line => "LINE",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    /// <param name="adminOrigin">The admin's origin (e.g. <c>https://assistant.example.org</c>), or
    /// <see langword="null"/> when the deployment has none configured: then the Email has no link.</param>
    public static EmailMessage Compose(
        string to,
        string organizationName,
        string assistantName,
        VisitorHandoffChannel channel,
        DateTimeOffset at,
        TimeZoneInfo timeZone,
        string? adminOrigin)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(timeZone);
        var assistant = OneLine(assistantName);
        var organization = OneLine(organizationName);
        var label = ChannelLabel(channel);
        // A Latin word between Chinese gets spaces around it (「在 LINE 回覆」), as everywhere in the product.
        var inSentence = channel == VisitorHandoffChannel.Line ? $" {label} " : label;
        var local = TimeZoneInfo.ConvertTime(at, timeZone);
        var time = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"{organization}的助理「{assistant}」在{inSentence}回覆了「找不到資料時的回覆」，訪客可能在等專人回覆。\n\n");
        body.Append(CultureInfo.InvariantCulture, $"助理：{assistant}\n");
        body.Append(CultureInfo.InvariantCulture, $"管道：{label}\n");
        body.Append(CultureInfo.InvariantCulture, $"時間：{time}（{timeZone.Id}）\n");
        if (!string.IsNullOrWhiteSpace(adminOrigin))
        {
            body.Append(CultureInfo.InvariantCulture, $"後台：{adminOrigin.TrimEnd('/')}{AdminPath}\n");
        }

        body.Append('\n');
        body.Append(channel == VisitorHandoffChannel.Line
            ? "請到 LINE Official Account Manager 的「聊天」查看並回覆這位使用者。\n"
            : "官網的訪客不會留下任何資料，請留意「找不到資料時的回覆」裡寫的聯絡方式（電話、LINE 等）。\n");
        body.Append("處理完請在後台的「轉真人」標記已處理。\n\n");
        body.Append("這封信不含訪客的對話內容。不想再收到這類 Email：到後台「團隊與設定 → 我的通知」關閉。\n");

        return new EmailMessage(to, $"【轉真人】{assistant}：{inSentence.TrimStart()}的訪客需要專人協助", body.ToString());
    }

    /// <summary>Names are typed by people: no line breaks or control characters reach a header.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(character => !char.IsControl(character))])));
}
