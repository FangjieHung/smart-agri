using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Line;

/// <summary>A connection test's results, in <see cref="LineConnectionCheck.All"/>'s order, and the
/// bot's user id when the token check passed (else <see langword="null"/>).</summary>
public sealed record LineConnectionTestResult(IReadOnlyList<LineConnectionCheck> Checks, string? BotUserId);

/// <summary>
/// The LINE channel's 「測試連線」 (M5b plan §3 B, decisions B and C): three checks, in order, each
/// passed or failed with a sentence for the owner (Traditional Chinese, never a credential):
/// <list type="number">
/// <item><c>access-token</c> — <c>GET /v2/bot/info</c> accepts the token, and the account it
/// belongs to is the official account id the owner entered (its basic id, or its premium id if it has
/// one; case-insensitive, <c>@</c> optional);</item>
/// <item><c>webhook-endpoint</c> — <c>PUT /v2/bot/channel/webhook/endpoint</c> sets the webhook URL to
/// this server's;</item>
/// <item><c>webhook-test</c> — <c>POST /v2/bot/channel/webhook/test</c>: LINE delivers a signed test
/// event to that URL, which answers <c>2xx</c> only when the signature verifies with the stored
/// channel secret.</item>
/// </list>
/// A failed check skips the ones after it (a wrong token stops the other two). LINE failing is a
/// failed check, never an exception.
/// </summary>
public static class LineConnectionTester
{
    public const string SkippedMessage = "前一項檢查未通過，這一項沒有執行。";

    public const string TokenUnreadableMessage =
        "保存的 Channel access token 無法解密（伺服器的金鑰可能已更換），請重新填寫 Channel access token 與 Channel secret 後儲存。";

    public const string TokenRejectedMessage =
        "LINE 不接受這個 Channel access token（可能填錯、已重新發行或已撤銷）；請到 LINE Developers Console 的 Messaging API 頁面發行長效型 Token，重新填寫後儲存。";

    public const string WebhookTestRateLimitedMessage =
        "LINE 限制每個頻道每小時最多測試 Webhook 60 次，目前已達上限；請稍後再測試連線。";

    /// <summary>Runs the three checks against LINE with <paramref name="accessToken"/>.</summary>
    /// <param name="officialAccountId">The official account id the owner entered (<c>@</c> first).</param>
    /// <param name="webhookUrl">This server's webhook URL for the assistant.</param>
    public static async Task<LineConnectionTestResult> RunAsync(
        ILineMessagingClient client,
        string accessToken,
        string officialAccountId,
        string webhookUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(officialAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);

        var info = await client.GetBotInfoAsync(accessToken, cancellationToken);
        var tokenCheck = TokenCheck(info, officialAccountId);
        if (tokenCheck.State != LineConnectionCheckState.Passed)
        {
            return new LineConnectionTestResult(
                [tokenCheck, Skipped(LineConnectionCheckKind.WebhookEndpoint), Skipped(LineConnectionCheckKind.WebhookTest)],
                null);
        }

        var botUserId = info.Value!.UserId;
        var set = await client.SetWebhookEndpointAsync(accessToken, webhookUrl, cancellationToken);
        var endpointCheck = set.Succeeded
            ? Passed(LineConnectionCheckKind.WebhookEndpoint, $"已將 LINE 的 Webhook 網址設為 {webhookUrl}。")
            : Failed(LineConnectionCheckKind.WebhookEndpoint, EndpointFailure(set, webhookUrl));
        if (endpointCheck.State != LineConnectionCheckState.Passed)
        {
            return new LineConnectionTestResult([tokenCheck, endpointCheck, Skipped(LineConnectionCheckKind.WebhookTest)], botUserId);
        }

        var test = await client.TestWebhookEndpointAsync(accessToken, webhookUrl, cancellationToken);
        return new LineConnectionTestResult([tokenCheck, endpointCheck, WebhookTestCheck(test)], botUserId);
    }

    /// <summary>What a test records when the stored token cannot be decrypted: the token check
    /// failed, the other two skipped — without calling LINE.</summary>
    public static LineConnectionTestResult TokenUnreadable() =>
        new(
            [
                Failed(LineConnectionCheckKind.AccessToken, TokenUnreadableMessage),
                Skipped(LineConnectionCheckKind.WebhookEndpoint),
                Skipped(LineConnectionCheckKind.WebhookTest),
            ],
            null);

    /// <summary>
    /// Whether <paramref name="entered"/> names the account <paramref name="info"/> describes: its
    /// basic id or its premium id, ignoring case and a missing <c>@</c>.
    /// </summary>
    public static bool IsSameAccount(LineBotInfo info, string entered)
    {
        ArgumentNullException.ThrowIfNull(info);
        var wanted = NormalizeAccountId(entered);
        return wanted.Length > 1
            && (string.Equals(NormalizeAccountId(info.BasicId), wanted, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(info.PremiumId)
                    && string.Equals(NormalizeAccountId(info.PremiumId), wanted, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Trimmed, with a leading <c>@</c>.</summary>
    public static string NormalizeAccountId(string? accountId)
    {
        var trimmed = (accountId ?? string.Empty).Trim();
        return trimmed.StartsWith('@') ? trimmed : "@" + trimmed;
    }

    private static LineConnectionCheck TokenCheck(LineApiResult<LineBotInfo> info, string officialAccountId)
    {
        if (!info.Succeeded)
        {
            return Failed(
                LineConnectionCheckKind.AccessToken,
                info.Outcome == LineApiOutcome.Unauthorized ? TokenRejectedMessage : CommonFailure(info));
        }

        var bot = info.Value!;
        if (!IsSameAccount(bot, officialAccountId))
        {
            return Failed(
                LineConnectionCheckKind.AccessToken,
                $"這個 Channel access token 屬於官方帳號 {DescribeAccount(bot)}，與填寫的官方帳號 ID {NormalizeAccountId(officialAccountId)} 不符；" +
                "請確認填寫的官方帳號 ID，或改用這個官方帳號的 Messaging API channel 的 Token。");
        }

        return Passed(LineConnectionCheckKind.AccessToken, $"Token 有效，屬於官方帳號 {DescribeAccount(bot)}。");
    }

    private static string DescribeAccount(LineBotInfo bot) =>
        string.IsNullOrWhiteSpace(bot.DisplayName)
            ? NormalizeAccountId(bot.BasicId)
            : $"{NormalizeAccountId(bot.BasicId)}（{bot.DisplayName}）";

    private static string EndpointFailure(LineApiResult result, string webhookUrl) =>
        result is { Outcome: LineApiOutcome.HttpError, StatusCode: 400 }
            ? $"LINE 不接受這個 Webhook 網址：{webhookUrl}{LineSays(result)}。Webhook 網址必須是可從網際網路連線的 HTTPS 網址，" +
              "請系統管理者確認 PublicChannels:PublicBaseUrl。"
            : CommonFailure(result);

    private static LineConnectionCheck WebhookTestCheck(LineApiResult<LineWebhookTestResult> result)
    {
        if (!result.Succeeded)
        {
            return Failed(
                LineConnectionCheckKind.WebhookTest,
                result.Outcome == LineApiOutcome.RateLimited ? WebhookTestRateLimitedMessage : CommonFailure(result));
        }

        var test = result.Value!;
        if (test.Success)
        {
            return Passed(
                LineConnectionCheckKind.WebhookTest,
                "LINE 送出的測試事件已送達，伺服器以 Channel secret 驗證簽章通過。");
        }

        var detail = string.IsNullOrWhiteSpace(test.Detail) ? string.Empty : $"（LINE：{Shorten(test.Detail)}）";
        var message = test.Reason switch
        {
            "COULD_NOT_CONNECT" =>
                $"LINE 無法連線到 Webhook 網址{detail}；請系統管理者確認網址可從網際網路連線，並使用公開憑證的 HTTPS（TLS 1.2 以上）。",
            "REQUEST_TIMEOUT" =>
                $"LINE 送出測試事件後，伺服器沒有在時限內回應{detail}；請稍後再試，持續發生時請洽系統管理者。",
            "ERROR_STATUS_CODE" when test.StatusCode == 401 =>
                "伺服器拒絕了 LINE 的測試事件（HTTP 401，簽章不符）：通常是 Channel secret 填錯了；請到 LINE Developers Console 確認 Channel secret，重新填寫後儲存。",
            "ERROR_STATUS_CODE" =>
                $"伺服器以 HTTP {test.StatusCode} 回應 LINE 的測試事件{detail}；請洽系統管理者確認 Webhook 端點可以使用。",
            _ => $"LINE 無法完成 Webhook 測試（{Shorten(test.Reason)}）{detail}；請稍後再試。",
        };
        return Failed(LineConnectionCheckKind.WebhookTest, message);
    }

    /// <summary>The sentence for a failure every call can have.</summary>
    private static string CommonFailure(LineApiResult result) =>
        result.Outcome switch
        {
            LineApiOutcome.Unauthorized =>
                $"LINE 拒絕了這個請求（HTTP {result.StatusCode}）{LineSays(result)}；請確認 Channel access token 仍然有效，重新填寫後儲存。",
            LineApiOutcome.RateLimited =>
                "LINE 暫時限制了請求次數（HTTP 429）；請稍後再測試連線。",
            LineApiOutcome.TimedOut =>
                "連線到 LINE 逾時；請稍後再試，持續發生時請洽系統管理者確認伺服器的對外網路。",
            LineApiOutcome.NetworkError =>
                "無法連線到 LINE；請稍後再試，持續發生時請洽系統管理者確認伺服器可以連上 api.line.me。",
            _ when result.StatusCode is >= 500 =>
                $"LINE 暫時無法處理請求（HTTP {result.StatusCode}）；請稍後再測試連線。",
            _ => $"LINE 回應錯誤（HTTP {result.StatusCode}）{LineSays(result)}；請稍後再試，持續發生時請洽系統管理者。",
        };

    private static string LineSays(LineApiResult result) =>
        string.IsNullOrWhiteSpace(result.ErrorMessage) ? string.Empty : $"（LINE：{Shorten(result.ErrorMessage)}）";

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";

    private static LineConnectionCheck Passed(LineConnectionCheckKind kind, string message) =>
        new(kind, LineConnectionCheckState.Passed, message);

    private static LineConnectionCheck Failed(LineConnectionCheckKind kind, string message) =>
        new(kind, LineConnectionCheckState.Failed, message);

    private static LineConnectionCheck Skipped(LineConnectionCheckKind kind) =>
        new(kind, LineConnectionCheckState.Skipped, SkippedMessage);
}
