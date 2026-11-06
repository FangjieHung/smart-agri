using System.Text.Json;
using System.Text.Json.Serialization;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The Google 商家 authorization (store-coordinator design §3 Q12, §5 Slice 7, #378) behind the authorization links of
/// #377: the consent screen asks for <see cref="CoordinatorGoogleClient.Scope"/> with offline access, the consent prompt
/// and PKCE; the code becomes a refresh token — the connection's secret (access tokens are fetched for each use and
/// never stored) — and the owner's locations are listed (every account's, each location once): exactly one connects,
/// several are offered on the selection form (「{名稱}（{地址}）」), none is the 「沒有可管理的商家」 failure. The
/// connection's external id is the location's resource name (<c>locations/{id}</c>), its display name the title.
/// </summary>
/// <remarks>
/// Failure codes (the page explains the first three, <see cref="CoordinatorAuthorizationPages"/>): <c>no-locations</c>,
/// <c>google-access-pending</c> (the platform's Basic API Access is not approved yet: quota 0, or the API not enabled),
/// <c>scope-not-granted</c> (the owner unticked 「管理商家檔案」), <c>no-refresh-token</c>, <c>invalid-grant</c>,
/// <c>exchange-failed</c>, <c>google-unavailable</c>. Configured exactly when <see cref="CoordinatorGoogleOptions"/> is.
/// </remarks>
public sealed class CoordinatorGoogleOAuthProvider : ICoordinatorOAuthProvider
{
    public const string NoLocations = "no-locations";
    public const string ScopeNotGranted = "scope-not-granted";
    public const string NoRefreshToken = "no-refresh-token";
    public const string InvalidGrant = "invalid-grant";
    public const string ExchangeFailed = "exchange-failed";

    private static readonly JsonSerializerOptions PendingJson = new(JsonSerializerDefaults.Web);

    private readonly CoordinatorGoogleClient _google;
    private readonly ILogger<CoordinatorGoogleOAuthProvider> _logger;

    public CoordinatorGoogleOAuthProvider(CoordinatorGoogleClient google, ILogger<CoordinatorGoogleOAuthProvider> logger)
    {
        _google = google;
        _logger = logger;
    }

    public CoordinatorService Service => CoordinatorService.Google;

    public bool IsConfigured => _google.IsConfigured;

    public bool UsesPkce => true;

    public Uri ConsentUrl(CoordinatorOAuthConsent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        return _google.ConsentUrl(
            consent.State,
            consent.RedirectUri,
            consent.CodeChallenge ?? throw new ArgumentException("Google's consent uses PKCE.", nameof(consent)));
    }

    public async Task<CoordinatorOAuthOutcome> ExchangeAsync(CoordinatorOAuthCodeExchange exchange, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (exchange.CodeVerifier is not { Length: > 0 } verifier)
        {
            return CoordinatorOAuthOutcome.Failed(ExchangeFailed);
        }

        var exchanged = await _google.ExchangeCodeAsync(exchange.Code, exchange.RedirectUri, verifier, cancellationToken);
        if (!exchanged.Succeeded)
        {
            return CoordinatorOAuthOutcome.Failed(exchanged.Outcome switch
            {
                GoogleApiOutcome.InvalidGrant => InvalidGrant,
                GoogleApiOutcome.ServerError or GoogleApiOutcome.TimedOut or GoogleApiOutcome.NetworkError => CoordinatorFailureCodes.GoogleUnavailable,
                _ => ExchangeFailed,
            });
        }

        var tokens = exchanged.Value!;
        if (!Granted(tokens.Scope))
        {
            // Google's granular consent lets the owner untick the one permission asked for.
            _logger.LogWarning("The Google owner did not grant {Scope}; nothing is stored.", CoordinatorGoogleClient.Scope);
            return CoordinatorOAuthOutcome.Failed(ScopeNotGranted);
        }

        if (tokens.RefreshToken is not { Length: > 0 } refreshToken)
        {
            _logger.LogWarning("Google sent no refresh token (offline access was asked for with the consent prompt); nothing is stored.");
            return CoordinatorOAuthOutcome.Failed(NoRefreshToken);
        }

        var listed = await LocationsAsync(tokens.AccessToken, cancellationToken);
        if (listed.FailureCode is { } failure)
        {
            return CoordinatorOAuthOutcome.Failed(failure);
        }

        var locations = listed.Locations;
        switch (locations.Count)
        {
            case 0:
                _logger.LogInformation("The Google owner manages no location; nothing is stored.");
                return CoordinatorOAuthOutcome.Failed(NoLocations);

            case 1:
                return CoordinatorOAuthOutcome.Connected(new CoordinatorOAuthCredential(refreshToken, locations[0].Name, locations[0].Title));

            default:
                var offered = locations.Take(CoordinatorOAuthOutcome.MaxChoices).ToList();
                if (locations.Count > offered.Count)
                {
                    _logger.LogWarning(
                        "The Google owner manages {Count} locations; the first {Offered} are offered.", locations.Count, offered.Count);
                }

                var pending = new Pending(refreshToken, offered.ToDictionary(location => location.Name, location => location.Title, StringComparer.Ordinal));
                return CoordinatorOAuthOutcome.NeedsSelection(
                    [.. offered.Select(location => new CoordinatorOAuthChoice(location.Name, Label(location)))],
                    new CoordinatorOAuthPending(JsonSerializer.Serialize(pending, PendingJson)));
        }
    }

    public Task<CoordinatorOAuthOutcome> SelectAsync(CoordinatorOAuthPending pending, string choiceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        Pending? data;
        try
        {
            data = JsonSerializer.Deserialize<Pending>(pending.Data, PendingJson);
        }
        catch (JsonException)
        {
            data = null;
        }

        if (data is not { RefreshToken.Length: > 0, Titles: not null } || !data.Titles.TryGetValue(choiceId, out var title))
        {
            return Task.FromResult(CoordinatorOAuthOutcome.Failed(ExchangeFailed));
        }

        return Task.FromResult(CoordinatorOAuthOutcome.Connected(new CoordinatorOAuthCredential(data.RefreshToken, choiceId, title)));
    }

    /// <summary>「{名稱}（{地址}）」, or the name alone (a service-area business has no storefront address).</summary>
    public static string Label(GoogleLocationSummary location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var title = string.IsNullOrWhiteSpace(location.Title) ? location.Name : location.Title.Trim();
        return location.Address is { Length: > 0 } address ? $"{title}（{address}）" : title;
    }

    private static bool Granted(string? scope) =>
        scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(CoordinatorGoogleClient.Scope, StringComparer.Ordinal) == true;

    /// <summary>Every location of every account the owner manages, each once, in the order Google lists them; a
    /// location name that cannot be an external id is skipped.</summary>
    private async Task<(IReadOnlyList<GoogleLocationSummary> Locations, string? FailureCode)> LocationsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var accounts = await _google.ListAccountsAsync(accessToken, cancellationToken);
        if (!accounts.Succeeded)
        {
            return ([], FailureOf(accounts.Outcome));
        }

        var locations = new List<GoogleLocationSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts.Value!)
        {
            var listed = await _google.ListLocationsAsync(accessToken, account.Name, cancellationToken);
            if (!listed.Succeeded)
            {
                return ([], FailureOf(listed.Outcome));
            }

            foreach (var location in listed.Value!)
            {
                if (!CoordinatorConnection.IsValidExternalId(location.Name))
                {
                    _logger.LogWarning("A Google location name of {Length} characters cannot be stored; it is not offered.", location.Name.Length);
                }
                else if (seen.Add(location.Name))
                {
                    locations.Add(location);
                }
            }
        }

        return (locations, null);
    }

    private static string FailureOf(GoogleApiOutcome outcome) => outcome switch
    {
        GoogleApiOutcome.AccessPending => CoordinatorFailureCodes.GoogleAccessPending,
        GoogleApiOutcome.ServerError or GoogleApiOutcome.TimedOut or GoogleApiOutcome.NetworkError or GoogleApiOutcome.RateLimited
            => CoordinatorFailureCodes.GoogleUnavailable,
        _ => ExchangeFailed,
    };

    /// <summary>What finishes a selection: the refresh token and each offered location's title (kept in memory only).</summary>
    private sealed record Pending(
        [property: JsonPropertyName("r")] string RefreshToken,
        [property: JsonPropertyName("t")] Dictionary<string, string> Titles)
    {
        public override string ToString() => nameof(Pending);
    }
}
