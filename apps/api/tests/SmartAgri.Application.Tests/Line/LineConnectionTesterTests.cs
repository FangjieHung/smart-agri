using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Tests.Line;

/// <summary>
/// 「測試連線」's three checks (M5b plan §3 B, #230): order, skipping after a failure, the basic id
/// match, and a sentence for every way LINE can fail — never an exception.
/// </summary>
public class LineConnectionTesterTests
{
    private const string Token = "token-0000000000000000000000000000000000000000";
    private const string WebhookUrl = "https://assistant.example.org/api/v1/line/webhook/01a10194-0000-7000-8000-000000000001";
    private const string BotUserId = "U0123456789abcdef0123456789abcdef";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task All_three_pass_in_order_and_the_bot_user_id_is_kept()
    {
        var client = new StubClient();

        var result = await RunAsync(client, "@anxin-demo");

        States(result).ShouldBe(["passed", "passed", "passed"]);
        result.Checks.Select(check => check.Check).ShouldBe(LineConnectionCheck.All);
        result.BotUserId.ShouldBe(BotUserId);
        client.Calls.ShouldBe([$"info {Token}", $"set {Token} {WebhookUrl}", $"test {Token} {WebhookUrl}"]);
        result.Checks[0].Message.ShouldBe("Token 有效，屬於官方帳號 @123abcde（安心農場）。");
        result.Checks[1].Message.ShouldContain(WebhookUrl);
        LineConnectionCheck.AllPassed([.. result.Checks]).ShouldBeTrue();
    }

    [Theory]
    [InlineData(LineApiOutcome.Unauthorized, 401, "LINE 不接受這個 Channel access token")]
    [InlineData(LineApiOutcome.RateLimited, 429, "HTTP 429")]
    [InlineData(LineApiOutcome.HttpError, 500, "LINE 暫時無法處理請求（HTTP 500）")]
    [InlineData(LineApiOutcome.TimedOut, null, "連線到 LINE 逾時")]
    [InlineData(LineApiOutcome.NetworkError, null, "無法連線到 LINE")]
    public async Task A_failed_token_check_skips_the_other_two_without_calling_line(LineApiOutcome outcome, int? status, string expected)
    {
        var client = new StubClient { Info = new LineApiResult<LineBotInfo>(outcome, status, "req", null, null) };

        var result = await RunAsync(client, "@anxin-demo");

        States(result).ShouldBe(["failed", "skipped", "skipped"]);
        result.Checks[0].Message.ShouldContain(expected);
        result.Checks[1].Message.ShouldBe(LineConnectionTester.SkippedMessage);
        result.BotUserId.ShouldBeNull();
        client.Calls.ShouldBe([$"info {Token}"]);
    }

    [Fact]
    public async Task A_token_of_another_official_account_fails_the_first_check_and_says_which_account_it_is()
    {
        var client = new StubClient();

        var result = await RunAsync(client, "@other-shop");

        States(result).ShouldBe(["failed", "skipped", "skipped"]);
        result.Checks[0].Message.ShouldBe(
            "這個 Channel access token 屬於官方帳號 @123abcde（安心農場），與填寫的官方帳號 ID @other-shop 不符；" +
            "請確認填寫的官方帳號 ID，或改用這個官方帳號的 Messaging API channel 的 Token。");
        result.BotUserId.ShouldBeNull();
        client.Calls.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("@123abcde")]
    [InlineData("@123ABCDE")]
    [InlineData("123abcde")]
    [InlineData("@ANXIN-demo")]
    [InlineData(" @anxin-demo ")]
    public void The_entered_id_matches_the_basic_or_premium_id_ignoring_case_and_the_at_sign(string entered) =>
        LineConnectionTester.IsSameAccount(new LineBotInfo(BotUserId, "@123abcde", "安心農場", "@anxin-demo"), entered).ShouldBeTrue();

    [Theory]
    [InlineData("@123abcd")]
    [InlineData("@")]
    [InlineData("")]
    [InlineData("@anxin")]
    public void Anything_else_does_not_match(string entered)
    {
        LineConnectionTester.IsSameAccount(new LineBotInfo(BotUserId, "@123abcde", "安心農場", "@anxin-demo"), entered).ShouldBeFalse();
        LineConnectionTester.IsSameAccount(new LineBotInfo(BotUserId, "@123abcde", "安心農場", null), "@anxin-demo").ShouldBeFalse();
    }

    [Theory]
    [InlineData(LineApiOutcome.HttpError, 400, "LINE 不接受這個 Webhook 網址")]
    [InlineData(LineApiOutcome.HttpError, 500, "HTTP 500")]
    [InlineData(LineApiOutcome.RateLimited, 429, "HTTP 429")]
    [InlineData(LineApiOutcome.TimedOut, null, "逾時")]
    public async Task A_failed_webhook_endpoint_skips_the_test_and_keeps_the_bot_user_id(LineApiOutcome outcome, int? status, string expected)
    {
        var client = new StubClient { Set = new LineApiResult(outcome, status, "req", "Invalid webhook endpoint URL") };

        var result = await RunAsync(client, "@anxin-demo");

        States(result).ShouldBe(["passed", "failed", "skipped"]);
        result.Checks[1].Message.ShouldContain(expected);
        result.BotUserId.ShouldBe(BotUserId);
        client.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_rejected_webhook_url_names_the_url_and_lines_reason()
    {
        var client = new StubClient { Set = new LineApiResult(LineApiOutcome.HttpError, 400, "req", "Invalid webhook endpoint URL") };

        var message = (await RunAsync(client, "@anxin-demo")).Checks[1].Message;

        message.ShouldContain(WebhookUrl);
        message.ShouldContain("（LINE：Invalid webhook endpoint URL）");
        message.ShouldContain("PublicChannels:PublicBaseUrl");
    }

    public static TheoryData<bool, int, string, string> WebhookTestReports() => new()
    {
        { false, 0, "COULD_NOT_CONNECT", "LINE 無法連線到 Webhook 網址（LINE：TLS handshake failure）" },
        { false, 0, "REQUEST_TIMEOUT", "沒有在時限內回應" },
        { false, 401, "ERROR_STATUS_CODE", "通常是 Channel secret 填錯了" },
        { false, 404, "ERROR_STATUS_CODE", "伺服器以 HTTP 404 回應 LINE 的測試事件" },
        { false, 0, "UNCLASSIFIED", "LINE 無法完成 Webhook 測試（UNCLASSIFIED）" },
    };

    [Theory]
    [MemberData(nameof(WebhookTestReports))]
    public async Task A_failed_webhook_test_explains_lines_report(bool success, int statusCode, string reason, string expected)
    {
        var detail = reason == "COULD_NOT_CONNECT" ? "TLS handshake failure" : statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var client = new StubClient
        {
            Test = new LineApiResult<LineWebhookTestResult>(LineApiOutcome.Succeeded, 200, "req", null, new LineWebhookTestResult(success, statusCode, reason, detail)),
        };

        var result = await RunAsync(client, "@anxin-demo");

        States(result).ShouldBe(["passed", "passed", "failed"]);
        result.Checks[2].Message.ShouldContain(expected);
        result.BotUserId.ShouldBe(BotUserId);
    }

    [Fact]
    public async Task Lines_hourly_limit_on_webhook_tests_is_a_failed_check_saying_so()
    {
        var client = new StubClient { Test = new LineApiResult<LineWebhookTestResult>(LineApiOutcome.RateLimited, 429, "req", "rate limit", null) };

        var result = await RunAsync(client, "@anxin-demo");

        States(result).ShouldBe(["passed", "passed", "failed"]);
        result.Checks[2].Message.ShouldBe(LineConnectionTester.WebhookTestRateLimitedMessage);
        result.Checks[2].Message.ShouldContain("60 次");
    }

    [Fact]
    public void An_unreadable_token_fails_the_first_check_without_calling_line()
    {
        var result = LineConnectionTester.TokenUnreadable();

        States(result).ShouldBe(["failed", "skipped", "skipped"]);
        result.Checks[0].Message.ShouldBe(LineConnectionTester.TokenUnreadableMessage);
        result.BotUserId.ShouldBeNull();
    }

    private static Task<LineConnectionTestResult> RunAsync(StubClient client, string officialAccountId) =>
        LineConnectionTester.RunAsync(client, Token, officialAccountId, WebhookUrl, CancellationToken);

    private static string[] States(LineConnectionTestResult result) =>
        [.. result.Checks.Select(check => check.State.ToString().ToLowerInvariant())];

    private sealed class StubClient : ILineMessagingClient
    {
        public List<string> Calls { get; } = [];

        public LineApiResult<LineBotInfo> Info { get; init; } =
            new(LineApiOutcome.Succeeded, 200, "req", null, new LineBotInfo(BotUserId, "@123abcde", "安心農場", "@anxin-demo"));

        public LineApiResult Set { get; init; } = new(LineApiOutcome.Succeeded, 200, "req", null);

        public LineApiResult<LineWebhookTestResult> Test { get; init; } =
            new(LineApiOutcome.Succeeded, 200, "req", null, new LineWebhookTestResult(true, 200, "OK", "200"));

        public Task<LineApiResult<LineBotInfo>> GetBotInfoAsync(string accessToken, CancellationToken cancellationToken)
        {
            Calls.Add($"info {accessToken}");
            return Task.FromResult(Info);
        }

        public Task<LineApiResult> SetWebhookEndpointAsync(string accessToken, string endpoint, CancellationToken cancellationToken)
        {
            Calls.Add($"set {accessToken} {endpoint}");
            return Task.FromResult(Set);
        }

        public Task<LineApiResult<LineWebhookTestResult>> TestWebhookEndpointAsync(string accessToken, string endpoint, CancellationToken cancellationToken)
        {
            Calls.Add($"test {accessToken} {endpoint}");
            return Task.FromResult(Test);
        }

        public Task<LineApiResult> ReplyAsync(string accessToken, string replyToken, IReadOnlyList<JsonObject> messages, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LineApiResult> PushAsync(string accessToken, string to, IReadOnlyList<JsonObject> messages, Guid? retryKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LineApiResult> StartLoadingAsync(string accessToken, string chatId, int loadingSeconds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
