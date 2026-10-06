using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SmartAgri.Application.Line;

namespace SmartAgri.Infrastructure.Line;

/// <summary>
/// <see cref="ILineMessagingClient"/> over a typed <see cref="HttpClient"/> (M5b plan §3 H): only the
/// endpoints this server uses, hand-written — there is no official .NET SDK and the community ones are
/// archived. The <see cref="HttpClient"/> is configured by the Api's <c>AddLineMessaging</c>: base
/// address <c>Line:ApiBaseUrl</c>, <see cref="Timeout"/>, and none of <c>IHttpClientFactory</c>'s own
/// request loggers (they would log full URLs and, at Trace, headers).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is logged:</b> one line per call — method, path, HTTP status and LINE's
/// <c>x-line-request-id</c> (which LINE support asks for; LINE keeps no logs for us) — or the kind of
/// failure. Never the access token, a request or response body, a reply token or a user id.
/// </para>
/// <para>
/// <b>Errors:</b> every expected failure is a <see cref="LineApiResult"/>
/// (<see cref="LineApiOutcome"/>); only the caller's own cancellation throws.
/// </para>
/// </remarks>
public sealed class LineMessagingClient : ILineMessagingClient
{
    /// <summary>The named <see cref="HttpClient"/> this client is built on.</summary>
    public const string HttpClientName = "line-messaging";

    /// <summary>How long a call may take before it is <see cref="LineApiOutcome.TimedOut"/>.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The response header that identifies a request to LINE support.</summary>
    public const string RequestIdHeader = "x-line-request-id";

    /// <summary>The push request header that makes a retry idempotent.</summary>
    public const string RetryKeyHeader = "X-Line-Retry-Key";

    private const int MaxErrorMessageLength = 300;

    private readonly HttpClient _http;
    private readonly ILogger<LineMessagingClient> _logger;

    public LineMessagingClient(HttpClient http, ILogger<LineMessagingClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task<LineApiResult<LineBotInfo>> GetBotInfoAsync(string accessToken, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "v2/bot/info", accessToken, body: null, retryKey: null, ReadBotInfo, cancellationToken);

    public async Task<LineApiResult> SetWebhookEndpointAsync(string accessToken, string endpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return await SendAsync<object>(
            HttpMethod.Put,
            "v2/bot/channel/webhook/endpoint",
            accessToken,
            writer => writer.WriteString("endpoint", endpoint),
            retryKey: null,
            read: null,
            cancellationToken);
    }

    public Task<LineApiResult<LineWebhookTestResult>> TestWebhookEndpointAsync(
        string accessToken, string endpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return SendAsync(
            HttpMethod.Post,
            "v2/bot/channel/webhook/test",
            accessToken,
            writer => writer.WriteString("endpoint", endpoint),
            retryKey: null,
            ReadWebhookTest,
            cancellationToken);
    }

    public async Task<LineApiResult> ReplyAsync(
        string accessToken, string replyToken, IReadOnlyList<JsonObject> messages, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyToken);
        RequireMessages(messages);
        return await SendAsync<object>(
            HttpMethod.Post,
            "v2/bot/message/reply",
            accessToken,
            writer =>
            {
                writer.WriteString("replyToken", replyToken);
                WriteMessages(writer, messages);
            },
            retryKey: null,
            read: null,
            cancellationToken);
    }

    public async Task<LineApiResult> PushAsync(
        string accessToken, string to, IReadOnlyList<JsonObject> messages, Guid? retryKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        RequireMessages(messages);
        return await SendAsync<object>(
            HttpMethod.Post,
            "v2/bot/message/push",
            accessToken,
            writer =>
            {
                writer.WriteString("to", to);
                WriteMessages(writer, messages);
            },
            retryKey,
            read: null,
            cancellationToken);
    }

    public async Task<LineApiResult> StartLoadingAsync(
        string accessToken, string chatId, int loadingSeconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        if (loadingSeconds is < 5 or > 60 || loadingSeconds % 5 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(loadingSeconds), loadingSeconds, "LINE accepts 5 to 60 seconds in steps of 5.");
        }

        return await SendAsync<object>(
            HttpMethod.Post,
            "v2/bot/chat/loading/start",
            accessToken,
            writer =>
            {
                writer.WriteString("chatId", chatId);
                writer.WriteNumber("loadingSeconds", loadingSeconds);
            },
            retryKey: null,
            read: null,
            cancellationToken);
    }

    private async Task<LineApiResult<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        string accessToken,
        Action<Utf8JsonWriter>? body,
        Guid? retryKey,
        Func<JsonElement, T?>? read,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var logPath = "/" + path;

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (retryKey is { } key)
        {
            request.Headers.Add(RetryKeyHeader, key.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonBody(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("LINE {Method} {Path} timed out after {TimeoutSeconds} s.", method.Method, logPath, _http.Timeout.TotalSeconds);
            return new LineApiResult<T>(LineApiOutcome.TimedOut, null, null, null, null);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning("LINE {Method} {Path} failed: {Error}.", method.Method, logPath, exception.HttpRequestError);
            return new LineApiResult<T>(LineApiOutcome.NetworkError, null, null, null, null);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var requestId = response.Headers.TryGetValues(RequestIdHeader, out var values) ? values.FirstOrDefault() : null;
            var level = response.IsSuccessStatusCode ? LogLevel.Information : LogLevel.Warning;
            _logger.Log(level, "LINE {Method} {Path} answered {StatusCode} (x-line-request-id {RequestId}).", method.Method, logPath, status, requestId);

            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new LineApiResult<T>(LineApiOutcome.TimedOut, status, requestId, null, null);
            }
            catch (HttpRequestException)
            {
                return new LineApiResult<T>(LineApiOutcome.NetworkError, status, requestId, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                var outcome = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LineApiOutcome.Unauthorized,
                    HttpStatusCode.TooManyRequests => LineApiOutcome.RateLimited,
                    _ => LineApiOutcome.HttpError,
                };
                return new LineApiResult<T>(outcome, status, requestId, ErrorMessageOf(text), null);
            }

            if (read is null)
            {
                return new LineApiResult<T>(LineApiOutcome.Succeeded, status, requestId, null, null);
            }

            T? value = null;
            try
            {
                using var document = JsonDocument.Parse(text);
                value = read(document.RootElement);
            }
            catch (JsonException)
            {
            }

            if (value is null)
            {
                _logger.LogWarning("LINE {Method} {Path} answered {StatusCode} with a body this client cannot read (x-line-request-id {RequestId}).", method.Method, logPath, status, requestId);
                return new LineApiResult<T>(LineApiOutcome.HttpError, status, requestId, "Unexpected response body.", null);
            }

            return new LineApiResult<T>(LineApiOutcome.Succeeded, status, requestId, null, value);
        }
    }

    private static ByteArrayContent JsonBody(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<JsonObject> messages)
    {
        writer.WriteStartArray("messages");
        foreach (var message in messages)
        {
            message.WriteTo(writer);
        }

        writer.WriteEndArray();
    }

    private static void RequireMessages(IReadOnlyList<JsonObject> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count is 0 or > LineMessages.MaxPerRequest)
        {
            throw new ArgumentException($"LINE takes 1 to {LineMessages.MaxPerRequest} message objects per request.", nameof(messages));
        }
    }

    private static LineBotInfo? ReadBotInfo(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && String(root, "userId") is { Length: > 0 } userId
        && String(root, "basicId") is { Length: > 0 } basicId
            ? new LineBotInfo(userId, basicId, String(root, "displayName") ?? string.Empty, String(root, "premiumId"))
            : null;

    private static LineWebhookTestResult? ReadWebhookTest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out var success)
            || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        var statusCode = root.TryGetProperty("statusCode", out var code) && code.TryGetInt32(out var parsed) ? parsed : 0;
        return new LineWebhookTestResult(success.GetBoolean(), statusCode, String(root, "reason") ?? string.Empty, String(root, "detail") ?? string.Empty);
    }

    /// <summary>LINE's error body is <c>{ "message": "…", "details": [...] }</c>; only the message,
    /// shortened.</summary>
    private static string? ErrorMessageOf(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object && String(document.RootElement, "message") is { Length: > 0 } message)
            {
                return message.Length <= MaxErrorMessageLength ? message : message[..MaxErrorMessageLength];
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
