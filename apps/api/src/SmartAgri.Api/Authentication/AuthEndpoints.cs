using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authorization;
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

/// <summary><c>POST /api/v1/auth/change-password</c> request.</summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

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

        // Outside the anonymous group: it needs the caller's bearer token. Exempt from the
        // "must change password" gate — it is how the gate is lifted.
        endpoints.MapPost("/api/v1/auth/change-password", ChangePasswordAsync)
            .RequireAuthorization()
            .AllowWhilePasswordChangeRequired();

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

    /// <summary>
    /// Replaces the signed-in account's password and clears
    /// <see cref="Account.PasswordChangeRequired"/>; <c>204</c> on success. The new password
    /// must pass Identity's rules and differ from the current one. Identity also rotates
    /// the security stamp, so the old password and any sign-in cookie issued before stop
    /// working (the access token in hand keeps working until it expires, now ungated).
    /// </summary>
    /// <remarks>
    /// Errors, following the API's error rules:
    /// <list type="bullet">
    /// <item><c>422</c> with <c>errors.currentPassword</c> — blank or wrong current password.
    /// The caller is already authenticated as this account, so saying "wrong" reveals
    /// nothing, and <c>401</c> would make the SPA drop the session over a typo. A wrong
    /// current password still counts as a failed sign-in towards lockout.</item>
    /// <item><c>422</c> with <c>errors.newPassword</c> — blank, same as the current one, or
    /// rejected by Identity's password rules (one message per broken rule).</item>
    /// <item><c>401</c> (no body) — no usable token, the account no longer exists, or it is
    /// locked out (including by this attempt): it can no longer sign in, so the SPA goes
    /// back to the login page.</item>
    /// </list>
    /// </remarks>
    internal static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext httpContext,
        SignInManager<Account> signInManager)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } accountId)
        {
            return ApiErrors.Unauthorized();
        }

        // Loaded under the organization filter of the token's org_id, tracked, so Identity
        // can write it back.
        var userManager = signInManager.UserManager;
        var account = await userManager.FindByIdAsync(accountId.ToString("D"));
        if (account is null)
        {
            return ApiErrors.Unauthorized();
        }

        var currentPassword = request.CurrentPassword ?? string.Empty;
        var newPassword = request.NewPassword ?? string.Empty;

        var blank = new Dictionary<string, string[]>();
        if (currentPassword.Length == 0)
        {
            blank[CurrentPasswordField] = ["請輸入目前密碼。"];
        }

        if (newPassword.Length == 0)
        {
            blank[NewPasswordField] = ["請輸入新密碼。"];
        }

        if (blank.Count > 0)
        {
            return PasswordNotChanged(blank);
        }

        var check = await signInManager.CheckPasswordSignInAsync(account, currentPassword, lockoutOnFailure: true);
        if (check.IsLockedOut || check.IsNotAllowed)
        {
            return ApiErrors.Unauthorized();
        }

        if (!check.Succeeded)
        {
            return PasswordNotChanged(CurrentPasswordField, [userManager.ErrorDescriber.PasswordMismatch().Description]);
        }

        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
        {
            return PasswordNotChanged(NewPasswordField, ["新密碼不可與目前密碼相同。"]);
        }

        // Cleared before ChangePasswordAsync so the flag and the new hash are saved in the
        // same UPDATE; if Identity rejects the new password nothing is saved at all.
        var wasRequired = account.PasswordChangeRequired;
        account.ClearPasswordChangeRequirement();
        var result = await userManager.ChangePasswordAsync(account, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            if (wasRequired)
            {
                account.RequirePasswordChange();
            }

            return PasswordNotChanged(NewPasswordField, [.. result.Errors.Select(error => error.Description)]);
        }

        return Results.NoContent();
    }

    internal static async Task<IResult> LogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.NoContent();
    }

    private const string CurrentPasswordField = "currentPassword";
    private const string NewPasswordField = "newPassword";

    private static IResult PasswordNotChanged(string field, string[] errors) =>
        PasswordNotChanged(new Dictionary<string, string[]> { [field] = errors });

    private static IResult PasswordNotChanged(IReadOnlyDictionary<string, string[]> errors) =>
        ApiErrors.ValidationFailed("密碼沒有變更。", errors);

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
