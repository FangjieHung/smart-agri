using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Line;
using SmartAgri.Infrastructure.Line;

namespace SmartAgri.Api.Tests.Line;

/// <summary>
/// <see cref="LineMessagingClient"/> against the <see cref="FakeLineServer"/> (M5b #230): each
/// endpoint's request (method, path, JSON body, bearer token) and the mapping of LINE's answers —
/// success, <c>401</c>/<c>403</c>, <c>429</c>, other errors, a timeout, a network failure — to
/// <see cref="LineApiResult"/>s, never exceptions. No network, no database.
/// </summary>
public sealed class LineMessagingClientTests : IDisposable
{
    private readonly FakeLineServer _line = new();
    private readonly CapturingLogger _log = new();
    private readonly HttpClient _http;
    private readonly LineMessagingClient _client;
    private readonly LineBot _bot;

    public LineMessagingClientTests()
    {
        _http = new HttpClient(_line) { BaseAddress = new Uri("https://api.line.me/"), Timeout = LineMessagingClient.Timeout };
        _client = new LineMessagingClient(_http, _log);
        _bot = _line.AddBot("@123abcde", "安心農場", premiumId: "@anxin-demo");
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _http.Dispose();

    // --- Request shapes ---------------------------------------------------------------------------

    [Fact]
    public async Task Bot_info_is_a_get_with_the_bearer_token_and_reads_the_ids()
    {
        var result = await _client.GetBotInfoAsync(_bot.AccessToken, CancellationToken);

        result.Outcome.ShouldBe(LineApiOutcome.Succeeded);
        result.StatusCode.ShouldBe(200);
        result.RequestId.ShouldNotBeNullOrEmpty();
        result.Value.ShouldBe(new LineBotInfo(_bot.UserId, "@123abcde", "安心農場", "@anxin-demo"));
        var request = _line.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("GET");
        request.Uri.ShouldBe(new Uri("https://api.line.me/v2/bot/info"));
        request.Authorization.ShouldBe("Bearer " + _bot.AccessToken);
        request.Body.ShouldBeNull();
    }

    [Fact]
    public async Task Setting_the_webhook_endpoint_is_a_put_of_the_endpoint()
    {
        var result = await _client.SetWebhookEndpointAsync(_bot.AccessToken, "https://assistant.example.org/api/v1/line/webhook/x", CancellationToken);

        result.Outcome.ShouldBe(LineApiOutcome.Succeeded);
        var request = _line.Requests.ShouldHaveSingleItem();
        request.Endpoint.ShouldBe(FakeLineServer.SetWebhookEndpoint);
        request.Authorization.ShouldBe("Bearer " + _bot.AccessToken);
        request.ContentType.ShouldBe("application/json; charset=utf-8");
        request.Body.ShouldBe("""{"endpoint":"https://assistant.example.org/api/v1/line/webhook/x"}""");
        _line.WebhookEndpointOf(_bot.AccessToken).ShouldBe("https://assistant.example.org/api/v1/line/webhook/x");
    }

    [Fact]
    public async Task Testing_the_webhook_endpoint_is_a_post_of_the_endpoint_and_reads_lines_report()
    {
        _line.ScriptWebhookTest(_bot.AccessToken, success: false, statusCode: 401, reason: "ERROR_STATUS_CODE", detail: "401");

        var result = await _client.TestWebhookEndpointAsync(_bot.AccessToken, "https://assistant.example.org/hook", CancellationToken);

        result.Outcome.ShouldBe(LineApiOutcome.Succeeded);
        result.Value.ShouldBe(new LineWebhookTestResult(false, 401, "ERROR_STATUS_CODE", "401"));
        var request = _line.Requests.ShouldHaveSingleItem();
        request.Endpoint.ShouldBe(FakeLineServer.TestWebhookEndpoint);
        request.Authorization.ShouldBe("Bearer " + _bot.AccessToken);
        request.Body.ShouldBe("""{"endpoint":"https://assistant.example.org/hook"}""");
    }

    [Fact]
    public async Task Reply_posts_the_reply_token_and_the_message_objects()
    {
        var result = await _client.ReplyAsync(
            _bot.AccessToken, "reply-token-1", [LineMessages.Text("退貨期限是 7 天。"), LineMessages.Text("第二則")], CancellationToken);

        result.Outcome.ShouldBe(LineApiOutcome.Succeeded);
        var request = _line.Requests.ShouldHaveSingleItem();
        request.Endpoint.ShouldBe(FakeLineServer.Reply);
        request.Authorization.ShouldBe("Bearer " + _bot.AccessToken);
        var json = request.Json;
        json.EnumerateObject().Select(property => property.Name).ShouldBe(["replyToken", "messages"]);
        json.GetProperty("replyToken").GetString().ShouldBe("reply-token-1");
        json.GetProperty("messages").EnumerateArray()
            .Select(message => $"{message.GetProperty("type").GetString()}:{message.GetProperty("text").GetString()}")
            .ShouldBe(["text:退貨期限是 7 天。", "text:第二則"]);
    }

    [Fact]
    public async Task Push_posts_the_recipient_and_messages_with_an_optional_retry_key()
    {
        var retryKey = Guid.Parse("0199b6a0-0000-7000-8000-000000000001");

        (await _client.PushAsync(_bot.AccessToken, "Uaaaa", [LineMessages.Text("補送")], retryKey, CancellationToken))
            .Outcome.ShouldBe(LineApiOutcome.Succeeded);
        (await _client.PushAsync(_bot.AccessToken, "Ubbbb", [LineMessages.Text("再一次")], null, CancellationToken))
            .Outcome.ShouldBe(LineApiOutcome.Succeeded);

        var requests = _line.Requests;
        requests.Select(request => request.Endpoint).ShouldBe([FakeLineServer.Push, FakeLineServer.Push]);
        requests[0].Json.GetProperty("to").GetString().ShouldBe("Uaaaa");
        requests[0].Json.GetProperty("messages")[0].GetProperty("text").GetString().ShouldBe("補送");
        requests[0].Headers["X-Line-Retry-Key"].ShouldBe("0199b6a0-0000-7000-8000-000000000001");
        requests[1].Headers.ContainsKey("X-Line-Retry-Key").ShouldBeFalse();
        requests.ShouldAllBe(request => request.Authorization == "Bearer " + _bot.AccessToken);
    }

    [Fact]
    public async Task Loading_posts_the_chat_id_and_seconds_and_202_is_success()
    {
        var result = await _client.StartLoadingAsync(_bot.AccessToken, "Uaaaa", 20, CancellationToken);

        result.Outcome.ShouldBe(LineApiOutcome.Succeeded);
        result.StatusCode.ShouldBe(202);
        var request = _line.Requests.ShouldHaveSingleItem();
        request.Endpoint.ShouldBe(FakeLineServer.StartLoading);
        request.Body.ShouldBe("""{"chatId":"Uaaaa","loadingSeconds":20}""");
    }

    [Fact]
    public async Task Arguments_outside_lines_limits_are_refused_before_any_request()
    {
        await Should.ThrowAsync<ArgumentException>(() => _client.ReplyAsync(_bot.AccessToken, "r", [], CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => _client.ReplyAsync(
            _bot.AccessToken, "r", [.. Enumerable.Range(0, 6).Select(i => LineMessages.Text($"{i}"))], CancellationToken));
        foreach (var seconds in new[] { 0, 4, 7, 65 })
        {
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _client.StartLoadingAsync(_bot.AccessToken, "Uaaaa", seconds, CancellationToken));
        }

        await Should.ThrowAsync<ArgumentException>(() => _client.GetBotInfoAsync(" ", CancellationToken));
        _line.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_base_url_with_a_path_keeps_it()
    {
        using var http = new HttpClient(_line) { BaseAddress = new LineOptions { ApiBaseUrl = "http://fake-line.test/line" }.ResolvedApiBaseUrl };
        var client = new LineMessagingClient(http, _log);

        (await client.GetBotInfoAsync(_bot.AccessToken, CancellationToken)).Outcome.ShouldBe(LineApiOutcome.Succeeded);

        _line.Requests.ShouldHaveSingleItem().Uri.ShouldBe(new Uri("http://fake-line.test/line/v2/bot/info"));
    }

    // --- Result mapping ---------------------------------------------------------------------------

    public static TheoryData<string, string> Endpoints() => new()
    {
        { FakeLineServer.BotInfo, "bot info" },
        { FakeLineServer.SetWebhookEndpoint, "set webhook endpoint" },
        { FakeLineServer.TestWebhookEndpoint, "test webhook endpoint" },
        { FakeLineServer.Reply, "reply" },
        { FakeLineServer.Push, "push" },
        { FakeLineServer.StartLoading, "loading" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Every_endpoint_maps_401_403_429_500_a_timeout_and_a_network_error_to_results(string endpoint, string because)
    {
        var cases = new (LineBehavior Behavior, LineApiOutcome Outcome, int? Status)[]
        {
            (LineBehavior.Unauthorized, LineApiOutcome.Unauthorized, 401),
            (LineBehavior.Forbidden, LineApiOutcome.Unauthorized, 403),
            (LineBehavior.RateLimited, LineApiOutcome.RateLimited, 429),
            (LineBehavior.ServerError, LineApiOutcome.HttpError, 500),
            (LineBehavior.Answer(HttpStatusCode.BadRequest, """{"message":"Invalid webhook endpoint URL"}"""), LineApiOutcome.HttpError, 400),
            (LineBehavior.TimeOut, LineApiOutcome.TimedOut, null),
            (LineBehavior.NetworkError, LineApiOutcome.NetworkError, null),
        };

        foreach (var (behavior, outcome, status) in cases)
        {
            _line.Script(_bot.AccessToken, endpoint, behavior);

            var result = await CallAsync(endpoint);

            result.Outcome.ShouldBe(outcome, $"{because}: {behavior.Kind} {behavior.Status}");
            result.StatusCode.ShouldBe(status, because);
            result.Succeeded.ShouldBeFalse(because);
            if (status is not null)
            {
                result.RequestId.ShouldNotBeNullOrEmpty(because);
                result.ErrorMessage.ShouldNotBeNullOrEmpty(because);
            }
        }
    }

    [Fact]
    public async Task An_unknown_token_is_unauthorized()
    {
        var result = await _client.GetBotInfoAsync("not-a-token-the-fake-knows-0123456789abcdef", CancellationToken);

        result.Outcome.ShouldBe(LineApiOutcome.Unauthorized);
        result.ErrorMessage!.ShouldContain("Authentication failed");
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task A_2xx_body_that_cannot_be_read_is_an_http_error()
    {
        _line.Script(_bot.AccessToken, FakeLineServer.BotInfo, LineBehavior.Answer(HttpStatusCode.OK, "<html>proxy</html>"));
        (await _client.GetBotInfoAsync(_bot.AccessToken, CancellationToken)).Outcome.ShouldBe(LineApiOutcome.HttpError);

        _line.Script(_bot.AccessToken, FakeLineServer.TestWebhookEndpoint, LineBehavior.Answer(HttpStatusCode.OK, """{"statusCode":200}"""));
        (await _client.TestWebhookEndpointAsync(_bot.AccessToken, "https://x.test/", CancellationToken)).Outcome.ShouldBe(LineApiOutcome.HttpError);
    }

    [Fact]
    public async Task A_real_http_client_timeout_is_timed_out()
    {
        using var http = new HttpClient(_line) { BaseAddress = new Uri("https://api.line.me/"), Timeout = TimeSpan.FromMilliseconds(100) };
        var client = new LineMessagingClient(http, _log);
        _line.Script(_bot.AccessToken, FakeLineServer.BotInfo, LineBehavior.Hang);

        var result = await client.GetBotInfoAsync(_bot.AccessToken, CancellationToken);

        result.Outcome.ShouldBe(LineApiOutcome.TimedOut);
        _log.Lines.ShouldContain(line => line.Contains("GET /v2/bot/info timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_callers_own_cancellation_throws()
    {
        _line.Script(_bot.AccessToken, FakeLineServer.BotInfo, LineBehavior.Hang);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => _client.GetBotInfoAsync(_bot.AccessToken, cancellation.Token));
    }

    // --- Logging ----------------------------------------------------------------------------------

    [Fact]
    public async Task Each_call_logs_method_path_status_and_request_id_but_no_token_or_body()
    {
        await _client.GetBotInfoAsync(_bot.AccessToken, CancellationToken);
        await _client.ReplyAsync(_bot.AccessToken, "reply-token-secretish", [LineMessages.Text("祕密的回答內容")], CancellationToken);
        await _client.PushAsync(_bot.AccessToken, "Urecipient0000", [LineMessages.Text("另一段內容")], null, CancellationToken);
        _line.Script(_bot.AccessToken, FakeLineServer.SetWebhookEndpoint, LineBehavior.Unauthorized);
        await _client.SetWebhookEndpointAsync(_bot.AccessToken, "https://hook.example.org/x", CancellationToken);

        var requestIds = _line.Requests.Count;
        requestIds.ShouldBe(4);
        _log.Lines.Count.ShouldBe(4);
        _log.Lines[0].ShouldStartWith("Information: LINE GET /v2/bot/info answered 200 (x-line-request-id ");
        _log.Lines[1].ShouldStartWith("Information: LINE POST /v2/bot/message/reply answered 200");
        _log.Lines[3].ShouldStartWith("Warning: LINE PUT /v2/bot/channel/webhook/endpoint answered 401");
        foreach (var line in _log.Lines)
        {
            line.ShouldNotContain(_bot.AccessToken);
            line.ShouldNotContain(_bot.AccessToken[..20]);
            line.ShouldNotContain("Bearer");
            line.ShouldNotContain("reply-token-secretish");
            line.ShouldNotContain("祕密");
            line.ShouldNotContain("Urecipient0000");
            line.ShouldNotContain("hook.example.org");
        }
    }

    private async Task<LineApiResult> CallAsync(string endpoint) =>
        endpoint switch
        {
            FakeLineServer.BotInfo => await _client.GetBotInfoAsync(_bot.AccessToken, CancellationToken),
            FakeLineServer.SetWebhookEndpoint => await _client.SetWebhookEndpointAsync(_bot.AccessToken, "https://x.test/hook", CancellationToken),
            FakeLineServer.TestWebhookEndpoint => await _client.TestWebhookEndpointAsync(_bot.AccessToken, "https://x.test/hook", CancellationToken),
            FakeLineServer.Reply => await _client.ReplyAsync(_bot.AccessToken, "r", [LineMessages.Text("a")], CancellationToken),
            FakeLineServer.Push => await _client.PushAsync(_bot.AccessToken, "Uaaaa", [LineMessages.Text("a")], null, CancellationToken),
            FakeLineServer.StartLoading => await _client.StartLoadingAsync(_bot.AccessToken, "Uaaaa", 5, CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };

    /// <summary>Keeps every formatted log line with its level, with the exception if any.</summary>
    private sealed class CapturingLogger : ILogger<LineMessagingClient>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue($"{logLevel}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception)}");
    }
}
