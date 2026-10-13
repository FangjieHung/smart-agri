using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using SmartAgri.Api.Tenancy;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Infrastructure.Accounts;
using static OpenIddict.Abstractions.OpenIddictConstants;
using OAuthErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;

namespace SmartAgri.Api.Authentication;

/// <summary>
/// The OpenIddict server endpoints (all in passthrough mode: OpenIddict validates the
/// protocol request, these handlers decide who the user is and what goes in the tokens).
/// </summary>
public static class ConnectEndpoints
{
    public const string AuthorizePath = "connect/authorize";
    public const string TokenPath = "connect/token";
    public const string EndSessionPath = "connect/endsession";
    public const string UserInfoPath = "connect/userinfo";

    /// <summary>OAuth 2.0 token revocation (RFC 7009), handled by OpenIddict itself (no passthrough): the
    /// SPA revokes its refresh token here when the user logs out (#354).</summary>
    public const string RevocationPath = "connect/revoke";

    private const string ServerScheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    public static IEndpointRouteBuilder MapConnectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // OpenIddict's own OAuth 2.0 / OpenID Connect endpoints, not part of the
        // apps/api/openapi/v1.json JSON contract the frontend generates types from (their
        // shapes are the OAuth/OIDC specs, not this API's DTOs); OpenIddict's own discovery
        // document (/.well-known/openid-configuration) describes them instead.
        endpoints.MapMethods("/" + AuthorizePath, [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost("/" + TokenPath, ExchangeAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapMethods("/" + EndSessionPath, [HttpMethods.Get, HttpMethods.Post], EndSessionAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapMethods("/" + UserInfoPath, [HttpMethods.Get, HttpMethods.Post], (Delegate)UserInfoAsync).AllowAnonymous().ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>
    /// Signed in (valid Identity cookie, account still usable: not locked out, not deactivated) → issue an authorization
    /// code. Otherwise redirect to the Angular login page with <c>returnUrl</c> set to
    /// this same authorization request, or fail with <c>login_required</c> when the client
    /// asked for <c>prompt=none</c>.
    /// </summary>
    internal static async Task<IResult> AuthorizeAsync(
        HttpContext httpContext,
        ClaimsOrganizationContext organizationContext,
        UserManager<Account> userManager,
        IOptions<SmartAgriAuthenticationOptions> options,
        TimeProvider clock)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict authorization request could not be retrieved.");

        var account = await GetSignedInAccountAsync(httpContext, organizationContext, userManager);
        if (account is null)
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Results.Forbid(
                    ErrorProperties(OAuthErrors.LoginRequired, "The user is not signed in."),
                    [ServerScheme]);
            }

            return Results.Redirect(LoginRedirect(httpContext, request, options.Value.LoginPath));
        }

        // The sign-in starts here: the code, and every refresh token after it, carry this time and stamp.
        var session = new SignInSession(clock.GetUtcNow(), await userManager.GetSecurityStampAsync(account) ?? string.Empty);
        return Results.SignIn(CreatePrincipal(account, request.GetScopes(), session), authenticationScheme: ServerScheme);
    }

    /// <summary>
    /// Redeems an authorization code or a refresh token. OpenIddict has already checked the grant
    /// itself (code + PKCE verifier + redirect URI, or a refresh token that is unexpired, not redeemed
    /// and not revoked — a redeemed one revokes its whole chain) and the client. The account is loaded
    /// again so the role is current and a grant stops working once the account cannot sign in any more
    /// (<see cref="CanContinueAsync"/>).
    /// </summary>
    internal static async Task<IResult> ExchangeAsync(
        HttpContext httpContext,
        ClaimsOrganizationContext organizationContext,
        UserManager<Account> userManager,
        SignInManager<Account> signInManager,
        IOpenIddictAuthorizationManager authorizationManager,
        TimeProvider clock)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict token request could not be retrieved.");

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            return Results.Forbid(ErrorProperties(OAuthErrors.UnsupportedGrantType, "Only the authorization code and refresh token grants are supported."), [ServerScheme]);
        }

        var grant = (await httpContext.AuthenticateAsync(ServerScheme)).Principal;
        var session = SignInSession.From(grant);

        // A sign-in that changed the password carries on with the new stamp, recorded on its authorization
        // (#360); every token after this one carries it too.
        if (session is not null
            && await PasswordChangeSignIns.ReadSecurityStampAsync(authorizationManager, grant!.GetAuthorizationId(), httpContext.RequestAborted) is { } restamped)
        {
            session = session with { SecurityStamp = restamped };
        }

        var account = await FindGrantAccountAsync(grant, organizationContext, userManager);
        var now = clock.GetUtcNow();

        if (account is null || session is null || !await CanContinueAsync(account, session, now, userManager, signInManager))
        {
            var description = request.IsRefreshTokenGrantType()
                ? "The refresh token is no longer valid."
                : "The authorization code is no longer valid.";
            return Results.Forbid(ErrorProperties(OAuthErrors.InvalidGrant, description), [ServerScheme]);
        }

        var principal = CreatePrincipal(account, grant!.GetScopes(), session);
        session.LimitLifetimes(principal, now);
        return Results.SignIn(principal, authenticationScheme: ServerScheme);
    }

    /// <summary>
    /// Whether a sign-in may still obtain tokens, checked on every code and refresh exchange: the
    /// account is not deactivated (issue #356; deactivating also revokes its tokens and rotates the
    /// stamp, so this is the check that holds even for a token entry the revocation missed), not
    /// locked out and may sign in, its security stamp is the one it signed in with (a
    /// password change or <c>reset-password</c> rotates it; the sign-in that changed the password
    /// continues with the new one, <see cref="PasswordChangeSignIns"/>), and the session's absolute lifetime has not
    /// run out. An account that must change its password still gets tokens: they only reach
    /// <c>GET /api/v1/me</c> and change-password (<c>PasswordChangeGate</c>).
    /// </summary>
    private static async Task<bool> CanContinueAsync(
        Account account,
        SignInSession session,
        DateTimeOffset now,
        UserManager<Account> userManager,
        SignInManager<Account> signInManager) =>
        !account.IsDeactivated
        && !await userManager.IsLockedOutAsync(account)
        && await signInManager.CanSignInAsync(account)
        && string.Equals(session.SecurityStamp, await userManager.GetSecurityStampAsync(account) ?? string.Empty, StringComparison.Ordinal)
        && !session.HasEnded(now);

    /// <summary>The grant's account, loaded under its own organization (the request has no other), or
    /// <see langword="null"/> when it no longer exists there.</summary>
    private static async Task<Account?> FindGrantAccountAsync(
        ClaimsPrincipal? grant,
        ClaimsOrganizationContext organizationContext,
        UserManager<Account> userManager)
    {
        if (AccountClaims.GetAccountId(grant) is not { } accountId
            || ClaimsOrganizationContext.Resolve(grant) is not { } organizationId)
        {
            return null;
        }

        organizationContext.Pin(organizationId);
        var account = await userManager.FindByIdAsync(accountId.ToString("D"));
        return account?.OrganizationId == organizationId ? account : null;
    }

    /// <summary>Signs out of the Identity cookie, then lets OpenIddict redirect to the
    /// validated <c>post_logout_redirect_uri</c> (or the login page).</summary>
    internal static async Task<IResult> EndSessionAsync(HttpContext httpContext, IOptions<SmartAgriAuthenticationOptions> options)
    {
        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.SignOut(new AuthenticationProperties { RedirectUri = options.Value.LoginPath }, [ServerScheme]);
    }

    /// <summary>Returns only <c>sub</c>; everything else about the account is on
    /// <c>GET /api/v1/me</c>.</summary>
    internal static async Task<IResult> UserInfoAsync(HttpContext httpContext)
    {
        var principal = (await httpContext.AuthenticateAsync(ServerScheme)).Principal;
        if (AccountClaims.GetAccountId(principal) is not { } accountId)
        {
            return Results.Challenge(ErrorProperties(OAuthErrors.InvalidToken, "The access token is not valid."), [ServerScheme]);
        }

        return Results.Json(new Dictionary<string, string> { [Claims.Subject] = accountId.ToString("D") });
    }

    /// <summary>
    /// The token principal: exactly <c>sub</c>, <c>org_id</c> and <c>role</c> — never
    /// permissions, which are read from the database per request. <c>sub</c> also goes in
    /// the identity token (required by OpenID Connect); the rest only in the access token.
    /// The <paramref name="session"/> claims have no destination: they stay in the
    /// authorization code and refresh tokens.
    /// </summary>
    internal static ClaimsPrincipal CreatePrincipal(Account account, IEnumerable<string> requestedScopes, SignInSession session)
    {
        var identity = new ClaimsIdentity(ServerScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, account.Id.ToString("D"));
        identity.SetClaim(AccountClaims.OrganizationId, account.OrganizationId.ToString("D"));
        identity.SetClaim(Claims.Role, WireNames<AccountRole>.ToWire(account.Role));
        session.WriteTo(identity);

        // "openid", and "offline_access" when the client asks for refresh tokens (the admin SPA
        // does, #354; OpenIddict only allows it to clients with the refresh token grant).
        identity.SetScopes(requestedScopes.Where(scope => scope is Scopes.OpenId or Scopes.OfflineAccess));

        identity.SetDestinations(static claim => claim.Type switch
        {
            Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            AccountClaims.OrganizationId or Claims.Role => [Destinations.AccessToken],
            _ => [],
        });

        return new ClaimsPrincipal(identity);
    }

    private static async Task<Account?> GetSignedInAccountAsync(
        HttpContext httpContext,
        ClaimsOrganizationContext organizationContext,
        UserManager<Account> userManager)
    {
        // Validating the cookie (see OnValidatePrincipal) pins its organization and checks
        // the security stamp.
        var cookie = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!cookie.Succeeded || ClaimsOrganizationContext.Resolve(cookie.Principal) is not { } organizationId)
        {
            return null;
        }

        organizationContext.Pin(organizationId);
        var account = await userManager.GetUserAsync(cookie.Principal!);
        if (account is null
            || account.OrganizationId != organizationId
            || account.IsDeactivated
            || await userManager.IsLockedOutAsync(account))
        {
            return null;
        }

        return account;
    }

    private static string LoginRedirect(HttpContext httpContext, OpenIddictRequest request, string loginPath)
    {
        // Rebuilt from the parsed parameters so a POSTed authorization request round-trips too.
        var parameters = request.GetParameters()
            .Select(parameter => new KeyValuePair<string, string?>(parameter.Key, (string?)parameter.Value));
        var returnUrl = httpContext.Request.PathBase + "/" + AuthorizePath + QueryString.Create(parameters);
        return QueryHelpers.AddQueryString(loginPath, "returnUrl", returnUrl);
    }

    private static AuthenticationProperties ErrorProperties(string error, string description) =>
        new(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        });
}
