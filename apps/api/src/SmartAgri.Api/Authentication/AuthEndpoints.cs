using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Tenancy;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Authentication;

/// <summary><c>GET /api/v1/auth/login-options</c> response.</summary>
/// <param name="OrganizationCodeRequired">False exactly when the deployment has one
/// organization; the login page can then hide the organization code field.</param>
public sealed record LoginOptionsResponse(bool OrganizationCodeRequired);

/// <summary><c>POST /api/v1/auth/login</c> request.</summary>
/// <param name="OrganizationCode">May be omitted only when exactly one organization exists.</param>
public sealed record LoginRequest(string? OrganizationCode, string? LoginName, string? Password);

/// <summary>
/// Sign-in endpoints used by the Angular login page. A successful login only sets the
/// Identity cookie; the SPA then continues the authorization code + PKCE flow at
/// <c>/connect/authorize</c> to obtain an access token.
/// </summary>
public static class AuthEndpoints
{
    private static readonly Lazy<(Account Account, string Hash)> TimingDecoy = new(() =>
    {
        var organization = new Organization(Guid.NewGuid(), "timing decoy", "timing-decoy");
        var account = Account.Create(organization, "decoy", "decoy", AccountRole.ExternalCustomer);
        var hash = new PasswordHasher<Account>().HashPassword(account, Guid.NewGuid().ToString("N"));
        return (account, hash);
    });

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth").AllowAnonymous();

        auth.MapGet("/login-options", GetLoginOptionsAsync)
            .Produces<LoginOptionsResponse>(StatusCodes.Status200OK);

        auth.MapPost("/login", LoginAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", (Delegate)LogoutAsync)
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    internal static async Task<LoginOptionsResponse> GetLoginOptionsAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var organizations = await dbContext.Organizations.Take(2).CountAsync(cancellationToken);
        return new LoginOptionsResponse(OrganizationCodeRequired: organizations != 1);
    }

    /// <summary>
    /// <c>204</c> and the Identity cookie on success. <b>Every</b> failure — unknown or
    /// missing organization code, unknown account, wrong password, locked account, blank
    /// fields — is the same bodiless <c>401</c>, so the response never reveals which part
    /// was wrong or whether an account exists. Five consecutive wrong passwords lock the
    /// account for 15 minutes.
    /// </summary>
    internal static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        AccountLookup accountLookup,
        ClaimsOrganizationContext organizationContext,
        SignInManager<Account> signInManager,
        CancellationToken cancellationToken)
    {
        // Signing in acts as nobody, whatever bearer token the caller may have attached:
        // the organization comes only from the account found below.
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var loginName = request.LoginName ?? string.Empty;
        var password = request.Password ?? string.Empty;

        var organizationCode = string.IsNullOrWhiteSpace(request.OrganizationCode)
            ? await GetSoleOrganizationCodeAsync(dbContext, cancellationToken)
            : request.OrganizationCode;

        var found = organizationCode is null || string.IsNullOrWhiteSpace(loginName) || password.Length == 0
            ? null
            : await accountLookup.FindForSignInAsync(organizationCode, loginName, cancellationToken);

        Account? account = null;
        if (found is not null)
        {
            // Nobody is signed in yet, so the request has no organization; Identity is
            // about to write this account's row (failed-attempt count, lockout, security
            // stamp), which the organization write guard only allows for the current
            // organization.
            organizationContext.Pin(found.OrganizationId);

            // Reload through the normal, organization-filtered path so Identity works on a
            // tracked instance (AccountLookup's result is untracked, and Identity's user
            // validator re-queries the same row before every update).
            account = await signInManager.UserManager.FindByIdAsync(found.Id.ToString("D"));
        }

        if (account is null)
        {
            // Spend about as long as a real password check, so response time does not
            // tell unknown accounts apart from wrong passwords.
            var decoy = TimingDecoy.Value;
            signInManager.UserManager.PasswordHasher.VerifyHashedPassword(decoy.Account, decoy.Hash, password);
            return ApiErrors.Unauthorized();
        }

        var result = await signInManager.CheckPasswordSignInAsync(account, password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            return ApiErrors.Unauthorized();
        }

        await signInManager.SignInAsync(account, isPersistent: false);
        return Results.NoContent();
    }

    internal static async Task<IResult> LogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.NoContent();
    }

    private static async Task<string?> GetSoleOrganizationCodeAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var codes = await dbContext.Organizations
            .OrderBy(organization => organization.Id)
            .Select(organization => organization.Code)
            .Take(2)
            .ToListAsync(cancellationToken);
        return codes is [var only] ? only : null;
    }
}
