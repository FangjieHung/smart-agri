using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using SmartAgri.Api.Coordinator;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// A fake of the Google endpoints the 統籌助理 uses (#378): an <see cref="HttpMessageHandler"/> under
/// <see cref="CoordinatorGoogleClient"/>'s named <see cref="HttpClient"/>, so nothing reaches the network — the OAuth
/// token endpoint (code exchange with PKCE, refresh), Account Management <c>accounts.list</c> and Business Information
/// <c>accounts.locations.list</c>, <c>locations.get</c> and <c>locations.patch</c>. Every test adds its own users and
/// locations (random ids and tokens), so tests sharing the host never see each other's; every request is recorded.
/// </summary>
/// <remarks>
/// <para>
/// It answers like Google's documentation says: the token endpoint form-encoded with this client's id and secret,
/// <c>invalid_grant</c> (<c>400</c>) for an unknown, used or revoked code or refresh token and for a PKCE verifier that does
/// not match; the APIs need a known access token (<c>401 UNAUTHENTICATED</c> otherwise), the user's own account or
/// location (<c>403 PERMISSION_DENIED</c>), a <c>readMask</c> (<c>400</c>) and, to patch, <c>updateMask=specialHours</c>. A
/// patch replaces <c>specialHours</c> by the body's (an absent one clears it) and refuses special hours on a location
/// without regular hours (<c>400 SPECIAL_HOURS_SET_WITHOUT_REGULAR_HOURS</c>). A location's hours are kept as the raw
/// JSON last written, and <c>locations.get</c> answers that very text.
/// </para>
/// <para>
/// <see cref="Script"/> makes the next calls of one endpoint for one subject (a location name, a refresh token, or
/// <see cref="AnySubject"/>) answer a chosen failure: quota 0, a rate limit, <c>503</c>, <c>401</c>, a timeout or a network
/// error.
/// </para>
/// </remarks>
public sealed class FakeGoogleServer : HttpMessageHandler
{
    public const string ClientId = "1234567890-fake.apps.googleusercontent.com";

    public const string Token = "POST /token";
    public const string Accounts = "GET /v1/accounts";
    public const string Locations = "GET /v1/accounts/{id}/locations";
    public const string GetLocation = "GET /v1/locations/{id}";
    public const string PatchLocation = "PATCH /v1/locations/{id}";

    /// <summary>A script for every subject of an endpoint.</summary>
    public const string AnySubject = "*";

    private readonly ConcurrentDictionary<string, Grant> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GoogleUser> _refreshTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GoogleUser> _accessTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FakeLocation> _locations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Endpoint, string Subject), ConcurrentQueue<GoogleBehavior>> _scripts = new();
    private readonly ConcurrentQueue<GoogleRequest> _requests = new();

    /// <summary>The platform's OAuth client secret the host is configured with.</summary>
    public string ClientSecret { get; } = "GOCSPX-fake-" + Guid.NewGuid().ToString("N");

    public IReadOnlyList<GoogleRequest> Requests => [.. _requests];

    /// <summary>Every access token issued (to check none leaks).</summary>
    public IReadOnlyCollection<string> AccessTokens => [.. _accessTokens.Keys];

    /// <summary>A location (<c>locations/{random}</c>) with <paramref name="regularHours"/> and <paramref name="specialHours"/>
    /// as raw JSON (absent when <see langword="null"/>).</summary>
    public FakeLocation AddLocation(string title, string? regularHours = WeekdayHours, string? specialHours = null, string? address = TaipeiAddress)
    {
        var location = new FakeLocation("locations/" + Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999), title, address)
        {
            RegularHours = regularHours,
            SpecialHours = specialHours,
        };
        _locations[location.Name] = location;
        return location;
    }

    /// <summary>A Google user managing <paramref name="accounts"/> (each the locations of one account), with a refresh
    /// token issued on their first code.</summary>
    public GoogleUser AddUser(params FakeLocation[][] accounts) =>
        new(
            "1//fake-refresh-" + Guid.NewGuid().ToString("N"),
            [.. accounts.Select(locations => new FakeAccount("accounts/" + Random.Shared.NextInt64(100_000_000, 999_999_999), [.. locations]))]);

    /// <summary>An authorization code of <paramref name="user"/> for the consent whose PKCE challenge is
    /// <paramref name="challenge"/>, granting <paramref name="scope"/>.</summary>
    public string Code(GoogleUser user, string challenge, string scope = CoordinatorGoogleClient.Scope, bool withRefreshToken = true)
    {
        var code = "4/fake-code-" + Guid.NewGuid().ToString("N");
        _codes[code] = new Grant(user, challenge, scope, withRefreshToken);
        return code;
    }

    /// <summary>The user revoked the platform's access: their refresh token is <c>invalid_grant</c> from now on.</summary>
    public void Revoke(GoogleUser user)
    {
        _refreshTokens.TryRemove(user.RefreshToken, out _);
        foreach (var token in _accessTokens.Where(pair => pair.Value == user).Select(pair => pair.Key).ToList())
        {
            _accessTokens.TryRemove(token, out _);
        }
    }

    /// <summary>The next calls of <paramref name="endpoint"/> for <paramref name="subject"/> answer
    /// <paramref name="behaviors"/>, one each, then as usual again.</summary>
    public void Script(string endpoint, string subject, params GoogleBehavior[] behaviors)
    {
        var queue = _scripts.GetOrAdd((endpoint, subject), _ => new ConcurrentQueue<GoogleBehavior>());
        foreach (var behavior in behaviors)
        {
            queue.Enqueue(behavior);
        }
    }

    /// <summary>The requests to <paramref name="endpoint"/> about <paramref name="location"/>.</summary>
    public IReadOnlyList<GoogleRequest> RequestsFor(string endpoint, FakeLocation location) =>
        [.. _requests.Where(request => request.Endpoint == endpoint && request.Subject == location.Name)];

    /// <summary>A handler instance is disposed by <c>IHttpClientFactory</c> when it rotates handlers; this one keeps
    /// working.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var (endpoint, subject) = EndpointOf(request.Method, uri);
        var query = QueryHelpers.ParseQuery(uri.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        var form = endpoint == Token && body is not null
            ? QueryHelpers.ParseQuery(body).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal)
            : new Dictionary<string, string>();
        if (endpoint == Token && form.GetValueOrDefault("grant_type") == "refresh_token")
        {
            subject = form.GetValueOrDefault("refresh_token");
        }

        var authorization = request.Headers.Authorization is { Scheme: "Bearer" } bearer ? bearer.Parameter : null;
        var formatVersion = request.Headers.TryGetValues(CoordinatorGoogleClient.ErrorFormatHeader, out var values) ? values.FirstOrDefault() : null;
        _requests.Enqueue(new GoogleRequest(request.Method.Method, endpoint, subject, uri, query, form, body, authorization, formatVersion));

        foreach (var key in new[] { subject ?? string.Empty, AnySubject })
        {
            if (_scripts.TryGetValue((endpoint, key), out var script) && script.TryDequeue(out var behavior))
            {
                return behavior.Kind switch
                {
                    // What HttpClient sees when its Timeout elapses: a cancellation the caller did not ask for.
                    GoogleBehaviorKind.TimeOut => throw new TaskCanceledException("The fake Google server timed out."),
                    GoogleBehaviorKind.NetworkError => throw new HttpRequestException(HttpRequestError.ConnectionError, "The fake Google server refused the connection."),
                    _ => Respond(behavior.Status, behavior.Body),
                };
            }
        }

        return endpoint switch
        {
            Token => TokenAnswer(form),
            Accounts => WithUser(authorization, user => Json(new JsonObject
            {
                ["accounts"] = new JsonArray([.. user.Accounts.Select(account => (JsonNode)new JsonObject
                {
                    ["name"] = account.Name,
                    ["accountName"] = "王小明",
                    ["type"] = "PERSONAL",
                    ["verificationState"] = "UNVERIFIED",
                })]),
            })),
            Locations => WithUser(authorization, user => ListLocations(user, subject!, query)),
            GetLocation => WithUser(authorization, user => WithLocation(user, subject!, location => query.ContainsKey("readMask")
                ? Respond(HttpStatusCode.OK, location.Json())
                : InvalidArgument("readMask is required"))),
            PatchLocation => WithUser(authorization, user => WithLocation(user, subject!, location => Patch(location, query, body))),
            _ => Respond(HttpStatusCode.NotFound, Error(404, "NOT_FOUND", "Not found.")),
        };
    }

    // --- Answers --------------------------------------------------------------------------------------

    private HttpResponseMessage TokenAnswer(Dictionary<string, string> form)
    {
        if (form.GetValueOrDefault("client_id") != ClientId || form.GetValueOrDefault("client_secret") != ClientSecret)
        {
            return Respond(HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_description":"The OAuth client was not found."}""");
        }

        switch (form.GetValueOrDefault("grant_type"))
        {
            case "authorization_code":
                if (form.GetValueOrDefault("code") is not { } code
                    || !_codes.TryRemove(code, out var grant)
                    || form.GetValueOrDefault("code_verifier") is not { } verifier
                    || CoordinatorOAuthStates.Challenge(verifier) != grant.Challenge
                    || form.GetValueOrDefault("redirect_uri") is not { Length: > 0 })
                {
                    return InvalidGrant("Malformed auth code.");
                }

                var issued = new JsonObject
                {
                    ["access_token"] = IssueAccessToken(grant.User),
                    ["expires_in"] = 3599,
                    ["scope"] = grant.Scope,
                    ["token_type"] = "Bearer",
                };
                if (grant.WithRefreshToken)
                {
                    _refreshTokens[grant.User.RefreshToken] = grant.User;
                    issued["refresh_token"] = grant.User.RefreshToken;
                }

                return Json(issued);

            case "refresh_token":
                return form.GetValueOrDefault("refresh_token") is { } refresh && _refreshTokens.TryGetValue(refresh, out var user)
                    ? Json(new JsonObject
                    {
                        ["access_token"] = IssueAccessToken(user),
                        ["expires_in"] = 3599,
                        ["scope"] = CoordinatorGoogleClient.Scope,
                        ["token_type"] = "Bearer",
                    })
                    : InvalidGrant("Token has been expired or revoked.");

            default:
                return Respond(HttpStatusCode.BadRequest, """{"error":"unsupported_grant_type","error_description":"Invalid grant_type."}""");
        }
    }

    private HttpResponseMessage ListLocations(GoogleUser user, string account, Dictionary<string, string> query)
    {
        if (user.Accounts.FirstOrDefault(candidate => candidate.Name == account) is not { } owned)
        {
            return Respond(HttpStatusCode.Forbidden, Error(403, "PERMISSION_DENIED", "The caller does not have permission"));
        }

        if (!query.ContainsKey("readMask"))
        {
            return InvalidArgument("readMask is required");
        }

        return Json(new JsonObject
        {
            ["locations"] = new JsonArray([.. owned.Locations.Select(location => (JsonNode)location.Summary())]),
            ["totalSize"] = owned.Locations.Count,
        });
    }

    private static HttpResponseMessage Patch(FakeLocation location, Dictionary<string, string> query, string? body)
    {
        if (query.GetValueOrDefault("updateMask") != "specialHours" || body is null)
        {
            return InvalidArgument("updateMask must be specialHours here");
        }

        using var document = JsonDocument.Parse(body);
        var hasSpecialHours = document.RootElement.TryGetProperty("specialHours", out var specialHours)
            && specialHours.TryGetProperty("specialHourPeriods", out var periods)
            && periods.GetArrayLength() > 0;
        if (hasSpecialHours && location.RegularHours is null)
        {
            return Respond(HttpStatusCode.BadRequest, Error(400, "INVALID_ARGUMENT", "Request contains an invalid argument.", "SPECIAL_HOURS_SET_WITHOUT_REGULAR_HOURS"));
        }

        location.SpecialHours = hasSpecialHours ? specialHours.GetRawText() : null;
        location.Patches.Enqueue(body);
        return Respond(HttpStatusCode.OK, location.Json());
    }

    private HttpResponseMessage WithUser(string? accessToken, Func<GoogleUser, HttpResponseMessage> answer) =>
        accessToken is not null && _accessTokens.TryGetValue(accessToken, out var user)
            ? answer(user)
            : Respond(HttpStatusCode.Unauthorized, Error(401, "UNAUTHENTICATED", "Request had invalid authentication credentials.", "ACCESS_TOKEN_TYPE_UNSUPPORTED"));

    private HttpResponseMessage WithLocation(GoogleUser user, string name, Func<FakeLocation, HttpResponseMessage> answer)
    {
        if (!_locations.TryGetValue(name, out var location))
        {
            return Respond(HttpStatusCode.NotFound, Error(404, "NOT_FOUND", "Requested entity was not found."));
        }

        return user.Accounts.Any(account => account.Locations.Contains(location))
            ? answer(location)
            : Respond(HttpStatusCode.Forbidden, Error(403, "PERMISSION_DENIED", "The caller does not have permission"));
    }

    private string IssueAccessToken(GoogleUser user)
    {
        var token = "ya29.fake-" + Guid.NewGuid().ToString("N");
        _accessTokens[token] = user;
        return token;
    }

    private static (string Endpoint, string? Subject) EndpointOf(HttpMethod method, Uri uri)
    {
        var path = uri.AbsolutePath;
        switch (uri.Host)
        {
            case "oauth2.googleapis.com" when method == HttpMethod.Post && path == "/token":
                return (Token, null);
            case "mybusinessaccountmanagement.googleapis.com" when method == HttpMethod.Get && path == "/v1/accounts":
                return (Accounts, null);
            case "mybusinessbusinessinformation.googleapis.com":
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (method == HttpMethod.Get && segments is ["v1", "accounts", var account, "locations"])
                {
                    return (Locations, $"accounts/{account}");
                }

                if (segments is ["v1", "locations", var id])
                {
                    if (method == HttpMethod.Get)
                    {
                        return (GetLocation, $"locations/{id}");
                    }

                    if (method == HttpMethod.Patch)
                    {
                        return (PatchLocation, $"locations/{id}");
                    }
                }

                break;
        }

        return ($"{method.Method} {uri.GetLeftPart(UriPartial.Path)}", null);
    }

    // --- Documented answers ---------------------------------------------------------------------------

    /// <summary>A project whose Basic API Access is not approved: a quota of 0 requests per minute, exceeded at once.</summary>
    public static GoogleBehavior QuotaZero(string service = "mybusinessbusinessinformation.googleapis.com") =>
        GoogleBehavior.Answer(HttpStatusCode.TooManyRequests, QuotaError(service, "0"));

    /// <summary>Too many calls for now (the default 300 per minute, or the 10 edits a minute of one profile).</summary>
    public static GoogleBehavior RateLimited(string service = "mybusinessbusinessinformation.googleapis.com") =>
        GoogleBehavior.Answer(HttpStatusCode.TooManyRequests, QuotaError(service, "300"));

    public static GoogleBehavior Unavailable() =>
        GoogleBehavior.Answer(HttpStatusCode.ServiceUnavailable, Error(503, "UNAVAILABLE", "The service is currently unavailable."));

    public static GoogleBehavior Unauthenticated() =>
        GoogleBehavior.Answer(HttpStatusCode.Unauthorized, Error(401, "UNAUTHENTICATED", "Request had invalid authentication credentials.", "ACCESS_TOKEN_EXPIRED"));

    private static string QuotaError(string service, string limit) => new JsonObject
    {
        ["error"] = new JsonObject
        {
            ["code"] = 429,
            ["message"] = $"Quota exceeded for quota metric 'Requests' and limit 'Requests per minute' of service '{service}' for consumer 'project_number:123456789012'.",
            ["status"] = "RESOURCE_EXHAUSTED",
            ["details"] = new JsonArray(
                new JsonObject
                {
                    ["@type"] = "type.googleapis.com/google.rpc.ErrorInfo",
                    ["reason"] = "RATE_LIMIT_EXCEEDED",
                    ["domain"] = "googleapis.com",
                    ["metadata"] = new JsonObject
                    {
                        ["consumer"] = "projects/123456789012",
                        ["quota_limit"] = "DefaultRequestsPerMinutePerProject",
                        ["quota_limit_value"] = limit,
                        ["quota_location"] = "global",
                        ["quota_metric"] = $"{service}/default_requests",
                        ["service"] = service,
                    },
                },
                new JsonObject
                {
                    ["@type"] = "type.googleapis.com/google.rpc.Help",
                    ["links"] = new JsonArray(new JsonObject
                    {
                        ["description"] = "Request a higher quota limit.",
                        ["url"] = "https://cloud.google.com/docs/quotas/help/request_increase",
                    }),
                }),
        },
    }.ToJsonString();

    private static string Error(int code, string status, string message, string? reason = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message, ["status"] = status };
        if (reason is not null)
        {
            error["details"] = new JsonArray(new JsonObject
            {
                ["@type"] = "type.googleapis.com/google.rpc.ErrorInfo",
                ["reason"] = reason,
                ["domain"] = reason.StartsWith("ACCESS_TOKEN", StringComparison.Ordinal) ? "googleapis.com" : "mybusinessbusinessinformation.googleapis.com",
            });
        }

        return new JsonObject { ["error"] = error }.ToJsonString();
    }

    private static HttpResponseMessage InvalidGrant(string description) =>
        Respond(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant", ["error_description"] = description }.ToJsonString());

    private static HttpResponseMessage InvalidArgument(string message) =>
        Respond(HttpStatusCode.BadRequest, Error(400, "INVALID_ARGUMENT", message));

    private static HttpResponseMessage Json(JsonObject body) => Respond(HttpStatusCode.OK, body.ToJsonString());

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Mon–Fri 11:00–21:00, as Google lists regular hours.</summary>
    public const string WeekdayHours = """
        {"periods":[{"openDay":"MONDAY","openTime":{"hours":11},"closeDay":"MONDAY","closeTime":{"hours":21}},{"openDay":"TUESDAY","openTime":{"hours":11},"closeDay":"TUESDAY","closeTime":{"hours":21}}]}
        """;

    public const string TaipeiAddress = """
        {"regionCode":"TW","languageCode":"zh-Hant","postalCode":"106","administrativeArea":"台北市","locality":"大安區","addressLines":["忠孝東路四段 100 號"]}
        """;

    private sealed record Grant(GoogleUser User, string Challenge, string Scope, bool WithRefreshToken);
}

/// <summary>A Google user of <see cref="FakeGoogleServer"/>.</summary>
public sealed record GoogleUser(string RefreshToken, IReadOnlyList<FakeAccount> Accounts);

public sealed record FakeAccount(string Name, IReadOnlyList<FakeLocation> Locations);

/// <summary>A location of <see cref="FakeGoogleServer"/>: its hours as the raw JSON last written, and each patch body.</summary>
public sealed class FakeLocation(string name, string title, string? address)
{
    public string Name => name;

    public string Title => title;

    public string? RegularHours { get; set; }

    public string? SpecialHours { get; set; }

    public ConcurrentQueue<string> Patches { get; } = new();

    /// <summary>The <c>specialHourPeriods</c> now, each as its raw JSON.</summary>
    public IReadOnlyList<string> SpecialHourPeriods()
    {
        if (SpecialHours is null)
        {
            return [];
        }

        using var document = JsonDocument.Parse(SpecialHours);
        return document.RootElement.TryGetProperty("specialHourPeriods", out var periods)
            ? [.. periods.EnumerateArray().Select(period => period.GetRawText())]
            : [];
    }

    internal JsonObject Summary()
    {
        var summary = new JsonObject { ["name"] = name, ["title"] = title };
        if (address is not null)
        {
            summary["storefrontAddress"] = JsonNode.Parse(address);
        }

        return summary;
    }

    /// <summary>The location as <c>locations.get</c> answers it: the hours spliced in as raw text.</summary>
    internal string Json()
    {
        var builder = new StringBuilder("{\"name\":").Append(JsonSerializer.Serialize(name));
        if (RegularHours is { } regular)
        {
            builder.Append(",\"regularHours\":").Append(regular);
        }

        if (SpecialHours is { } special)
        {
            builder.Append(",\"specialHours\":").Append(special);
        }

        return builder.Append('}').ToString();
    }
}

public enum GoogleBehaviorKind
{
    Answer,
    TimeOut,
    NetworkError,
}

public sealed record GoogleBehavior(GoogleBehaviorKind Kind, HttpStatusCode Status, string Body)
{
    public static GoogleBehavior Answer(HttpStatusCode status, string body) => new(GoogleBehaviorKind.Answer, status, body);

    public static GoogleBehavior TimeOut() => new(GoogleBehaviorKind.TimeOut, default, string.Empty);

    public static GoogleBehavior NetworkError() => new(GoogleBehaviorKind.NetworkError, default, string.Empty);
}

/// <summary>A request <see cref="FakeGoogleServer"/> received: its endpoint, subject (location, account or refresh
/// token), query, form fields (token endpoint), body, bearer token and error-format header.</summary>
public sealed record GoogleRequest(
    string Method,
    string Endpoint,
    string? Subject,
    Uri Uri,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Form,
    string? Body,
    string? AccessToken,
    string? ErrorFormatVersion);
