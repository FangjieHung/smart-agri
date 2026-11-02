using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Observability;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The owner's side of an FB or Google authorization link (store-coordinator design §3 Q12, §5 Slice 6, #377): three
/// anonymous endpoints outside the OpenAPI document, each answering a small server-rendered page in Traditional Chinese
/// (<see cref="CoordinatorAuthorizationPages"/>) or a redirect, never JSON.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><c>GET /api/v1/coordinator/connect?token={token}</c>: a usable link (exists, unused, unexpired, its service available)
/// sets the nonce cookie and redirects (<c>302</c>) to the service's consent screen with a protected
/// <c>state</c>; anything else is the same 「連結已失效」 page (<c>404</c>), whatever the reason.</item>
/// <item><c>GET /api/v1/coordinator/oauth/{service}/callback</c>: <c>404</c> for a service that is not available; the
/// state must read and its nonce match the cookie, else the generic error page; the service's <c>error</c> is
/// 「已取消授權」 (the link stays usable); otherwise the link is taken (compare-and-set: a second callback gets
/// 「連結已失效」), the code exchanged, and the credential stored (「授權完成」) or the selection form shown.</item>
/// <item><c>POST /api/v1/coordinator/oauth/{service}/select</c>: the selection form, under the same checks; only a choice
/// the provider offered is accepted.</item>
/// </list>
/// Every answer is <c>no-store</c>, <c>Referrer-Policy: no-referrer</c>, <c>noindex</c>, with a CSP that loads nothing.
/// The link's token, the code, the state, the verifier and the credential never appear in a page, a log or a redirect
/// other than to the service; a failure shows at most a short code.
/// </remarks>
public static class CoordinatorAuthorizationEndpoints
{
    public const string CallbackRoute = CoordinatorAuthorizationLinks.OAuthPrefix + "/{service}/callback";
    public const string SelectRoute = CoordinatorAuthorizationLinks.OAuthPrefix + "/{service}/select";

    public static IEndpointRouteBuilder MapCoordinatorAuthorizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(CoordinatorAuthorizationLinks.ConnectPath, OpenAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet(CallbackRoute, CallbackAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(SelectRoute, SelectAsync).AllowAnonymous().ExcludeFromDescription();
        return endpoints;
    }

    private static Task<IResult> OpenAsync(HttpContext context, CoordinatorAuthorizationFlow flow) =>
        flow.OpenAsync(Anonymous(context), context.Request.Query["token"] is { Count: 1 } token ? token[0] : null);

    private static Task<IResult> CallbackAsync(HttpContext context, string service, CoordinatorAuthorizationFlow flow) =>
        flow.CallbackAsync(Anonymous(context), service);

    private static Task<IResult> SelectAsync(HttpContext context, string service, CoordinatorAuthorizationFlow flow) =>
        flow.SelectAsync(Anonymous(context), service);

    /// <summary>Anonymous by design: a sign-in cookie sent along must not decide anything.</summary>
    private static HttpContext Anonymous(HttpContext context)
    {
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        CoordinatorAuthorizationPages.SetHeaders(context.Response);
        return context;
    }
}

/// <summary>
/// The steps of <see cref="CoordinatorAuthorizationEndpoints"/> (#377). Logs carry the link, organization, service and
/// why something was refused — never a token, code, state, nonce, verifier or credential.
/// </summary>
public sealed class CoordinatorAuthorizationFlow
{
    /// <summary>Largest selection form body read (bytes).</summary>
    public const int MaxFormBytes = 16 * 1024;

    private const int MaxCodeLength = 4096;

    private readonly CoordinatorAuthorizationLinks _links;
    private readonly CoordinatorOAuthStates _states;
    private readonly CoordinatorOAuthSelections _selections;
    private readonly CoordinatorConnections _connections;
    private readonly CoordinatorBindings _bindings;
    private readonly ISecretProtector _secrets;
    private readonly TimeProvider _clock;
    private readonly ILogger<CoordinatorAuthorizationFlow> _logger;

    public CoordinatorAuthorizationFlow(
        CoordinatorAuthorizationLinks links,
        CoordinatorOAuthStates states,
        CoordinatorOAuthSelections selections,
        CoordinatorConnections connections,
        CoordinatorBindings bindings,
        ISecretProtector secrets,
        TimeProvider clock,
        ILogger<CoordinatorAuthorizationFlow> logger)
    {
        _links = links;
        _states = states;
        _selections = selections;
        _connections = connections;
        _bindings = bindings;
        _secrets = secrets;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The nonce cookie's name for <paramref name="service"/>.</summary>
    public static string CookieName(CoordinatorService service) => $"coordinator_oauth_{WireNames<CoordinatorService>.ToWire(service)}";

    /// <summary><c>GET /api/v1/coordinator/connect?token={token}</c>.</summary>
    public async Task<IResult> OpenAsync(HttpContext context, string? token)
    {
        var cancellationToken = context.RequestAborted;
        if (_links.PublicBaseUrl is null || !AuthorizationLinkTokens.IsToken(token))
        {
            _logger.LogInformation("An authorization link was refused: {Reason}.", _links.PublicBaseUrl is null ? "no public base URL" : "malformed");
            return CoordinatorAuthorizationPages.InvalidLink();
        }

        var link = await _links.FindAsync(token, cancellationToken);
        var now = _clock.GetUtcNow();
        var provider = link is null ? null : _links.Provider(link.Service);
        if (link is null || !link.IsUsable(now) || provider is null)
        {
            _logger.LogInformation(
                "An authorization link was refused: {Reason}{Link}.",
                link is null ? "unknown" : link.UsedAt is not null ? "used" : now >= link.ExpiresAt ? "expired" : "not configured",
                link is null ? string.Empty : $" (link {link.Id}, organization {link.OrganizationId}, {WireNames<CoordinatorService>.ToWire(link.Service)})");
            return CoordinatorAuthorizationPages.InvalidLink();
        }

        var service = link.Service;
        var nonce = CoordinatorOAuthStates.NewRandom();
        var verifier = provider.UsesPkce ? CoordinatorOAuthStates.NewRandom() : null;
        var state = _states.New(link.Id, link.OrganizationId, service, nonce, verifier);
        Uri consent;
        try
        {
            consent = provider.ConsentUrl(new CoordinatorOAuthConsent(
                _states.Protect(CoordinatorOAuthStateKind.Consent, state),
                _links.RedirectUri(service),
                verifier is null ? null : CoordinatorOAuthStates.Challenge(verifier)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                "The {Service} provider could not build its consent URL for link {LinkId}: {Failure}",
                WireNames<CoordinatorService>.ToWire(service), link.Id, ExceptionSummary.Of(exception));
            return CoordinatorAuthorizationPages.Failed(null);
        }

        if (!consent.IsAbsoluteUri || consent.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogError("The {Service} provider's consent URL is not absolute HTTPS; link {LinkId} not opened.", WireNames<CoordinatorService>.ToWire(service), link.Id);
            return CoordinatorAuthorizationPages.Failed(null);
        }

        context.Response.Cookies.Append(CookieName(service), nonce, NonceCookie(service, CoordinatorOAuthStates.Lifetime));
        _logger.LogInformation(
            "Authorization link {LinkId} of organization {OrganizationId} ({Service}, {Purpose}) opened; sent to the consent screen.",
            link.Id, link.OrganizationId, WireNames<CoordinatorService>.ToWire(service), WireNames<CoordinatorAuthorizationPurpose>.ToWire(link.Purpose));
        return Results.Redirect(consent.AbsoluteUri);
    }

    /// <summary><c>GET /api/v1/coordinator/oauth/{service}/callback</c>.</summary>
    public async Task<IResult> CallbackAsync(HttpContext context, string serviceName)
    {
        if (!TryService(serviceName, out var service) || _links.Provider(service) is not { } provider)
        {
            return Results.NotFound();
        }

        var wire = WireNames<CoordinatorService>.ToWire(service);
        var query = context.Request.Query;
        var state = Single(query["state"]) is { } text ? _states.TryRead(CoordinatorOAuthStateKind.Consent, text, service) : null;
        if (state is null)
        {
            _logger.LogInformation("A {Service} authorization callback was refused: no valid state.", wire);
            return CoordinatorAuthorizationPages.Failed(null);
        }

        if (!NonceMatches(context, service, state.Nonce))
        {
            _logger.LogInformation("A {Service} authorization callback of link {LinkId} was refused: the nonce cookie is missing or wrong.", wire, state.LinkId);
            return CoordinatorAuthorizationPages.Failed(null);
        }

        if (query.ContainsKey("error"))
        {
            var code = ShortCode(Single(query["error"]));
            context.Response.Cookies.Delete(CookieName(service), NonceCookie(service, null));
            _logger.LogInformation(
                "The owner did not authorize {Service} for link {LinkId} (error {Error}); the link stays usable.", wire, state.LinkId, code ?? "unreadable");
            return CoordinatorAuthorizationPages.Cancelled(code);
        }

        if (Single(query["code"]) is not { Length: > 0 and <= MaxCodeLength } authorizationCode)
        {
            _logger.LogInformation("A {Service} authorization callback of link {LinkId} was refused: no code.", wire, state.LinkId);
            return CoordinatorAuthorizationPages.Failed(null);
        }

        try
        {
            if (!await _links.TakeAsync(state.OrganizationId, state.LinkId, service, context.RequestAborted))
            {
                _logger.LogInformation("A {Service} authorization callback of link {LinkId} was refused: the link is used or expired.", wire, state.LinkId);
                return CoordinatorAuthorizationPages.InvalidLink();
            }

            CoordinatorOAuthOutcome outcome;
            try
            {
                outcome = await provider.ExchangeAsync(
                    new CoordinatorOAuthCodeExchange(authorizationCode, _links.RedirectUri(service), state.CodeVerifier), context.RequestAborted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
            {
                _logger.LogError(
                    "The {Service} code exchange of link {LinkId} failed: {Failure}", wire, state.LinkId, ExceptionSummary.Of(exception));
                return CoordinatorAuthorizationPages.Failed("exchange-error");
            }

            switch (outcome.Kind)
            {
                case CoordinatorOAuthOutcomeKind.Connected:
                    return await ConnectAsync(context, state, outcome.Credential!);

                case CoordinatorOAuthOutcomeKind.NeedsSelection:
                    var selection = new CoordinatorOAuthPendingSelection(
                        state.OrganizationId, service, state.Nonce, outcome.Choices, outcome.Pending!, _clock.GetUtcNow() + CoordinatorOAuthStates.Lifetime);
                    _selections.Keep(state.LinkId, selection);
                    var formState = _states.Protect(
                        CoordinatorOAuthStateKind.Selection,
                        _states.New(state.LinkId, state.OrganizationId, service, state.Nonce, codeVerifier: null));
                    _logger.LogInformation(
                        "Link {LinkId} of organization {OrganizationId} ({Service}): the owner chooses among {Count}.",
                        state.LinkId, state.OrganizationId, wire, outcome.Choices.Count);
                    return CoordinatorAuthorizationPages.Selection(service, formState, outcome.Choices);

                default:
                    _logger.LogWarning("The {Service} code exchange of link {LinkId} failed: {FailureCode}.", wire, state.LinkId, outcome.FailureCode);
                    return CoordinatorAuthorizationPages.Failed(outcome.FailureCode);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogError("The {Service} authorization of link {LinkId} failed: {Failure}", wire, state.LinkId, ExceptionSummary.Of(exception));
            return CoordinatorAuthorizationPages.Failed(null);
        }
    }

    /// <summary><c>POST /api/v1/coordinator/oauth/{service}/select</c>.</summary>
    public async Task<IResult> SelectAsync(HttpContext context, string serviceName)
    {
        if (!TryService(serviceName, out var service) || _links.Provider(service) is not { } provider)
        {
            return Results.NotFound();
        }

        var wire = WireNames<CoordinatorService>.ToWire(service);
        var form = await ReadFormAsync(context);
        if (form is null)
        {
            _logger.LogInformation("A {Service} selection was refused: not a small URL-encoded form.", wire);
            return CoordinatorAuthorizationPages.Failed(null);
        }

        var state = Single(form["state"]) is { } text ? _states.TryRead(CoordinatorOAuthStateKind.Selection, text, service) : null;
        if (state is null)
        {
            _logger.LogInformation("A {Service} selection was refused: no valid state.", wire);
            return CoordinatorAuthorizationPages.Failed(null);
        }

        if (!NonceMatches(context, service, state.Nonce))
        {
            _logger.LogInformation("A {Service} selection of link {LinkId} was refused: the nonce cookie is missing or wrong.", wire, state.LinkId);
            return CoordinatorAuthorizationPages.Failed(null);
        }

        var pending = _selections.Find(state.LinkId);
        if (pending is null || pending.OrganizationId != state.OrganizationId || pending.Service != service || !SameSecret(pending.Nonce, state.Nonce))
        {
            _logger.LogInformation("A {Service} selection of link {LinkId} was refused: nothing to choose (taken, expired or restarted).", wire, state.LinkId);
            return CoordinatorAuthorizationPages.Failed(null);
        }

        var choice = Single(form["choice"]);
        if (choice is null || !pending.Choices.Any(offered => string.Equals(offered.Id, choice, StringComparison.Ordinal)))
        {
            _logger.LogWarning("A {Service} selection of link {LinkId} was refused: not one of the {Count} choices offered.", wire, state.LinkId, pending.Choices.Count);
            return CoordinatorAuthorizationPages.Failed(null);
        }

        if (!_selections.TryTake(state.LinkId, pending))
        {
            _logger.LogInformation("A {Service} selection of link {LinkId} was refused: another request took it.", wire, state.LinkId);
            return CoordinatorAuthorizationPages.Failed(null);
        }

        try
        {
            var outcome = await provider.SelectAsync(pending.Pending, choice, context.RequestAborted);
            if (outcome.Kind != CoordinatorOAuthOutcomeKind.Connected)
            {
                _logger.LogWarning(
                    "The {Service} selection of link {LinkId} did not connect: {Outcome}.", wire, state.LinkId, outcome.FailureCode ?? "another selection");
                return CoordinatorAuthorizationPages.Failed(outcome.FailureCode);
            }

            return await ConnectAsync(context, state, outcome.Credential!);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogError("The {Service} selection of link {LinkId} failed: {Failure}", wire, state.LinkId, ExceptionSummary.Of(exception));
            return CoordinatorAuthorizationPages.Failed(null);
        }
    }

    /// <summary>Stores <paramref name="credential"/> (protected) as the shop's active connection, with one
    /// <c>coordinator-connection-updated</c> activity, and says so.</summary>
    private async Task<IResult> ConnectAsync(HttpContext context, CoordinatorOAuthState state, CoordinatorOAuthCredential credential)
    {
        var service = state.Service;
        var secret = _secrets.Protect(CoordinatorConnection.PurposeOf(service), credential.Secret, _clock.GetUtcNow());
        var change = await _connections.SetAsync(
            state.OrganizationId, service, secret, credential.ExternalId, credential.DisplayName, context.RequestAborted);
        context.Response.Cookies.Delete(CookieName(service), NonceCookie(service, null));
        _logger.LogInformation(
            "Organization {OrganizationId} connected {Service} ({Change}) through authorization link {LinkId}.",
            state.OrganizationId, WireNames<CoordinatorService>.ToWire(service), change, state.LinkId);
        var storeName = await _bindings.StoreNameAsync(state.OrganizationId, context.RequestAborted);
        return CoordinatorAuthorizationPages.Done(storeName, service, credential.DisplayName);
    }

    private CookieOptions NonceCookie(CoordinatorService service, TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = _links.CookiePath(service),
        MaxAge = maxAge,
        IsEssential = true,
    };

    /// <summary>The selection form's fields: <c>application/x-www-form-urlencoded</c> with a <c>Content-Length</c> of at
    /// most <see cref="MaxFormBytes"/> and a few fields; otherwise <see langword="null"/>. Read through the request's own
    /// form feature: the authentication middleware may already have read (and kept) it.</summary>
    private static async Task<IFormCollection?> ReadFormAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength is not (> 0 and <= MaxFormBytes)
            || !string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var form = await request.ReadFormAsync(context.RequestAborted);
            return form.Count <= 8 ? form : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static bool NonceMatches(HttpContext context, CoordinatorService service, string nonce) =>
        context.Request.Cookies[CookieName(service)] is { Length: > 0 } cookie && SameSecret(cookie, nonce);

    private static bool SameSecret(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static bool TryService(string? name, out CoordinatorService service)
    {
        service = default;
        foreach (var candidate in new[] { CoordinatorService.Facebook, CoordinatorService.Google })
        {
            if (string.Equals(name, WireNames<CoordinatorService>.ToWire(candidate), StringComparison.Ordinal))
            {
                service = candidate;
                return true;
            }
        }

        return false;
    }

    private static string? Single(StringValues values) => values.Count == 1 ? values[0] : null;

    /// <summary>A service's error as a short code worth showing (e.g. <c>access_denied</c>), or <see langword="null"/>:
    /// never its text.</summary>
    private static string? ShortCode(string? error) =>
        error is { Length: > 0 and <= 32 } && error.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.')
            ? error
            : null;
}

/// <summary>
/// The small pages of an authorization link (#377): one inline style allowed by its hash, nothing loaded, no frames, a
/// form only on the selection page (to this origin). Every value from outside is HTML-encoded.
/// </summary>
public static class CoordinatorAuthorizationPages
{
    public const string InvalidLinkMessage = "連結已失效，請向平台營運者索取新的連結。";

    public const string FailedMessage = "授權沒有完成。請重新打開原本的授權連結；如果仍然不行，請向平台營運者索取新的連結。";

    public const string CancelledMessage = "你取消了授權，統籌助理不會使用這個帳號。要完成授權，請重新打開原本的授權連結。";

    private const string HtmlContentType = "text/html; charset=utf-8";

    private const string Style =
        "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;"
        + "font:16px/1.6 system-ui,sans-serif;color:#292524;background:#fafaf9}"
        + "main{max-width:28rem;padding:24px}h1{font-size:1.25rem;margin:0 0 12px}p{margin:0 0 12px}"
        + "fieldset{border:0;margin:0 0 16px;padding:0}legend{margin-bottom:8px}"
        + "label{display:block;padding:12px;margin-bottom:8px;border:1px solid #d6d3d1;border-radius:8px;background:#fff}"
        + "button{font:inherit;padding:10px 20px;border:0;border-radius:8px;background:#166534;color:#fff}"
        + ".code{color:#78716c;font-size:.875rem}";

    private static readonly string StyleHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Style)));

    /// <summary>Pages without a form.</summary>
    public static readonly string ContentSecurityPolicy =
        $"default-src 'none'; style-src 'sha256-{StyleHash}'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>The selection page: its form posts to this origin only.</summary>
    public static readonly string SelectionContentSecurityPolicy =
        $"default-src 'none'; style-src 'sha256-{StyleHash}'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>The headers of every answer (redirects and <c>404</c>s included).</summary>
    public static void SetHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Robots-Tag"] = "noindex";
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
    }

    /// <summary>The link is unknown, used, expired or its service is not available: one page for all of them.</summary>
    public static IResult InvalidLink() => Page("連結已失效", $"<p>{InvalidLinkMessage}</p>", StatusCodes.Status404NotFound);

    /// <summary>Anything else that went wrong, with a short code of ours when there is one.</summary>
    public static IResult Failed(string? code) =>
        Page("授權沒有完成", $"<p>{FailedMessage}</p>{Code(code)}", StatusCodes.Status400BadRequest);

    /// <summary>The owner said no on the service's screen.</summary>
    public static IResult Cancelled(string? code) => Page("已取消授權", $"<p>{CancelledMessage}</p>{Code(code)}", StatusCodes.Status200OK);

    /// <summary>「授權完成」, naming the shop and the account connected.</summary>
    public static IResult Done(string? storeName, CoordinatorService service, string displayName)
    {
        var shop = string.IsNullOrWhiteSpace(storeName) ? "你的店" : Encode(storeName);
        var account = string.IsNullOrWhiteSpace(displayName) ? string.Empty : $"：{Encode(displayName.Trim())}";
        return Page(
            "授權完成",
            $"<p>{shop} 已連結{CoordinatorAuthorizations.Label(service)}{account}。</p>"
            + "<p>統籌助理之後會用這個帳號執行設為自動的動作。你可以關閉這個頁面了。</p>",
            StatusCodes.Status200OK);
    }

    /// <summary>The form to choose one page or location; <paramref name="state"/> is the protected selection state.</summary>
    public static IResult Selection(CoordinatorService service, string state, IReadOnlyList<CoordinatorOAuthChoice> choices)
    {
        var noun = service == CoordinatorService.Facebook ? "粉專" : "營業地點";
        var body = new StringBuilder()
            .Append($"<p>你的帳號有 {choices.Count} 個{noun}，請選擇要讓統籌助理使用的那一個。</p>")
            .Append("<form method=\"post\" action=\"select\">")
            .Append($"<input type=\"hidden\" name=\"state\" value=\"{Encode(state)}\">")
            .Append($"<fieldset><legend>{CoordinatorAuthorizations.Label(service)}的{noun}</legend>");
        foreach (var choice in choices)
        {
            body.Append($"<label><input type=\"radio\" name=\"choice\" value=\"{Encode(choice.Id)}\" required> {Encode(choice.Label)}</label>");
        }

        body.Append("</fieldset><button type=\"submit\">連結</button></form>");
        return Page($"選擇要連結的{noun}", body.ToString(), StatusCodes.Status200OK, SelectionContentSecurityPolicy);
    }

    private static string Code(string? code) => code is null ? string.Empty : $"<p class=\"code\">（代碼：{Encode(code)}）</p>";

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static IResult Page(string title, string body, int statusCode, string? contentSecurityPolicy = null) =>
        new PageResult(
            "<!doctype html>\n<html lang=\"zh-Hant\">\n<head>\n<meta charset=\"utf-8\">\n"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
            + "<meta name=\"robots\" content=\"noindex\">\n<meta name=\"referrer\" content=\"no-referrer\">\n"
            + $"<title>{title}｜統籌助理授權</title>\n<style>{Style}</style>\n</head>\n<body>\n<main>\n<h1>{title}</h1>\n{body}\n</main>\n</body>\n</html>\n",
            statusCode,
            contentSecurityPolicy);

    private sealed class PageResult(string html, int statusCode, string? contentSecurityPolicy) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (contentSecurityPolicy is not null)
            {
                httpContext.Response.Headers.ContentSecurityPolicy = contentSecurityPolicy;
            }

            return Results.Text(html, HtmlContentType, Encoding.UTF8, statusCode).ExecuteAsync(httpContext);
        }
    }
}
