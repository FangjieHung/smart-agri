using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace SmartAgri.Api.Coordinator;

/// <summary>How a Graph API call went.</summary>
public enum FacebookGraphOutcome
{
    Succeeded,

    /// <summary>Graph answered with its error object (<see cref="FacebookGraphResult.Error"/>).</summary>
    GraphError,

    /// <summary>An HTTP error without a readable error object, or a success body this client cannot read.</summary>
    HttpError,

    TimedOut,

    NetworkError,
}

/// <summary>Graph's error object, without its <c>message</c> (text from outside, kept out of logs and pages).</summary>
/// <param name="Code">e.g. <c>190</c> (token), <c>10</c>/<c>200</c> (permission), <c>4</c>/<c>17</c>/<c>32</c>/<c>613</c> (throttled).</param>
/// <param name="Subcode"><c>error_subcode</c>, e.g. <c>463</c> (expired), <c>460</c> (password changed), <c>33</c> (no such object).</param>
/// <param name="FbTraceId">For Meta support; safe to log.</param>
public sealed record FacebookGraphError(int Code, int? Subcode, string? Type, string? FbTraceId);

/// <summary>A Graph API call's result: its outcome, HTTP status and Graph's error (no secret, no text from outside).</summary>
public class FacebookGraphResult
{
    public FacebookGraphResult(FacebookGraphOutcome outcome, int? statusCode, FacebookGraphError? error)
    {
        Outcome = outcome;
        StatusCode = statusCode;
        Error = error;
    }

    public FacebookGraphOutcome Outcome { get; }

    public int? StatusCode { get; }

    public FacebookGraphError? Error { get; }

    public bool Succeeded => Outcome == FacebookGraphOutcome.Succeeded;

    /// <summary>Never a value (it may be a token).</summary>
    public override string ToString() =>
        $"{nameof(FacebookGraphResult)} {{ Outcome = {Outcome}, StatusCode = {StatusCode}, Code = {Error?.Code}, Subcode = {Error?.Subcode} }}";
}

/// <summary>A Graph API call's result with what it read (<see cref="Value"/> when <see cref="FacebookGraphResult.Succeeded"/>).</summary>
public sealed class FacebookGraphResult<T> : FacebookGraphResult
    where T : class
{
    public FacebookGraphResult(FacebookGraphOutcome outcome, int? statusCode, FacebookGraphError? error, T? value)
        : base(outcome, statusCode, error)
    {
        Value = value;
    }

    public T? Value { get; }
}

/// <summary>A page of <c>GET /me/accounts</c>: its id, name, page access token and the user's tasks on it.
/// <see cref="ToString"/> shows no token.</summary>
public sealed record FacebookPage(string Id, string Name, string AccessToken, IReadOnlyList<string> Tasks)
{
    /// <summary>The task publishing needs (<c>POST /{page-id}/photos</c> and <c>/feed</c>).</summary>
    public const string CreateContentTask = "CREATE_CONTENT";

    /// <summary>Whether the user may post on the page.</summary>
    public bool CanCreateContent => Tasks.Contains(CreateContentTask, StringComparer.Ordinal);

    public override string ToString() => $"{nameof(FacebookPage)} {{ Id = {Id}, Tasks = {string.Join(',', Tasks)} }}";
}

/// <summary>
/// The few Graph API calls of FB 粉專貼文 (store-coordinator design §5 Slice 8, #379), hand-written over a typed
/// <see cref="HttpClient"/> pinned to <see cref="ApiVersion"/>: the Facebook Login code exchange and the long-lived
/// user token, the user's pages (<c>GET /me/accounts</c>), a photo post (<c>POST /{page-id}/photos</c> with
/// <c>url</c> and <c>caption</c> — Graph documents <c>message</c> there as deprecated in favour of <c>caption</c>), a text
/// post (<c>POST /{page-id}/feed</c> with <c>message</c>) and deleting a post (<c>DELETE /{post-id}</c>). How errors are
/// read: <see cref="FacebookGraphErrors"/> and the remarks of <see cref="CoordinatorFacebookPostExecutor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Secrets</b>: every call made with an access token carries its <c>appsecret_proof</c>
/// (<see cref="AppSecretProof"/>); the token and the proof go in the form body of a <c>POST</c>, in the query of a
/// <c>GET</c> or <c>DELETE</c> (the documented <c>access_token</c> parameter), and the code exchange's
/// <c>client_secret</c> in its query. No URL, body, token, proof or Graph message is logged: one line per call with the
/// endpoint's shape (<c>/{page-id}/photos</c>), the HTTP status, Graph's code and subcode and its <c>fbtrace_id</c>. The
/// <see cref="HttpClient"/> has none of <c>IHttpClientFactory</c>'s own loggers.
/// </para>
/// <para><b>Errors</b>: every expected failure is a <see cref="FacebookGraphResult"/>; only the caller's own cancellation throws.</para>
/// </remarks>
public sealed class FacebookGraphClient
{
    /// <summary>The named <see cref="HttpClient"/> this client is built on.</summary>
    public const string HttpClientName = "facebook-graph";

    /// <summary>The Graph API version every call and the consent screen use (latest on 2026-11-02, released 2026-07-29).</summary>
    public const string ApiVersion = "v26.0";

    /// <summary>The Facebook Login dialog.</summary>
    public const string DialogUrl = "https://www.facebook.com/" + ApiVersion + "/dialog/oauth";

    /// <summary>Graph's address (the <see cref="HttpClient"/>'s base address).</summary>
    public static readonly Uri BaseAddress = new("https://graph.facebook.com/" + ApiVersion + "/");

    /// <summary>How long a call may take: a photo post makes Facebook download the image first.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Most pages read from <c>/me/accounts</c> (one request; the selection form shows at most as many).</summary>
    public const int PagesLimit = 100;

    private readonly HttpClient _http;
    private readonly CoordinatorFacebookOptions _options;
    private readonly ILogger<FacebookGraphClient> _logger;

    public FacebookGraphClient(HttpClient http, IOptions<CoordinatorFacebookOptions> options, ILogger<FacebookGraphClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary><c>appsecret_proof</c>: HMAC-SHA256 of <paramref name="accessToken"/> keyed with <paramref name="appSecret"/>,
    /// lowercase hex.</summary>
    public static string AppSecretProof(string appSecret, string accessToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(appSecret);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(accessToken)));
    }

    /// <summary>The callback's code → a (short-lived) user access token.</summary>
    public Task<FacebookGraphResult<string>> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(redirectUri);
        var (appId, appSecret) = App();
        var path = QueryHelpers.AddQueryString("oauth/access_token", new Dictionary<string, string?>
        {
            ["client_id"] = appId,
            ["redirect_uri"] = redirectUri,
            ["client_secret"] = appSecret,
            ["code"] = code,
        });
        return SendAsync(HttpMethod.Get, path, "/oauth/access_token (code)", null, ReadAccessToken, cancellationToken);
    }

    /// <summary>A user access token → a long-lived one (about 60 days; the page tokens read with it do not expire).</summary>
    public Task<FacebookGraphResult<string>> ExchangeForLongLivedAsync(string userToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userToken);
        var (appId, appSecret) = App();
        var path = QueryHelpers.AddQueryString("oauth/access_token", new Dictionary<string, string?>
        {
            ["grant_type"] = "fb_exchange_token",
            ["client_id"] = appId,
            ["client_secret"] = appSecret,
            ["fb_exchange_token"] = userToken,
        });
        return SendAsync(HttpMethod.Get, path, "/oauth/access_token (fb_exchange_token)", null, ReadAccessToken, cancellationToken);
    }

    /// <summary>The pages the user of <paramref name="userToken"/> has a task on (at most <see cref="PagesLimit"/>), each
    /// with its page access token and the user's tasks.</summary>
    public Task<FacebookGraphResult<IReadOnlyList<FacebookPage>>> PagesAsync(string userToken, CancellationToken cancellationToken)
    {
        var path = QueryHelpers.AddQueryString("me/accounts", WithToken(userToken, new Dictionary<string, string?>
        {
            ["fields"] = "id,name,access_token,tasks",
            ["limit"] = PagesLimit.ToString(CultureInfo.InvariantCulture),
        }));
        return SendAsync(HttpMethod.Get, path, "/me/accounts", null, ReadPages, cancellationToken);
    }

    /// <summary>Posts <paramref name="imageUrl"/> with <paramref name="caption"/> on the page: the page post's id
    /// (<c>post_id</c>; the photo's <c>id</c> when Graph gives no <c>post_id</c>).</summary>
    public Task<FacebookGraphResult<string>> PostPhotoAsync(
        string pageId, string pageToken, string imageUrl, string caption, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(imageUrl);
        ArgumentException.ThrowIfNullOrEmpty(caption);
        return SendAsync(
            HttpMethod.Post,
            Segment(pageId) + "/photos",
            "/{page-id}/photos",
            Form(WithToken(pageToken, new Dictionary<string, string?> { ["url"] = imageUrl, ["caption"] = caption })),
            root => String(root, "post_id") ?? String(root, "id"),
            cancellationToken);
    }

    /// <summary>Posts <paramref name="message"/> on the page: the post's id.</summary>
    public Task<FacebookGraphResult<string>> PostTextAsync(string pageId, string pageToken, string message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        return SendAsync(
            HttpMethod.Post,
            Segment(pageId) + "/feed",
            "/{page-id}/feed",
            Form(WithToken(pageToken, new Dictionary<string, string?> { ["message"] = message })),
            root => String(root, "id"),
            cancellationToken);
    }

    /// <summary>Deletes the post <paramref name="postId"/> (Graph answers <c>{"success": true}</c>).</summary>
    public async Task<FacebookGraphResult> DeleteAsync(string postId, string pageToken, CancellationToken cancellationToken)
    {
        var path = QueryHelpers.AddQueryString(Segment(postId), WithToken(pageToken, []));
        return await SendAsync(
            HttpMethod.Delete,
            path,
            "/{post-id}",
            null,
            root => root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True ? "deleted" : null,
            cancellationToken);
    }

    private (string AppId, string AppSecret) App() =>
        _options.IsConfigured
            ? (_options.AppId!.Trim(), _options.AppSecret!.Trim())
            : throw new InvalidOperationException("Coordinator:Facebook is not configured.");

    /// <summary><paramref name="parameters"/> with <c>access_token</c> and its <c>appsecret_proof</c>.</summary>
    private Dictionary<string, string?> WithToken(string accessToken, Dictionary<string, string?> parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        parameters["access_token"] = accessToken;
        parameters["appsecret_proof"] = AppSecretProof(App().AppSecret, accessToken);
        return parameters;
    }

    private static string Segment(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Uri.EscapeDataString(id);
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string?> fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value ?? string.Empty)));

    private async Task<FacebookGraphResult<T>> SendAsync<T>(
        HttpMethod method, string path, string endpoint, HttpContent? body, Func<JsonElement, T?> read, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(method, path) { Content = body };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Facebook Graph {Method} {Endpoint} timed out after {TimeoutSeconds} s.", method.Method, endpoint, _http.Timeout.TotalSeconds);
            return new FacebookGraphResult<T>(FacebookGraphOutcome.TimedOut, null, null, null);
        }
        catch (HttpRequestException exception)
        {
            // The exception's message may name the address; only its kind is logged.
            _logger.LogWarning("Facebook Graph {Method} {Endpoint} failed: {Error}.", method.Method, endpoint, exception.HttpRequestError);
            return new FacebookGraphResult<T>(FacebookGraphOutcome.NetworkError, null, null, null);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Facebook Graph {Method} {Endpoint} answered {StatusCode} but its body timed out.", method.Method, endpoint, status);
                return new FacebookGraphResult<T>(FacebookGraphOutcome.TimedOut, status, null, null);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning("Facebook Graph {Method} {Endpoint} answered {StatusCode} but its body failed: {Error}.", method.Method, endpoint, status, exception.HttpRequestError);
                return new FacebookGraphResult<T>(FacebookGraphOutcome.NetworkError, status, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = ErrorOf(text);
                _logger.LogWarning(
                    "Facebook Graph {Method} {Endpoint} answered {StatusCode} (code {Code}, subcode {Subcode}, type {Type}, fbtrace_id {FbTraceId}).",
                    method.Method, endpoint, status, error?.Code, error?.Subcode, error?.Type, error?.FbTraceId);
                return new FacebookGraphResult<T>(error is null ? FacebookGraphOutcome.HttpError : FacebookGraphOutcome.GraphError, status, error, null);
            }

            T? value = null;
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    value = read(document.RootElement);
                }
            }
            catch (JsonException)
            {
            }

            if (value is null)
            {
                _logger.LogWarning("Facebook Graph {Method} {Endpoint} answered {StatusCode} with a body this client cannot read.", method.Method, endpoint, status);
                return new FacebookGraphResult<T>(FacebookGraphOutcome.HttpError, status, null, null);
            }

            _logger.LogInformation("Facebook Graph {Method} {Endpoint} answered {StatusCode}.", method.Method, endpoint, status);
            return new FacebookGraphResult<T>(FacebookGraphOutcome.Succeeded, status, null, value);
        }
    }

    private static string? ReadAccessToken(JsonElement root) => String(root, "access_token") is { Length: > 0 } token ? token : null;

    private static IReadOnlyList<FacebookPage>? ReadPages(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var pages = new List<FacebookPage>();
        foreach (var page in data.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object || String(page, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            var tasks = page.TryGetProperty("tasks", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(task => task.ValueKind == JsonValueKind.String).Select(task => task.GetString()!).ToList()
                : [];
            pages.Add(new FacebookPage(id, String(page, "name") ?? string.Empty, String(page, "access_token") ?? string.Empty, tasks));
        }

        return pages;
    }

    /// <summary>Graph's <c>{"error": {...}}</c> without its message, or <see langword="null"/>.</summary>
    private static FacebookGraphError? ErrorOf(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && Integer(error, "code") is { } code)
            {
                return new FacebookGraphError(code, Integer(error, "error_subcode"), Short(String(error, "type")), Short(String(error, "fbtrace_id")));
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A short identifier worth logging (<c>OAuthException</c>, an fbtrace id), else <see langword="null"/>.</summary>
    private static string? Short(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or '+' or '=')
            ? value
            : null;
}

/// <summary>Whether a Graph error means the credential, a permission, a limit or Graph itself (#379).</summary>
public static class FacebookGraphErrors
{
    /// <summary>The token is expired, revoked or invalid, or its user lost the page (190, 102 and the authentication
    /// subcodes 458–467, 492): the owner must authorize again.</summary>
    public static bool IsTokenRefused(FacebookGraphError? error) =>
        error is { Code: 190 or 102 } || error?.Subcode is 458 or 459 or 460 or 463 or 464 or 467 or 492;

    /// <summary>A permission is missing or was removed (10, 200–299): App Review not passed, or not granted.</summary>
    public static bool IsPermissionMissing(FacebookGraphError? error) => error is { Code: 10 or (>= 200 and <= 299) };

    /// <summary>Throttled: the app (4, 341), the user (17), Pages (32 with a user token, 80001 with a page token) or a
    /// custom limit (613).</summary>
    public static bool IsRateLimited(FacebookGraphResult result) =>
        result.Error is { Code: 4 or 17 or 32 or 341 or 613 or 80001 }
        || (result.Error is null && result.StatusCode == (int)HttpStatusCode.TooManyRequests);

    /// <summary>Graph is down or did not answer: temporary (1, 2), a server error, a timeout or no connection.</summary>
    public static bool IsUnavailable(FacebookGraphResult result) =>
        result.Outcome is FacebookGraphOutcome.TimedOut or FacebookGraphOutcome.NetworkError
        || result.Error is { Code: 1 or 2 }
        || (result.Error is null && result.StatusCode >= 500);

    /// <summary>Graph says the object does not exist (code 100, subcode 33): on a delete, it is gone already.</summary>
    public static bool IsMissingObject(FacebookGraphError? error) => error is { Code: 100, Subcode: 33 };
}
