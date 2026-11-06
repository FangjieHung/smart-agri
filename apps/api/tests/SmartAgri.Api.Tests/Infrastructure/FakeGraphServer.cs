using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// A fake Meta Graph API <c>v26.0</c> (#379): an <see cref="HttpMessageHandler"/> put under <c>FacebookGraphClient</c>'s
/// <c>HttpClient</c>, so nothing reaches <c>graph.facebook.com</c>. It knows the users added with <see cref="AddUser"/>
/// (a code, a short-lived and a long-lived user token, and their pages) and the pages added with <see cref="AddPage"/>,
/// keyed by token — every test uses its own — and records every request.
/// </summary>
/// <remarks>
/// <para>
/// Like Graph: <c>GET /oauth/access_token</c> exchanges a known code (with this app's id, secret and the redirect URI it
/// was issued for) for the short-lived token, and <c>grant_type=fb_exchange_token</c> a short-lived token for the
/// long-lived one; <c>GET /me/accounts</c> lists the user's pages with their tokens and tasks; <c>POST /{page-id}/photos</c>
/// (<c>url</c>, <c>caption</c>) and <c>POST /{page-id}/feed</c> (<c>message</c>) create a post <c>{page-id}_{n}</c>;
/// <c>DELETE /{post-id}</c> deletes it — one that does not exist (or is another page's) is code 100, subcode 33.
/// </para>
/// <para>
/// Every call with an <c>access_token</c> must carry its <c>appsecret_proof</c> (HMAC-SHA256 with the app secret, hex),
/// else <c>400</c> code 100 — so a success proves the proof right. An unknown token is <c>400</c> code 190.
/// <see cref="Script"/> queues answers for one endpoint of one token (a Graph error, a server error without a body, a
/// timeout, a network error, or a post accepted whose answer never comes), used once each, in order.
/// </para>
/// </remarks>
public sealed class FakeGraphServer : HttpMessageHandler
{
    public const string AccessTokenEndpoint = "GET /oauth/access_token";
    public const string AccountsEndpoint = "GET /me/accounts";
    public const string PhotosEndpoint = "POST /{page-id}/photos";
    public const string FeedEndpoint = "POST /{page-id}/feed";
    public const string DeleteEndpoint = "DELETE /{post-id}";

    /// <summary>The tasks of a page's admin.</summary>
    public static readonly string[] AdminTasks = ["ANALYZE", "ADVERTISE", "MODERATE", "CREATE_CONTENT", "MANAGE"];

    private readonly ConcurrentDictionary<string, GraphUser> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GraphUser> _userTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GraphPage> _pageTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GraphPost> _posts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Token, string Endpoint), ConcurrentQueue<GraphBehavior>> _scripts = new();
    private readonly ConcurrentQueue<GraphRequest> _requests = new();
    private long _ids = 9_000_000;

    public FakeGraphServer(string appId, string appSecret)
    {
        AppId = appId;
        AppSecret = appSecret;
    }

    public string AppId { get; }

    public string AppSecret { get; }

    /// <summary>Every request received, in order.</summary>
    public IReadOnlyList<GraphRequest> Requests => [.. _requests];

    /// <summary>The requests made with <paramref name="accessToken"/> (as <c>access_token</c> or <c>fb_exchange_token</c>).</summary>
    public IReadOnlyList<GraphRequest> RequestsWith(string accessToken) =>
        [.. _requests.Where(request => request.Parameter("access_token") == accessToken || request.Parameter("fb_exchange_token") == accessToken)];

    /// <summary>A page (not yet reachable) with a fresh id and token, and the user's <paramref name="tasks"/> on it.</summary>
    public GraphPage NewPage(string name, params string[] tasks) =>
        new(NextId().ToString(System.Globalization.CultureInfo.InvariantCulture), name, "fake-page-token-" + Guid.NewGuid().ToString("N"), tasks);

    /// <summary>Makes <paramref name="page"/>'s token usable (for posting and deleting).</summary>
    public GraphPage AddPage(GraphPage page)
    {
        _pageTokens[page.AccessToken] = page;
        return page;
    }

    /// <summary>A user who logs in with a fresh code for <paramref name="redirectUri"/> and has <paramref name="pages"/>.</summary>
    public GraphUser AddUser(string redirectUri, params GraphPage[] pages)
    {
        var user = new GraphUser(
            "fake-code-" + Guid.NewGuid().ToString("N"),
            "fake-short-user-token-" + Guid.NewGuid().ToString("N"),
            "fake-long-user-token-" + Guid.NewGuid().ToString("N"),
            redirectUri,
            pages);
        _codes[user.Code] = user;
        _userTokens[user.ShortToken] = user;
        _userTokens[user.LongToken] = user;
        foreach (var page in pages)
        {
            AddPage(page);
        }

        return user;
    }

    /// <summary>The next requests to <paramref name="endpoint"/> with <paramref name="accessToken"/> answer
    /// <paramref name="behaviors"/>, one each, in order.</summary>
    public void Script(string accessToken, string endpoint, params GraphBehavior[] behaviors)
    {
        var queue = _scripts.GetOrAdd((accessToken, endpoint), _ => new ConcurrentQueue<GraphBehavior>());
        foreach (var behavior in behaviors)
        {
            queue.Enqueue(behavior);
        }
    }

    /// <summary>The posts of <paramref name="pageId"/> that exist now.</summary>
    public IReadOnlyList<GraphPost> PostsOf(string pageId) => [.. _posts.Values.Where(post => post.PageId == pageId).OrderBy(post => post.Id)];

    /// <summary>Deletes a post as its page's admin would, by hand.</summary>
    public bool DeleteByHand(string postId) => _posts.TryRemove(postId, out _);

    /// <summary><c>appsecret_proof</c> of <paramref name="accessToken"/>, computed here independently.</summary>
    public string ProofOf(string accessToken) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), Encoding.UTF8.GetBytes(accessToken))).ToLowerInvariant();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var query = QueryHelpers.ParseQuery(uri.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var form = request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded" && body is not null
            ? QueryHelpers.ParseQuery(body).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var path = uri.AbsolutePath;
        var recorded = new GraphRequest(request.Method.Method, path, EndpointOf(request.Method, path), query, form, uri);
        _requests.Enqueue(recorded);

        if (!path.StartsWith("/v26.0/", StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.BadRequest, 2635, null, "Unsupported API version.");
        }

        var token = recorded.Parameter("access_token") ?? recorded.Parameter("fb_exchange_token") ?? recorded.Parameter("code") ?? string.Empty;
        if (_scripts.TryGetValue((token, recorded.Endpoint), out var script) && script.TryDequeue(out var behavior))
        {
            switch (behavior.Kind)
            {
                case GraphBehaviorKind.TimeOut:
                    throw new TaskCanceledException("The fake Graph API timed out.");
                case GraphBehaviorKind.NetworkError:
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "The fake Graph API refused the connection.");
                case GraphBehaviorKind.AcceptThenTimeOut:
                    Answer(recorded);
                    throw new TaskCanceledException("The fake Graph API accepted the post and timed out.");
                default:
                    return Respond(behavior.Status, behavior.Body);
            }
        }

        return Answer(recorded);
    }

    /// <summary>A handler instance is disposed by <c>IHttpClientFactory</c> when it rotates handlers; this one keeps
    /// working (its state is the whole point).</summary>
    protected override void Dispose(bool disposing)
    {
    }

    private HttpResponseMessage Answer(GraphRequest request)
    {
        if (request.Endpoint == AccessTokenEndpoint)
        {
            return AccessToken(request);
        }

        var token = request.Parameter("access_token");
        if (token is null)
        {
            return Error(HttpStatusCode.BadRequest, 104, null, "An access token is required to request this resource.");
        }

        if (request.Parameter("appsecret_proof") != ProofOf(token))
        {
            return Error(HttpStatusCode.BadRequest, 100, null, "Invalid appsecret_proof provided in the API argument", "GraphMethodException");
        }

        var segments = request.Path["/v26.0/".Length..].Split('/');
        switch (request.Endpoint)
        {
            case AccountsEndpoint when _userTokens.TryGetValue(token, out var user):
                return Respond(HttpStatusCode.OK, new JsonObject
                {
                    ["data"] = new JsonArray([.. user.Pages.Select(page => (JsonNode)new JsonObject
                    {
                        ["id"] = page.Id,
                        ["name"] = page.Name,
                        ["access_token"] = page.AccessToken,
                        ["tasks"] = new JsonArray([.. page.Tasks.Select(task => (JsonNode)JsonValue.Create(task))]),
                    })]),
                    ["paging"] = new JsonObject { ["cursors"] = new JsonObject { ["before"] = "QVFIUmJl", ["after"] = "QVFIUmFm" } },
                }.ToJsonString());

            case PhotosEndpoint or FeedEndpoint when _pageTokens.TryGetValue(token, out var page):
                if (page.Id != Uri.UnescapeDataString(segments[0]))
                {
                    return Error(HttpStatusCode.Forbidden, 200, null, "(#200) Permissions error");
                }

                var photo = request.Endpoint == PhotosEndpoint;
                var text = photo ? request.Parameter("caption") : request.Parameter("message");
                if (string.IsNullOrEmpty(text) || (photo && string.IsNullOrEmpty(request.Parameter("url"))))
                {
                    return Error(HttpStatusCode.BadRequest, 100, null, "(#100) Invalid parameter");
                }

                var post = new GraphPost($"{page.Id}_{NextId()}", page.Id, photo ? request.Parameter("url") : null, text!);
                _posts[post.Id] = post;
                return Respond(HttpStatusCode.OK, photo
                    ? new JsonObject { ["id"] = NextId().ToString(System.Globalization.CultureInfo.InvariantCulture), ["post_id"] = post.Id }.ToJsonString()
                    : new JsonObject { ["id"] = post.Id }.ToJsonString());

            case DeleteEndpoint when _pageTokens.TryGetValue(token, out var owner):
                var id = Uri.UnescapeDataString(segments[0]);
                return _posts.TryGetValue(id, out var existing) && existing.PageId == owner.Id && _posts.TryRemove(id, out _)
                    ? Respond(HttpStatusCode.OK, """{"success":true}""")
                    : Error(
                        HttpStatusCode.BadRequest, 100, 33,
                        $"Unsupported delete request. Object with ID '{id}' does not exist, cannot be loaded due to missing permissions, or does not support this operation.",
                        "GraphMethodException");

            default:
                return Error(HttpStatusCode.BadRequest, 190, null, "Invalid OAuth access token - Cannot parse access token");
        }
    }

    private HttpResponseMessage AccessToken(GraphRequest request)
    {
        if (request.Parameter("client_id") != AppId || request.Parameter("client_secret") != AppSecret)
        {
            return Error(HttpStatusCode.BadRequest, 101, null, "Error validating application. Invalid application ID.");
        }

        if (request.Parameter("grant_type") == "fb_exchange_token")
        {
            return request.Parameter("fb_exchange_token") is { } shortToken && _userTokens.TryGetValue(shortToken, out var user)
                ? Respond(HttpStatusCode.OK, new JsonObject { ["access_token"] = user.LongToken, ["token_type"] = "bearer", ["expires_in"] = 5183944 }.ToJsonString())
                : Error(HttpStatusCode.BadRequest, 190, null, "Invalid OAuth access token - Cannot parse access token");
        }

        return request.Parameter("code") is { } code && _codes.TryGetValue(code, out var owner) && request.Parameter("redirect_uri") == owner.RedirectUri
            ? Respond(HttpStatusCode.OK, new JsonObject { ["access_token"] = owner.ShortToken, ["token_type"] = "bearer", ["expires_in"] = 5183 }.ToJsonString())
            : Error(HttpStatusCode.BadRequest, 100, 36007, "Invalid verification code format.");
    }

    private static string EndpointOf(HttpMethod method, string path)
    {
        var rest = path.StartsWith("/v26.0/", StringComparison.Ordinal) ? path["/v26.0/".Length..] : path.TrimStart('/');
        var segments = rest.Split('/');
        return (method.Method, segments) switch
        {
            ("GET", ["oauth", "access_token"]) => AccessTokenEndpoint,
            ("GET", ["me", "accounts"]) => AccountsEndpoint,
            ("POST", [_, "photos"]) => PhotosEndpoint,
            ("POST", [_, "feed"]) => FeedEndpoint,
            ("DELETE", [_]) => DeleteEndpoint,
            _ => $"{method.Method} {path}",
        };
    }

    private long NextId() => Interlocked.Increment(ref _ids);

    private static HttpResponseMessage Error(HttpStatusCode status, int code, int? subcode, string message, string type = "OAuthException") =>
        Respond(status, GraphBehavior.ErrorBody(code, subcode, message, type));

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>A user of the <see cref="FakeGraphServer"/>: the code they come back with and their tokens.</summary>
public sealed record GraphUser(string Code, string ShortToken, string LongToken, string RedirectUri, IReadOnlyList<GraphPage> Pages);

/// <summary>A page of the <see cref="FakeGraphServer"/>, with the user's tasks on it.</summary>
public sealed record GraphPage(string Id, string Name, string AccessToken, IReadOnlyList<string> Tasks);

/// <summary>A post that exists on the <see cref="FakeGraphServer"/>.</summary>
public sealed record GraphPost(string Id, string PageId, string? ImageUrl, string Text);

/// <summary>A request the <see cref="FakeGraphServer"/> received: query and form fields parsed.</summary>
public sealed record GraphRequest(
    string Method, string Path, string Endpoint, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Form, Uri Uri)
{
    /// <summary>A field of the form, else of the query.</summary>
    public string? Parameter(string name) => Form.TryGetValue(name, out var value) ? value : Query.GetValueOrDefault(name);
}

public enum GraphBehaviorKind
{
    Answer,
    TimeOut,
    NetworkError,
    AcceptThenTimeOut,
}

/// <summary>How a scripted Graph request answers.</summary>
public sealed record GraphBehavior(GraphBehaviorKind Kind, HttpStatusCode Status, string Body)
{
    /// <summary>Throws as <c>HttpClient</c> does when its timeout elapses, at once.</summary>
    public static GraphBehavior TimeOut { get; } = new(GraphBehaviorKind.TimeOut, 0, string.Empty);

    public static GraphBehavior NetworkError { get; } = new(GraphBehaviorKind.NetworkError, 0, string.Empty);

    /// <summary>A post Graph accepts whose answer never comes: a timeout.</summary>
    public static GraphBehavior AcceptThenTimeOut { get; } = new(GraphBehaviorKind.AcceptThenTimeOut, 0, string.Empty);

    /// <summary>A <c>502</c> without Graph's error object (a proxy in front of it).</summary>
    public static GraphBehavior BadGateway { get; } = new(GraphBehaviorKind.Answer, HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>");

    /// <summary>Graph's error object with <paramref name="code"/> (and <paramref name="subcode"/>).</summary>
    public static GraphBehavior Error(HttpStatusCode status, int code, int? subcode = null, string message = "(#0) Scripted error") =>
        new(GraphBehaviorKind.Answer, status, ErrorBody(code, subcode, message, "OAuthException"));

    public static string ErrorBody(int code, int? subcode, string message, string type)
    {
        var error = new JsonObject { ["message"] = message, ["type"] = type, ["code"] = code, ["fbtrace_id"] = "AbCdEf" + code };
        if (subcode is { } value)
        {
            error["error_subcode"] = value;
        }

        return new JsonObject { ["error"] = error }.ToJsonString();
    }
}
