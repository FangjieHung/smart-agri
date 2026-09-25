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

    private const string ServerScheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    public static IEndpointRouteBuilder MapConnectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/" + AuthorizePath, [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync).AllowAnonymous();
        endpoints.MapPost("/" + TokenPath, ExchangeAsync).AllowAnonymous();
        endpoints.MapMethods("/" + EndSessionPath, [HttpMethods.Get, HttpMethods.Post], EndSessionAsync).AllowAnonymous();
        endpoints.MapMethods("/" + UserInfoPath, [HttpMethods.Get, HttpMethods.Post], (Delegate)UserInfoAsync).AllowAnonymous();
        return endpoints;
    }

    /// <summary>
    /// Signed in (valid Identity cookie, account still usable) → issue an authorization
    /// code. Otherwise redirect to the Angular login page with <c>returnUrl</c> set to
    /// this same authorization request, or fail with <c>login_required</c> when the client
    /// asked for <c>prompt=none</c>.
    /// </summary>
    internal static async Task<IResult> AuthorizeAsync(
        HttpContext httpContext,
        ClaimsOrganizationContext organizationContext,
        UserManager<Account> userManager,
        IOptions<SmartAgriAuthenticationOptions> options)
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

        return Results.SignIn(CreatePrincipal(account, request.GetScopes()), authenticationScheme: ServerScheme);
    }

    /// <summary>
    /// Redeems an authorization code (OpenIddict has already checked the code, the PKCE
    /// verifier, the client and the redirect URI). The account is loaded again so a
    /// deleted or locked-out account cannot redeem a code, and the role is current.
    /// </summary>
    internal static async Task<IResult> ExchangeAsync(
        HttpContext httpContext,
        ClaimsOrganizationContext organizationContext,
        UserManager<Account> userManager,
        SignInManager<Account> signInManager)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict token request could not be retrieved.");

        if (!request.IsAuthorizationCodeGrantType())
        {
            return Results.Forbid(ErrorProperties(OAuthErrors.UnsupportedGrantType, "Only the authorization code grant is supported."), [ServerScheme]);
        }

        var codePrincipal = (await httpContext.AuthenticateAsync(ServerScheme)).Principal;
        var accountId = AccountClaims.GetAccountId(codePrincipal);
        var organizationId = ClaimsOrganizationContext.Resolve(codePrincipal);

        Account? account = null;
        if (accountId is not null && organizationId is not null)
        {
            organizationContext.Pin(organizationId.Value);
            account = await userManager.FindByIdAsync(accountId.Value.ToString("D"));
        }

        if (account is null
            || account.OrganizationId != organizationId
            || await userManager.IsLockedOutAsync(account)
            || !await signInManager.CanSignInAsync(account))
        {
            return Results.Forbid(ErrorProperties(OAuthErrors.InvalidGrant, "The authorization code is no longer valid."), [ServerScheme]);
        }

        return Results.SignIn(CreatePrincipal(account, codePrincipal!.GetScopes()), authenticationScheme: ServerScheme);
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
    /// </summary>
    internal static ClaimsPrincipal CreatePrincipal(Account account, IEnumerable<string> requestedScopes)
    {
        var identity = new ClaimsIdentity(ServerScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, account.Id.ToString("D"));
        identity.SetClaim(AccountClaims.OrganizationId, account.OrganizationId.ToString("D"));
        identity.SetClaim(Claims.Role, WireNames<AccountRole>.ToWire(account.Role));

        // Only "openid"; in particular never "offline_access" (no refresh tokens in M1).
        identity.SetScopes(requestedScopes.Where(scope => scope == Scopes.OpenId));

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
        if (account is null || account.OrganizationId != organizationId || await userManager.IsLockedOutAsync(account))
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
