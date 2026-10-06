using System.Text.Json.Nodes;

namespace SmartAgri.Application.Line;

/// <summary>
/// The LINE Messaging API calls this server makes (M5b plan §3 H), with the channel's access token
/// passed in on every call. Implemented in Infrastructure by <c>LineMessagingClient</c> over a typed
/// <c>HttpClient</c>; no LINE SDK.
/// </summary>
/// <remarks>
/// An expected failure — LINE refusing the token, rate limiting, an HTTP error, a timeout, an
/// unreachable network — is a <see cref="LineApiResult"/>, never an exception, so a caller can turn it
/// into a message (an external-service failure is not an HTTP error of ours). Only the caller's own
/// cancellation throws.
/// </remarks>
public interface ILineMessagingClient
{
    /// <summary><c>GET /v2/bot/info</c>: whose token this is.</summary>
    Task<LineApiResult<LineBotInfo>> GetBotInfoAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary><c>PUT /v2/bot/channel/webhook/endpoint</c> <c>{ endpoint }</c>: where LINE delivers
    /// the channel's webhook events (LINE may take up to a minute to use it).</summary>
    Task<LineApiResult> SetWebhookEndpointAsync(string accessToken, string endpoint, CancellationToken cancellationToken);

    /// <summary><c>POST /v2/bot/channel/webhook/test</c> <c>{ endpoint }</c>: LINE sends a signed
    /// test event to <paramref name="endpoint"/> and reports how it went. LINE allows 60 per hour per
    /// channel (<see cref="LineApiOutcome.RateLimited"/> beyond that).</summary>
    Task<LineApiResult<LineWebhookTestResult>> TestWebhookEndpointAsync(string accessToken, string endpoint, CancellationToken cancellationToken);

    /// <summary><c>POST /v2/bot/message/reply</c> <c>{ replyToken, messages }</c>: free, once per
    /// reply token, about a minute after the event. 1 to 5 message objects.</summary>
    Task<LineApiResult> ReplyAsync(string accessToken, string replyToken, IReadOnlyList<JsonObject> messages, CancellationToken cancellationToken);

    /// <summary><c>POST /v2/bot/message/push</c> <c>{ to, messages }</c>: counted against the
    /// account's monthly quota per recipient. 1 to 5 message objects; <paramref name="retryKey"/>
    /// (<c>X-Line-Retry-Key</c>) makes a retry idempotent.</summary>
    Task<LineApiResult> PushAsync(
        string accessToken, string to, IReadOnlyList<JsonObject> messages, Guid? retryKey, CancellationToken cancellationToken);

    /// <summary><c>POST /v2/bot/chat/loading/start</c> <c>{ chatId, loadingSeconds }</c>: the
    /// 「輸入中」 animation in a one-to-one chat, 5–60 seconds in steps of 5.</summary>
    Task<LineApiResult> StartLoadingAsync(string accessToken, string chatId, int loadingSeconds, CancellationToken cancellationToken);
}

/// <summary>How a LINE API call went.</summary>
public enum LineApiOutcome
{
    /// <summary>A 2xx response.</summary>
    Succeeded,

    /// <summary><c>401</c> or <c>403</c>: LINE does not accept the access token (wrong, reissued,
    /// revoked) or it lacks the permission.</summary>
    Unauthorized,

    /// <summary><c>429</c>: a rate limit, or the monthly message quota, is used up.</summary>
    RateLimited,

    /// <summary>Any other HTTP status (or a 2xx whose body could not be read).</summary>
    HttpError,

    /// <summary>No response within the client's timeout.</summary>
    TimedOut,

    /// <summary>The request could not be sent or the connection failed (DNS, TLS, refused, reset).</summary>
    NetworkError,
}

/// <summary>A LINE API call's outcome; never carries the access token or a message body.</summary>
/// <param name="StatusCode">The HTTP status, or <see langword="null"/> without a response.</param>
/// <param name="RequestId">LINE's <c>x-line-request-id</c> (for LINE support), when it answered.</param>
/// <param name="ErrorMessage">LINE's error <c>message</c> on a non-2xx response, shortened; LINE's
/// own words, never a credential.</param>
public record LineApiResult(LineApiOutcome Outcome, int? StatusCode, string? RequestId, string? ErrorMessage)
{
    public bool Succeeded => Outcome == LineApiOutcome.Succeeded;
}

/// <summary>A LINE API call's outcome and, when it succeeded, what LINE answered.</summary>
public sealed record LineApiResult<T>(LineApiOutcome Outcome, int? StatusCode, string? RequestId, string? ErrorMessage, T? Value)
    : LineApiResult(Outcome, StatusCode, RequestId, ErrorMessage)
    where T : class;

/// <summary><c>GET /v2/bot/info</c>'s answer (the fields used here).</summary>
/// <param name="UserId">The bot's own user id (<c>U</c> + 32 hexadecimal digits): a webhook's
/// <c>destination</c>.</param>
/// <param name="BasicId">The account's basic id, <c>@</c> first (e.g. <c>@123abcde</c>).</param>
/// <param name="PremiumId">The account's premium id, <c>@</c> first, if it bought one.</param>
public sealed record LineBotInfo(string UserId, string BasicId, string DisplayName, string? PremiumId);

/// <summary><c>POST /v2/bot/channel/webhook/test</c>'s answer.</summary>
/// <param name="Success">Whether the endpoint answered the test event with a 2xx.</param>
/// <param name="StatusCode">The endpoint's HTTP status (0 when LINE could not get one).</param>
/// <param name="Reason"><c>OK</c>, <c>COULD_NOT_CONNECT</c>, <c>REQUEST_TIMEOUT</c>,
/// <c>ERROR_STATUS_CODE</c> or <c>UNCLASSIFIED</c>.</param>
/// <param name="Detail">LINE's details (e.g. the status code, or a TLS error).</param>
public sealed record LineWebhookTestResult(bool Success, int StatusCode, string Reason, string Detail);

/// <summary>Message objects for <see cref="ILineMessagingClient.ReplyAsync"/> and
/// <see cref="ILineMessagingClient.PushAsync"/>.</summary>
public static class LineMessages
{
    /// <summary>A reply or push carries at most this many message objects.</summary>
    public const int MaxPerRequest = 5;

    /// <summary>A text message (at most 5,000 UTF-16 code units; the caller shortens it).</summary>
    public static JsonObject Text(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new JsonObject { ["type"] = "text", ["text"] = text };
    }
}
