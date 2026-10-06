using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Chat;
using SmartAgri.Api.Tenancy;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// The <c>Visitor</c> authentication scheme of the website channel's visitor API (M5a plan §3 D):
/// <c>Authorization: Visitor &lt;token&gt;</c>, a token from <see cref="VisitorTokens"/>. A valid one
/// becomes a principal with exactly three claims — <see cref="VisitorIdClaim"/>,
/// <see cref="OrganizationClaimTypes.OrganizationId"/> and <see cref="AssistantIdClaim"/> — and no
/// <c>sub</c>, so it is never an account: <see cref="ClaimsOrganizationContext"/> takes the
/// organization from it (query filters, write guards and model-call recording work unchanged),
/// while <c>AccountClaims.GetAccountId</c> stays <see langword="null"/>.
/// </summary>
/// <remarks>
/// <para>
/// The two kinds of caller never mix. <c>/api/v1/public/*</c> authorizes with
/// <see cref="Policy"/>, which authenticates <b>only</b> this scheme — the authorization middleware
/// replaces the request's principal with this scheme's result, so a member's bearer token there
/// is simply absent (<c>401</c>). Every other endpoint authenticates the default scheme (OpenIddict
/// validation), which only reads <c>Bearer</c> tokens, so a visitor token is absent there too
/// (<c>401</c>).
/// </para>
/// <para>
/// No CORS policy is registered anywhere: the visitor API is called only by the widget, which the
/// API itself serves (same origin), and <see cref="PublicOriginGuard"/> refuses any other origin.
/// </para>
/// </remarks>
public static class VisitorAuthentication
{
    public const string Scheme = "Visitor";

    /// <summary>The authorization policy of every <c>/api/v1/public/*</c> endpoint that needs a visitor.</summary>
    public const string Policy = "visitor";

    /// <summary>The random visitor id (never stored).</summary>
    public const string VisitorIdClaim = "visitor_id";

    /// <summary>The one assistant the token was issued for.</summary>
    public const string AssistantIdClaim = "assistant_id";

    /// <summary>The keyed <see cref="ChatRunLocks"/> instance that holds one running reply per visitor.</summary>
    public const string RunLocksKey = "visitor";

    /// <summary>Registers the scheme, <see cref="Policy"/>, <see cref="VisitorTokens"/>, the
    /// session endpoint's assistant lookup and the per-visitor run locks. Requires Data Protection (<c>AddSmartAgriDataProtection</c>).</summary>
    public static IServiceCollection AddVisitorAuthentication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<VisitorTokens>();
        services.AddScoped<SmartAgri.Infrastructure.Assistants.PublicAssistantLookup>();
        services.AddKeyedSingleton<ChatRunLocks>(RunLocksKey);
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, VisitorAuthenticationHandler>(Scheme, displayName: null, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(VisitorIdClaim)
                .RequireClaim(AssistantIdClaim));
        return services;
    }

    /// <summary>The visitor's id and assistant id from a principal this scheme produced, or
    /// <see langword="null"/> for any other principal.</summary>
    public static (Guid VisitorId, Guid AssistantId)? Read(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true
            || !string.Equals(principal.Identity.AuthenticationType, Scheme, StringComparison.Ordinal))
        {
            return null;
        }

        return Guid.TryParseExact(principal.FindFirstValue(VisitorIdClaim), "D", out var visitorId)
            && Guid.TryParseExact(principal.FindFirstValue(AssistantIdClaim), "D", out var assistantId)
            ? (visitorId, assistantId)
            : null;
    }
}

/// <summary>Reads <c>Authorization: Visitor &lt;token&gt;</c> (see <see cref="VisitorAuthentication"/>).
/// No such header is "no result"; a header with an invalid or expired token is a failure; both
/// challenge with a bodiless <c>401</c>.</summary>
public sealed class VisitorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string Prefix = VisitorAuthentication.Scheme + " ";

    private readonly VisitorTokens _tokens;

    public VisitorAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        VisitorTokens tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers.Authorization;
        if (headers.Count != 1 || headers[0] is not { } header || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (_tokens.TryRead(header[Prefix.Length..]) is not { } claims)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired visitor token."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(VisitorAuthentication.VisitorIdClaim, claims.VisitorId.ToString("D")),
                new Claim(OrganizationClaimTypes.OrganizationId, claims.OrganizationId.ToString("D")),
                new Claim(VisitorAuthentication.AssistantIdClaim, claims.AssistantId.ToString("D")),
            ],
            VisitorAuthentication.Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = VisitorAuthentication.Scheme;
        return Task.CompletedTask;
    }
}
