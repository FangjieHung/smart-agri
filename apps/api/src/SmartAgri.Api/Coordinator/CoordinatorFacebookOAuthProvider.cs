using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The FB 粉專 authorization (store-coordinator design §3 Q12, §5 Slice 8, #379) behind the authorization links of #377:
/// Facebook Login (for Business) without PKCE. The consent screen asks for <see cref="Scopes"/> — or, with
/// <see cref="CoordinatorFacebookOptions.ConfigId"/> set, names that Facebook Login for Business configuration instead.
/// The callback's code becomes a user token, then a long-lived one, whose <c>/me/accounts</c> lists the owner's pages;
/// only pages the owner can post on (<see cref="FacebookPage.CreateContentTask"/>) count. Exactly one is connected at
/// once; several go to the selection form; none fails with <see cref="NoPagesCode"/> (「這個 Facebook 帳號沒有可以發文的粉專」).
/// The connection's secret is the chosen page's access token (from a long-lived user token: it does not expire, but a
/// password change, removing the app or losing the page role invalidates it — #377's re-authorization), its external id
/// the page id, its display name the page name.
/// </summary>
/// <remarks>
/// The user tokens are never stored: only the pages' tokens, for the selection form, in <see cref="CoordinatorOAuthPending"/>
/// (the server's memory, 30 minutes). Failure codes: <see cref="NoPagesCode"/>, <see cref="PermissionCode"/> (the
/// owner did not grant the pages permissions), <see cref="UnavailableCode"/> (Graph down, timed out, unreachable) and
/// <see cref="ExchangeCode"/> (Graph refused the code or a token).
/// </remarks>
public sealed class CoordinatorFacebookOAuthProvider : ICoordinatorOAuthProvider
{
    /// <summary>What a post on a page needs: list the pages, publish, and read the page (Graph's photos reference).</summary>
    public const string Scopes = "pages_show_list,pages_manage_posts,pages_read_engagement";

    /// <summary>The account has no page it can post on (or the owner left them all unticked).</summary>
    public const string NoPagesCode = "facebook-no-pages";

    public const string PermissionCode = CoordinatorFailureCodes.FacebookPermission;

    public const string UnavailableCode = CoordinatorFailureCodes.FacebookUnavailable;

    public const string ExchangeCode = "facebook-exchange";

    private static readonly JsonSerializerOptions PendingJson = new(JsonSerializerDefaults.Web);

    private readonly FacebookGraphClient _graph;
    private readonly CoordinatorFacebookOptions _options;
    private readonly ILogger<CoordinatorFacebookOAuthProvider> _logger;

    public CoordinatorFacebookOAuthProvider(
        FacebookGraphClient graph, IOptions<CoordinatorFacebookOptions> options, ILogger<CoordinatorFacebookOAuthProvider> logger)
    {
        _graph = graph;
        _options = options.Value;
        _logger = logger;
    }

    public CoordinatorService Service => CoordinatorService.Facebook;

    public bool IsConfigured => _options.IsConfigured;

    public bool UsesPkce => false;

    public Uri ConsentUrl(CoordinatorOAuthConsent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.AppId?.Trim(),
            ["redirect_uri"] = consent.RedirectUri,
            ["state"] = consent.State,
            ["response_type"] = "code",
        };
        if (_options.ResolvedConfigId is { } configId)
        {
            query["config_id"] = configId;
        }
        else
        {
            query["scope"] = Scopes;
        }

        return new Uri(QueryHelpers.AddQueryString(FacebookGraphClient.DialogUrl, query));
    }

    public async Task<CoordinatorOAuthOutcome> ExchangeAsync(CoordinatorOAuthCodeExchange exchange, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        var user = await _graph.ExchangeCodeAsync(exchange.Code, exchange.RedirectUri, cancellationToken);
        if (!user.Succeeded)
        {
            return Failed("code exchange", user);
        }

        var longLived = await _graph.ExchangeForLongLivedAsync(user.Value!, cancellationToken);
        if (!longLived.Succeeded)
        {
            return Failed("long-lived token", longLived);
        }

        var listed = await _graph.PagesAsync(longLived.Value!, cancellationToken);
        if (!listed.Succeeded)
        {
            return Failed("pages", listed);
        }

        var pages = listed.Value!
            .Where(page => page.CanCreateContent && page.AccessToken.Length > 0 && CoordinatorConnection.IsValidExternalId(page.Id))
            .DistinctBy(page => page.Id, StringComparer.Ordinal)
            .Take(CoordinatorOAuthOutcome.MaxChoices)
            .ToList();
        _logger.LogInformation(
            "The FB account has {Listed} pages, {Postable} of them postable (task {Task}).",
            listed.Value!.Count, pages.Count, FacebookPage.CreateContentTask);
        return pages.Count switch
        {
            0 => CoordinatorOAuthOutcome.Failed(NoPagesCode),
            1 => CoordinatorOAuthOutcome.Connected(Credential(pages[0].Id, pages[0].Name, pages[0].AccessToken)),
            _ => CoordinatorOAuthOutcome.NeedsSelection(
                [.. pages.Select(page => new CoordinatorOAuthChoice(page.Id, page.Name))],
                new CoordinatorOAuthPending(JsonSerializer.Serialize(
                    pages.Select(page => new PendingPage(page.Id, page.Name, page.AccessToken)).ToList(), PendingJson))),
        };
    }

    public Task<CoordinatorOAuthOutcome> SelectAsync(CoordinatorOAuthPending pending, string choiceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        List<PendingPage>? pages = null;
        try
        {
            pages = JsonSerializer.Deserialize<List<PendingPage>>(pending.Data, PendingJson);
        }
        catch (JsonException)
        {
        }

        var page = pages?.FirstOrDefault(candidate => string.Equals(candidate.Id, choiceId, StringComparison.Ordinal));
        return Task.FromResult(page is { Token.Length: > 0 }
            ? CoordinatorOAuthOutcome.Connected(Credential(page.Id, page.Name, page.Token))
            : CoordinatorOAuthOutcome.Failed(ExchangeCode));
    }

    private static CoordinatorOAuthCredential Credential(string pageId, string name, string pageToken) =>
        new(pageToken, pageId, string.IsNullOrWhiteSpace(name) ? pageId : name);

    private CoordinatorOAuthOutcome Failed(string step, FacebookGraphResult result)
    {
        var code = FacebookGraphErrors.IsUnavailable(result) || FacebookGraphErrors.IsRateLimited(result)
            ? UnavailableCode
            : FacebookGraphErrors.IsPermissionMissing(result.Error) ? PermissionCode : ExchangeCode;
        _logger.LogWarning("The FB authorization failed at the {Step}: {Result} → {FailureCode}.", step, result, code);
        return CoordinatorOAuthOutcome.Failed(code);
    }

    /// <summary>A postable page waiting for the owner's choice (the server's memory only).</summary>
    private sealed record PendingPage(
        [property: JsonPropertyName("i")] string Id,
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("t")] string Token)
    {
        public override string ToString() => $"{nameof(PendingPage)} {{ Id = {Id} }}";
    }
}
