using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.OpenApi;
using SmartAgri.Api.Tenancy;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    /// <summary>Access token lifetime: matches the frontend's 30-minute session
    /// (<c>DEMO_SESSION_TIMEOUT_MS</c>). No refresh tokens in M1.</summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Lifetime of the Identity sign-in cookie that bridges <c>POST /api/v1/auth/login</c>
    /// and <c>/connect/authorize</c>. Not sliding, and equal to the access token lifetime,
    /// so the SPA cannot silently re-authorize past the 30-minute session.
    /// </summary>
    public static readonly TimeSpan SignInCookieLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// ASP.NET Core Identity (accounts, password hashing, lockout, the sign-in cookie),
    /// the OpenIddict server (authorization code + PKCE only) and OpenIddict validation of
    /// the server's own access tokens, plus one authorization policy per permission.
    /// </summary>
    /// <exception cref="InvalidOperationException">Outside Development, token
    /// certificates are not configured (see <see cref="TokenCredentials"/>).</exception>
    public static WebApplicationBuilder AddSmartAgriAuthentication(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var section = builder.Configuration.GetSection(SmartAgriAuthenticationOptions.SectionName);
        services.Configure<SmartAgriAuthenticationOptions>(section);
        var options = section.Get<SmartAgriAuthenticationOptions>() ?? new SmartAgriAuthenticationOptions();

        // Fails fast (before the host is built) outside Development without keys — except
        // while Microsoft.Extensions.ApiDescription.Server generates the OpenAPI document at
        // build time (BuildTimeOpenApi.IsGeneratingDocument), which runs this composition
        // root without ever starting the server or needing real certificates.
        var credentials = BuildTimeOpenApi.IsGeneratingDocument
            ? TokenCredentials.Ephemeral
            : TokenCredentials.Resolve(options, builder.Environment);
        var isDevelopment = builder.Environment.IsDevelopment();

        services
            .AddIdentityCore<Account>(identity =>
            {
                // Identity's user name is "{organizationCode}/{loginName}" (authentication ADR).
                identity.User.AllowedUserNameCharacters += "/";

                identity.Lockout.AllowedForNewUsers = true;
                identity.Lockout.MaxFailedAccessAttempts = 5;
                identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddSignInManager()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddClaimsPrincipalFactory<AccountClaimsPrincipalFactory>();

        // Check the security stamp every time the cookie is read (only on /connect/*), so
        // a lockout or password change stops the cookie from minting new tokens at once.
        services.Configure<SecurityStampValidatorOptions>(stamp => stamp.ValidationInterval = TimeSpan.Zero);

        services
            .AddAuthentication(authentication =>
            {
                // API endpoints only ever accept bearer access tokens. The Identity cookie
                // is read explicitly by /connect/authorize and /connect/endsession, never
                // implicitly, so it cannot authorize an API call (no CSRF surface there).
                authentication.DefaultScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            })
            .AddIdentityCookies();

        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = "smartagri.signin";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            cookie.ExpireTimeSpan = SignInCookieLifetime;
            cookie.SlidingExpiration = false;

            cookie.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = async context =>
                {
                    // The account row is organization scoped: establish the organization
                    // named inside this server-encrypted cookie before Identity reloads it.
                    if (ClaimsOrganizationContext.Resolve(context.Principal) is not { } organizationId)
                    {
                        context.RejectPrincipal();
                        return;
                    }

                    context.HttpContext.RequestServices.GetRequiredService<ClaimsOrganizationContext>().Pin(organizationId);
                    await SecurityStampValidator.ValidatePrincipalAsync(context);
                },

                // Never redirect API callers to an MVC login page.
                OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                },
            };
        });

        services
            .AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
            .AddServer(server =>
            {
                server
                    .SetAuthorizationEndpointUris(ConnectEndpoints.AuthorizePath)
                    .SetTokenEndpointUris(ConnectEndpoints.TokenPath)
                    .SetEndSessionEndpointUris(ConnectEndpoints.EndSessionPath)
                    .SetUserInfoEndpointUris(ConnectEndpoints.UserInfoPath);

                // Authorization code + PKCE only: no implicit, password, client
                // credentials or refresh token flows.
                server.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();

                server.SetAccessTokenLifetime(AccessTokenLifetime);

                // Access tokens are signed JWTs, not encrypted: they only carry sub, org_id
                // and role, and the SPA never needs to read them anyway. Authorization codes
                // stay encrypted.
                server.DisableAccessTokenEncryption();

                if (!string.IsNullOrWhiteSpace(options.Issuer))
                {
                    server.SetIssuer(new Uri(options.Issuer, UriKind.Absolute));
                }

                if (credentials.UseEphemeralKeys)
                {
                    server.AddEphemeralSigningKey().AddEphemeralEncryptionKey();
                }
                else
                {
                    server
                        .AddSigningCertificate(credentials.SigningCertificate!)
                        .AddEncryptionCertificate(credentials.EncryptionCertificate!);
                }

                var aspNetCore = server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough();

                // Local development and the test host speak plain HTTP.
                if (isDevelopment)
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseAspNetCore();
            });

        services.AddScoped<AdminSpaClientRegistrar>();

        services.AddScoped<IAccountPermissionSource, DatabaseAccountPermissionSource>();
        services.AddScoped<RequestAccountPermissions>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();
        services
            .AddAuthorizationBuilder()
            .AddPermissionPolicies()
            // Secure by default: an endpoint that declares nothing requires a signed-in
            // caller. Anonymous endpoints opt out explicitly with AllowAnonymous().
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return builder;
    }
}
