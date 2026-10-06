using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// A fake LINE Messaging API (M5b #230, reused by #231/#232): an <see cref="HttpMessageHandler"/> put
/// under <c>LineMessagingClient</c>'s <c>HttpClient</c>, so nothing reaches the network. It knows the
/// bots added with <see cref="AddBot"/>, keyed by access token — every test uses its own token, so
/// tests sharing one host never see each other's bots, scripts or requests — and records every request.
/// </summary>
/// <remarks>
/// <para>
/// By default each endpoint answers like LINE: <c>GET /v2/bot/info</c> the bot's ids,
/// <c>PUT /v2/bot/channel/webhook/endpoint</c> stores the URL (<see cref="WebhookEndpointOf"/>),
/// <c>POST /v2/bot/channel/webhook/test</c> a successful test, reply and push <c>200</c>,
/// loading <c>202</c>; every response carries an <c>x-line-request-id</c>. An unknown token gets LINE's
/// <c>401</c>.
/// </para>
/// <para>
/// <see cref="Script"/> makes one endpoint of one bot fail (<c>401</c>, <c>429</c>, <c>500</c>, a
/// timeout or a network error) or answer with a chosen status and body;
/// <see cref="ScriptWebhookTest"/> sets what LINE reports about our webhook.
/// </para>
/// </remarks>
public sealed class FakeLineServer : HttpMessageHandler
{
    public const string BotInfo = "GET /v2/bot/info";
    public const string SetWebhookEndpoint = "PUT /v2/bot/channel/webhook/endpoint";
    public const string GetWebhookEndpoint = "GET /v2/bot/channel/webhook/endpoint";
    public const string TestWebhookEndpoint = "POST /v2/bot/channel/webhook/test";
    public const string Reply = "POST /v2/bot/message/reply";
    public const string Push = "POST /v2/bot/message/push";
    public const string StartLoading = "POST /v2/bot/chat/loading/start";

    private readonly ConcurrentDictionary<string, BotState> _bots = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<LineRequest> _requests = new();

    /// <summary>Every request received, in order (unknown tokens included).</summary>
    public IReadOnlyList<LineRequest> Requests => [.. _requests];

    /// <summary>Called with each request after it is recorded and before it is answered (e.g. to
    /// change the database while "LINE" is working).</summary>
    public Func<LineRequest, Task>? OnRequest { get; set; }

    /// <summary>Adds (or replaces) a bot reachable with <paramref name="bot"/>'s access token.</summary>
    public FakeLineServer AddBot(LineBot bot)
    {
        _bots[bot.AccessToken] = new BotState(bot);
        return this;
    }

    /// <summary>A bot with a fresh token, user id and basic id (and the given display name).</summary>
    public LineBot AddBot(string basicId, string displayName = "安心客服", string? premiumId = null)
    {
        var bot = new LineBot(
            "fake-line-token-" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8],
            "U" + Guid.NewGuid().ToString("N"),
            basicId,
            displayName,
            premiumId);
        AddBot(bot);
        return bot;
    }

    /// <summary>From now on <paramref name="endpoint"/> (e.g. <see cref="BotInfo"/>) answers
    /// <paramref name="behavior"/> for <paramref name="accessToken"/>'s bot.</summary>
    public void Script(string accessToken, string endpoint, LineBehavior behavior) =>
        Bot(accessToken).Scripts[endpoint] = behavior;

    /// <summary>Back to the default answers for <paramref name="accessToken"/>'s bot.</summary>
    public void ResetScripts(string accessToken) => Bot(accessToken).Scripts.Clear();

    /// <summary>What LINE reports when it has tested our webhook (the call itself is a <c>200</c>).</summary>
    public void ScriptWebhookTest(string accessToken, bool success, int statusCode, string reason, string detail) =>
        Script(accessToken, TestWebhookEndpoint, LineBehavior.Answer(
            HttpStatusCode.OK,
            new JsonObject
            {
                ["success"] = success,
                ["timestamp"] = "2026-10-06T08:00:00.000Z",
                ["statusCode"] = statusCode,
                ["reason"] = reason,
                ["detail"] = detail,
            }.ToJsonString()));

    /// <summary>The webhook URL last set for the bot, or <see langword="null"/>.</summary>
    public string? WebhookEndpointOf(string accessToken) => Bot(accessToken).WebhookEndpoint;

    /// <summary>The requests made with <paramref name="accessToken"/>, in order.</summary>
    public IReadOnlyList<LineRequest> RequestsWith(string accessToken) =>
        [.. _requests.Where(request => request.Authorization == "Bearer " + accessToken)];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        var recorded = new LineRequest(
            request.Method.Method,
            ApiPath(request.RequestUri!),
            request.RequestUri!,
            headers.GetValueOrDefault("Authorization"),
            headers.GetValueOrDefault("Content-Type"),
            body,
            headers);
        _requests.Enqueue(recorded);
        if (OnRequest is { } onRequest)
        {
            await onRequest(recorded);
        }

        var token = recorded.Authorization is { } authorization && authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            ? authorization["Bearer ".Length..]
            : null;
        if (token is null || !_bots.TryGetValue(token, out var bot))
        {
            return Respond(HttpStatusCode.Unauthorized, """{"message":"Authentication failed. Confirm that the access token in the authorization header is valid."}""");
        }

        var endpoint = recorded.Endpoint;
        if (bot.Scripts.TryGetValue(endpoint, out var behavior))
        {
            switch (behavior.Kind)
            {
                case LineBehaviorKind.TimeOut:
                    // What HttpClient sees when its Timeout elapses: a cancellation the caller did not ask for.
                    throw new TaskCanceledException("The fake LINE server timed out.");
                case LineBehaviorKind.NetworkError:
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "The fake LINE server refused the connection.");
                case LineBehaviorKind.Hang:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
                default:
                    return Respond(behavior.Status, behavior.Body);
            }
        }

        return endpoint switch
        {
            BotInfo => Respond(HttpStatusCode.OK, new JsonObject
            {
                ["userId"] = bot.Bot.UserId,
                ["basicId"] = bot.Bot.BasicId,
                ["premiumId"] = bot.Bot.PremiumId,
                ["displayName"] = bot.Bot.DisplayName,
                ["chatMode"] = "bot",
                ["markAsReadMode"] = "auto",
            }.ToJsonString()),
            SetWebhookEndpoint => SetEndpoint(bot, body),
            GetWebhookEndpoint => bot.WebhookEndpoint is { } current
                ? Respond(HttpStatusCode.OK, new JsonObject { ["endpoint"] = current, ["active"] = true }.ToJsonString())
                : Respond(HttpStatusCode.NotFound, """{"message":"Not found"}"""),
            TestWebhookEndpoint => Respond(HttpStatusCode.OK, """{"success":true,"timestamp":"2026-10-06T08:00:00.000Z","statusCode":200,"reason":"OK","detail":"200"}"""),
            Reply => Respond(HttpStatusCode.OK, """{"sentMessages":[]}"""),
            Push => Respond(HttpStatusCode.OK, """{"sentMessages":[]}"""),
            StartLoading => Respond(HttpStatusCode.Accepted, "{}"),
            _ => Respond(HttpStatusCode.NotFound, """{"message":"Not found"}"""),
        };
    }

    /// <summary>A handler instance is disposed by <c>IHttpClientFactory</c> when it rotates handlers;
    /// this one keeps working (its state is the whole point).</summary>
    protected override void Dispose(bool disposing)
    {
    }

    private static HttpResponseMessage SetEndpoint(BotState bot, string? body)
    {
        var endpoint = body is null ? null : JsonNode.Parse(body)?["endpoint"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Respond(HttpStatusCode.BadRequest, """{"message":"The request body has 1 error(s)"}""");
        }

        bot.WebhookEndpoint = endpoint;
        return Respond(HttpStatusCode.OK, "{}");
    }

    /// <summary>The path from <c>/v2/</c> on, whatever base path <c>Line:ApiBaseUrl</c> has.</summary>
    private static string ApiPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        var start = path.IndexOf("/v2/", StringComparison.Ordinal);
        return start >= 0 ? path[start..] : path;
    }

    private BotState Bot(string accessToken) =>
        _bots.TryGetValue(accessToken, out var bot) ? bot : throw new InvalidOperationException("No fake LINE bot has that token.");

    private static HttpResponseMessage Respond(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.Add("x-line-request-id", Guid.NewGuid().ToString());
        return response;
    }

    private sealed class BotState
    {
        public BotState(LineBot bot)
        {
            Bot = bot;
        }

        public LineBot Bot { get; }

        public ConcurrentDictionary<string, LineBehavior> Scripts { get; } = new(StringComparer.Ordinal);

        public string? WebhookEndpoint { get; set; }
    }
}

/// <summary>A bot of the <see cref="FakeLineServer"/>.</summary>
public sealed record LineBot(string AccessToken, string UserId, string BasicId, string DisplayName, string? PremiumId = null);

/// <summary>A request the <see cref="FakeLineServer"/> received.</summary>
/// <param name="Path">The API path, e.g. <c>/v2/bot/info</c> (without the base address's own path).</param>
/// <param name="Authorization">The <c>Authorization</c> header as sent.</param>
public sealed record LineRequest(
    string Method,
    string Path,
    Uri Uri,
    string? Authorization,
    string? ContentType,
    string? Body,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary><c>METHOD /path</c>, as <see cref="FakeLineServer.BotInfo"/> and the others.</summary>
    public string Endpoint => $"{Method} {Path}";

    /// <summary>The body as JSON.</summary>
    public JsonElement Json => JsonDocument.Parse(Body ?? "null").RootElement.Clone();
}

public enum LineBehaviorKind
{
    Answer,
    TimeOut,
    NetworkError,
    Hang,
}

/// <summary>How a scripted endpoint answers.</summary>
public sealed record LineBehavior(LineBehaviorKind Kind, HttpStatusCode Status, string Body)
{
    public static LineBehavior Unauthorized { get; } = Answer(
        HttpStatusCode.Unauthorized, """{"message":"Authentication failed. Confirm that the access token in the authorization header is valid."}""");

    public static LineBehavior Forbidden { get; } = Answer(
        HttpStatusCode.Forbidden, """{"message":"Access to this API is not available for your account"}""");

    public static LineBehavior RateLimited { get; } = Answer(
        (HttpStatusCode)429, """{"message":"The API rate limit has been exceeded. Try again later."}""");

    public static LineBehavior ServerError { get; } = Answer(HttpStatusCode.InternalServerError, """{"message":"Internal server error"}""");

    /// <summary>Throws as <c>HttpClient</c> does when its timeout elapses, at once.</summary>
    public static LineBehavior TimeOut { get; } = new(LineBehaviorKind.TimeOut, 0, string.Empty);

    public static LineBehavior NetworkError { get; } = new(LineBehaviorKind.NetworkError, 0, string.Empty);

    /// <summary>Never answers (until cancelled): for a real <c>HttpClient.Timeout</c>.</summary>
    public static LineBehavior Hang { get; } = new(LineBehaviorKind.Hang, 0, string.Empty);

    public static LineBehavior Answer(HttpStatusCode status, string body) => new(LineBehaviorKind.Answer, status, body);
}
