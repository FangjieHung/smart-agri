using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace SmartAgri.Api.Coordinator;

/// <summary>How a call to Google went (#378).</summary>
public enum GoogleApiOutcome
{
    Succeeded,

    /// <summary>The token endpoint refused the code or the refresh token (<c>invalid_grant</c>): expired, revoked, the
    /// password changed, or the authorization removed.</summary>
    InvalidGrant,

    /// <summary>The platform's OAuth client was refused (<c>invalid_client</c>, <c>unauthorized_client</c>).</summary>
    ClientRejected,

    /// <summary><c>401</c>: the access token was refused.</summary>
    Unauthorized,

    /// <summary><c>403</c> other than <see cref="AccessPending"/>: no permission on the resource, or a scope missing.</summary>
    Forbidden,

    /// <summary><c>404</c>.</summary>
    NotFound,

    /// <summary>The platform's project may not call the Business Profile APIs yet: a quota of 0 (Basic API Access not
    /// approved) or the API not enabled (<c>SERVICE_DISABLED</c>).</summary>
    AccessPending,

    /// <summary><c>429</c> with a quota above 0: too many calls for now.</summary>
    RateLimited,

    /// <summary>Another <c>4xx</c> (e.g. <c>400 INVALID_ARGUMENT</c>).</summary>
    Rejected,

    /// <summary><c>5xx</c>.</summary>
    ServerError,

    TimedOut,

    NetworkError,
}

/// <summary>A call's outcome, its HTTP status, Google's error status and reason (<c>ErrorInfo.reason</c>) — never its
/// message — and the value when it succeeded.</summary>
public sealed record GoogleApiResult<T>(GoogleApiOutcome Outcome, int? StatusCode, string? ErrorStatus, string? ErrorReason, T? Value)
    where T : class
{
    public bool Succeeded => Outcome == GoogleApiOutcome.Succeeded && Value is not null;

    /// <summary>The same failure, for another value type.</summary>
    public GoogleApiResult<TOther> As<TOther>()
        where TOther : class => new(Outcome, StatusCode, ErrorStatus, ErrorReason, null);

    /// <summary>No value (it may hold a token).</summary>
    public override string ToString() =>
        $"{nameof(GoogleApiResult<T>)} {{ Outcome = {Outcome}, StatusCode = {StatusCode}, ErrorStatus = {ErrorStatus}, ErrorReason = {ErrorReason} }}";
}

/// <summary>Tokens from Google's token endpoint. <see cref="ToString"/> shows none of them.</summary>
/// <param name="RefreshToken">Only when exchanging a code with <c>access_type=offline</c> (and, reliably, with
/// <c>prompt=consent</c>).</param>
/// <param name="Scope">The scopes granted, space-separated (the owner may grant fewer than asked).</param>
public sealed record GoogleTokens(string AccessToken, string? RefreshToken, string? Scope)
{
    public override string ToString() => $"{nameof(GoogleTokens)} {{ Scope = {Scope}, RefreshToken = {(RefreshToken is null ? "none" : "set")} }}";
}

/// <summary>A Business Profile account (<c>accounts/{id}</c>).</summary>
public sealed record GoogleAccount(string Name, string? AccountName, string? Type);

/// <summary>A location as listed (<c>locations/{id}</c>, its title and a short address).</summary>
public sealed record GoogleLocationSummary(string Name, string Title, string? Address);

/// <summary>
/// The few Google endpoints the 統籌助理 uses (store-coordinator design §5 Slice 7, #378), hand-written over a named
/// <see cref="HttpClient"/> (<see cref="HttpClientName"/>: <see cref="Timeout"/>, none of <c>IHttpClientFactory</c>'s request
/// loggers): the OAuth 2.0 token endpoint (code exchange with PKCE, refresh), Account Management <c>accounts.list</c>,
/// Business Information <c>accounts.locations.list</c>, <c>locations.get</c> and <c>locations.patch</c>. Reads
/// <see cref="CoordinatorGoogleOptions"/> for the platform's OAuth client.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is logged:</b> one line per call — the endpoint's name, the HTTP status, and Google's error status and
/// <c>ErrorInfo.reason</c> (or the token endpoint's <c>error</c> code). Never a token, a code, the client secret, a
/// request or response body, or Google's error message.
/// </para>
/// <para>
/// <b>Errors:</b> every expected failure is a <see cref="GoogleApiResult{T}"/>; only the caller's own cancellation throws.
/// A <c>429</c> whose <c>ErrorInfo.metadata.quota_limit_value</c> is <c>"0"</c> and a <c>403 SERVICE_DISABLED</c> are
/// <see cref="GoogleApiOutcome.AccessPending"/>: Google's documented sign of a project whose Basic API Access is not
/// approved is a quota of 0, and a call then exceeds it at once.
/// </para>
/// </remarks>
public sealed class CoordinatorGoogleClient
{
    /// <summary>The named <see cref="HttpClient"/> this client is built on.</summary>
    public const string HttpClientName = "coordinator-google";

    /// <summary>How long a call may take before it is <see cref="GoogleApiOutcome.TimedOut"/>.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string AccountManagementBase = "https://mybusinessaccountmanagement.googleapis.com/v1/";
    public const string BusinessInformationBase = "https://mybusinessbusinessinformation.googleapis.com/v1/";

    /// <summary>Asks the Business Profile APIs for their detailed error format (basic setup guide).</summary>
    public const string ErrorFormatHeader = "X-GOOG-API-FORMAT-VERSION";

    /// <summary>The one scope asked for: manage the owner's Business Profile.</summary>
    public const string Scope = "https://www.googleapis.com/auth/business.manage";

    /// <summary>Pages read at most per list (accounts, or one account's locations).</summary>
    public const int MaxPages = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly CoordinatorGoogleOptions _options;
    private readonly ILogger<CoordinatorGoogleClient> _logger;

    public CoordinatorGoogleClient(HttpClient http, IOptions<CoordinatorGoogleOptions> options, ILogger<CoordinatorGoogleClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The platform's OAuth client is set.</summary>
    public bool IsConfigured => _options.IsConfigured;

    /// <summary>The consent screen: the scope, offline access (a refresh token), the consent prompt every time (Google
    /// sends a refresh token again only then), the state and the PKCE <c>S256</c> challenge.</summary>
    public Uri ConsentUrl(string state, string redirectUri, string codeChallenge) =>
        new(QueryHelpers.AddQueryString(AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = RequireClientId(),
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        }));

    /// <summary>Exchanges an authorization code (with its PKCE verifier) for tokens.</summary>
    public Task<GoogleApiResult<GoogleTokens>> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken) =>
        TokenAsync(
            "token (authorization_code)",
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", redirectUri),
                new("code_verifier", codeVerifier),
            ],
            cancellationToken);

    /// <summary>A new access token from the stored refresh token.</summary>
    public Task<GoogleApiResult<GoogleTokens>> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        TokenAsync("token (refresh_token)", [new("grant_type", "refresh_token"), new("refresh_token", refreshToken)], cancellationToken);

    /// <summary>The accounts the owner can manage (up to <see cref="MaxPages"/> pages).</summary>
    public async Task<GoogleApiResult<IReadOnlyList<GoogleAccount>>> ListAccountsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var accounts = new List<GoogleAccount>();
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = QueryHelpers.AddQueryString(AccountManagementBase + "accounts", Query(("pageSize", "20"), ("pageToken", pageToken)));
            var result = await SendAsync(HttpMethod.Get, url, "accounts.list", accessToken, body: null, cancellationToken);
            if (!result.Succeeded)
            {
                return result.As<IReadOnlyList<GoogleAccount>>();
            }

            using var document = result.Value!.Document;
            var root = document.RootElement;
            if (root.TryGetProperty("accounts", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var account in list.EnumerateArray())
                {
                    if (String(account, "name") is { } name)
                    {
                        accounts.Add(new GoogleAccount(name, String(account, "accountName"), String(account, "type")));
                    }
                }
            }

            if ((pageToken = String(root, "nextPageToken")) is null)
            {
                break;
            }
        }

        return new(GoogleApiOutcome.Succeeded, 200, null, null, accounts);
    }

    /// <summary>The locations of <paramref name="account"/> (<c>accounts/{id}</c>, up to <see cref="MaxPages"/> pages of 100).</summary>
    public async Task<GoogleApiResult<IReadOnlyList<GoogleLocationSummary>>> ListLocationsAsync(
        string accessToken, string account, CancellationToken cancellationToken)
    {
        var locations = new List<GoogleLocationSummary>();
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = QueryHelpers.AddQueryString(
                BusinessInformationBase + account + "/locations",
                Query(("readMask", "name,title,storefrontAddress"), ("pageSize", "100"), ("pageToken", pageToken)));
            var result = await SendAsync(HttpMethod.Get, url, "accounts.locations.list", accessToken, body: null, cancellationToken);
            if (!result.Succeeded)
            {
                return result.As<IReadOnlyList<GoogleLocationSummary>>();
            }

            using var document = result.Value!.Document;
            var root = document.RootElement;
            if (root.TryGetProperty("locations", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var location in list.EnumerateArray())
                {
                    if (String(location, "name") is { } name)
                    {
                        locations.Add(new GoogleLocationSummary(
                            name,
                            String(location, "title") ?? string.Empty,
                            location.TryGetProperty("storefrontAddress", out var address) ? ShortAddress(address) : null));
                    }
                }
            }

            if ((pageToken = String(root, "nextPageToken")) is null)
            {
                break;
            }
        }

        return new(GoogleApiOutcome.Succeeded, 200, null, null, locations);
    }

    /// <summary>A location's <c>regularHours</c> and <c>specialHours</c> (the root object Google answered).</summary>
    public async Task<GoogleApiResult<JsonDocument>> GetHoursAsync(string accessToken, string location, CancellationToken cancellationToken)
    {
        var url = QueryHelpers.AddQueryString(BusinessInformationBase + location, "readMask", "name,regularHours,specialHours");
        var result = await SendAsync(HttpMethod.Get, url, "locations.get", accessToken, body: null, cancellationToken);
        return result.Succeeded ? new(GoogleApiOutcome.Succeeded, result.StatusCode, null, null, result.Value!.Document) : result.As<JsonDocument>();
    }

    /// <summary>Replaces a location's <c>specialHours</c> by the one in <paramref name="body"/>
    /// (<c>updateMask=specialHours</c>).</summary>
    public async Task<GoogleApiResult<JsonDocument>> PatchSpecialHoursAsync(string accessToken, string location, string body, CancellationToken cancellationToken)
    {
        var url = QueryHelpers.AddQueryString(BusinessInformationBase + location, "updateMask", "specialHours");
        var result = await SendAsync(HttpMethod.Patch, url, "locations.patch", accessToken, body, cancellationToken);
        return result.Succeeded ? new(GoogleApiOutcome.Succeeded, result.StatusCode, null, null, result.Value!.Document) : result.As<JsonDocument>();
    }

    /// <summary>「台北市大安區忠孝東路四段 100 號」: the address's area, locality and first line, as one short label.</summary>
    internal static string? ShortAddress(JsonElement address)
    {
        if (address.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var line = address.TryGetProperty("addressLines", out var lines) && lines.ValueKind == JsonValueKind.Array
            ? lines.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            : null;
        var parts = new[] { String(address, "administrativeArea"), String(address, "locality"), line }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        // CJK addresses run from the largest area to the street without spaces; others read street first.
        return String(address, "regionCode") is "TW" or "HK" or "MO" or "CN" or "JP"
            ? string.Concat(parts)
            : string.Join(", ", Enumerable.Reverse(parts));
    }

    private async Task<GoogleApiResult<GoogleTokens>> TokenAsync(string call, List<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        fields.Add(new("client_id", RequireClientId()));
        fields.Add(new("client_secret", _options.ClientSecret ?? throw new InvalidOperationException("Coordinator:Google:ClientSecret is not set.")));
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
        var (response, failure) = await TrySendAsync(request, call, cancellationToken);
        if (response is null)
        {
            return failure!.As<GoogleTokens>();
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var (document, readFailure) = await ReadAsync(response, call, cancellationToken);
            if (readFailure is not null)
            {
                return readFailure.As<GoogleTokens>();
            }

            using (document)
            {
                var root = document?.RootElement;
                if (!response.IsSuccessStatusCode)
                {
                    // RFC 6749 §5.2: { "error": "invalid_grant", "error_description": "…" } — only the code is kept.
                    var error = root is { ValueKind: JsonValueKind.Object } errorRoot ? String(errorRoot, "error") : null;
                    var outcome = error switch
                    {
                        "invalid_grant" => GoogleApiOutcome.InvalidGrant,
                        "invalid_client" or "unauthorized_client" => GoogleApiOutcome.ClientRejected,
                        _ => OutcomeOf(response.StatusCode, null, null),
                    };
                    _logger.LogWarning("Google {Call} answered {StatusCode} ({Error}).", call, status, ShortCode(error) ?? "no error code");
                    return new(outcome, status, null, ShortCode(error), null);
                }

                _logger.LogInformation("Google {Call} answered {StatusCode}.", call, status);
                if (root is not { ValueKind: JsonValueKind.Object } tokens || String(tokens, "access_token") is not { Length: > 0 } accessToken)
                {
                    return new(GoogleApiOutcome.Rejected, status, null, "no-access-token", null);
                }

                return new(GoogleApiOutcome.Succeeded, status, null, null, new GoogleTokens(accessToken, String(tokens, "refresh_token"), String(tokens, "scope")));
            }
        }
    }

    private async Task<GoogleApiResult<JsonBody>> SendAsync(
        HttpMethod method, string url, string call, string accessToken, string? body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Business Profile's detailed errors (ErrorInfo reasons such as SPECIAL_HOURS_SET_WITHOUT_REGULAR_HOURS).
        request.Headers.Add(ErrorFormatHeader, "2");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var (response, failure) = await TrySendAsync(request, call, cancellationToken);
        if (response is null)
        {
            return failure!.As<JsonBody>();
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var (document, readFailure) = await ReadAsync(response, call, cancellationToken);
            if (readFailure is not null)
            {
                return readFailure.As<JsonBody>();
            }

            if (!response.IsSuccessStatusCode)
            {
                using (document)
                {
                    var (errorStatus, reason, quotaLimit) = ErrorOf(document?.RootElement);
                    var outcome = OutcomeOf(response.StatusCode, reason, quotaLimit);
                    _logger.LogWarning(
                        "Google {Call} answered {StatusCode} ({ErrorStatus}, {Reason}{Quota}) → {Outcome}.",
                        call, status, errorStatus ?? "no status", reason ?? "no reason", quotaLimit is null ? string.Empty : $", quota limit {quotaLimit}", outcome);
                    return new(outcome, status, errorStatus, reason, null);
                }
            }

            _logger.LogInformation("Google {Call} answered {StatusCode}.", call, status);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document?.Dispose();
                return new(GoogleApiOutcome.Rejected, status, null, "not-json", null);
            }

            return new(GoogleApiOutcome.Succeeded, status, null, null, new JsonBody(document));
        }
    }

    private async Task<(HttpResponseMessage? Response, GoogleApiResult<JsonBody>? Failure)> TrySendAsync(
        HttpRequestMessage request, string call, CancellationToken cancellationToken)
    {
        try
        {
            return (await _http.SendAsync(request, cancellationToken), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Google {Call} timed out after {TimeoutSeconds} s.", call, _http.Timeout.TotalSeconds);
            return (null, new(GoogleApiOutcome.TimedOut, null, null, null, null));
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning("Google {Call} failed: {Error}.", call, exception.HttpRequestError);
            return (null, new(GoogleApiOutcome.NetworkError, null, null, null, null));
        }
    }

    private async Task<(JsonDocument? Document, GoogleApiResult<JsonBody>? Failure)> ReadAsync(
        HttpResponseMessage response, string call, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Google {Call} timed out while answering.", call);
            return (null, new(GoogleApiOutcome.TimedOut, (int)response.StatusCode, null, null, null));
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning("Google {Call} failed while answering: {Error}.", call, exception.HttpRequestError);
            return (null, new(GoogleApiOutcome.NetworkError, (int)response.StatusCode, null, null, null));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        try
        {
            return (JsonDocument.Parse(text), null);
        }
        catch (JsonException)
        {
            // An HTML error page from a proxy, for instance: judged by its status alone.
            return (null, null);
        }
    }

    /// <summary>Google's error object (AIP-193): its <c>status</c>, the <c>ErrorInfo</c> reason and the quota limit it
    /// names (<c>metadata.quota_limit_value</c>), when present.</summary>
    internal static (string? Status, string? Reason, string? QuotaLimit) ErrorOf(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        string? reason = null;
        string? quotaLimit = null;
        if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind == JsonValueKind.Object && String(detail, "@type") == "type.googleapis.com/google.rpc.ErrorInfo")
                {
                    reason ??= String(detail, "reason");
                    if (detail.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
                    {
                        quotaLimit ??= String(metadata, "quota_limit_value");
                    }
                }
            }
        }

        return (ShortCode(String(error, "status")), ShortCode(reason), ShortCode(quotaLimit));
    }

    internal static GoogleApiOutcome OutcomeOf(HttpStatusCode status, string? reason, string? quotaLimit) => status switch
    {
        HttpStatusCode.Unauthorized => GoogleApiOutcome.Unauthorized,
        HttpStatusCode.Forbidden when reason == "SERVICE_DISABLED" => GoogleApiOutcome.AccessPending,
        HttpStatusCode.Forbidden => GoogleApiOutcome.Forbidden,
        HttpStatusCode.NotFound => GoogleApiOutcome.NotFound,
        HttpStatusCode.TooManyRequests when quotaLimit == "0" => GoogleApiOutcome.AccessPending,
        HttpStatusCode.TooManyRequests => GoogleApiOutcome.RateLimited,
        >= HttpStatusCode.InternalServerError => GoogleApiOutcome.ServerError,
        _ => GoogleApiOutcome.Rejected,
    };

    private string RequireClientId() =>
        _options.ClientId ?? throw new InvalidOperationException("Coordinator:Google:ClientId is not set.");

    private static Dictionary<string, string?> Query(params (string Key, string? Value)[] pairs) =>
        pairs.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>A code worth logging (<c>RATE_LIMIT_EXCEEDED</c>, <c>invalid_grant</c>, <c>0</c>), or <see langword="null"/>:
    /// never free text.</summary>
    private static string? ShortCode(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? value
            : null;

    /// <summary>A JSON object Google answered; the document is disposed by whoever takes it.</summary>
    private sealed class JsonBody(JsonDocument document)
    {
        public JsonDocument Document => document;
    }
}
