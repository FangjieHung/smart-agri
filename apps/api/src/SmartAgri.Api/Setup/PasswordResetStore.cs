using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using SmartAgri.Api.Tenancy;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Setup;

/// <summary>Outcome of <see cref="IPasswordResetStore.ResetAsync"/>.</summary>
public enum PasswordResetOutcome
{
    Reset,

    /// <summary>No organization has this code; nothing was written.</summary>
    OrganizationNotFound,

    /// <summary>The organization exists but has no account with this login name; nothing was written.</summary>
    AccountNotFound,

    /// <summary>Identity refused the password or the update; nothing was written.</summary>
    Rejected,
}

/// <summary>What <see cref="IPasswordResetStore.ResetAsync"/> did: on <see cref="PasswordResetOutcome.Reset"/>,
/// the organization and account as stored (for the operator's confirmation), never the password.</summary>
public sealed record PasswordResetResult(
    PasswordResetOutcome Outcome,
    IReadOnlyList<string> Errors,
    Guid? AccountId = null,
    string? OrganizationCode = null,
    string? OrganizationName = null,
    string? LoginName = null)
{
    public static PasswordResetResult Done(Guid accountId, string organizationCode, string organizationName, string loginName) =>
        new(PasswordResetOutcome.Reset, [], accountId, organizationCode, organizationName, loginName);

    public static readonly PasswordResetResult OrganizationNotFound = new(PasswordResetOutcome.OrganizationNotFound, []);

    public static readonly PasswordResetResult AccountNotFound = new(PasswordResetOutcome.AccountNotFound, []);

    public static PasswordResetResult Rejected(IEnumerable<string> errors) => new(PasswordResetOutcome.Rejected, [.. errors]);
}

/// <summary>The database side of <c>reset-password</c>, behind an interface so the command's
/// argument, refusal and output rules are testable without a database.</summary>
public interface IPasswordResetStore
{
    /// <summary>
    /// Gives the account <paramref name="loginName"/> of the organization <paramref name="organizationCode"/>
    /// (and no other) <paramref name="password"/> as a one-time password: must change it at the next
    /// sign-in, lockout and failed-attempt count cleared, security stamp rotated, and every token the
    /// authorization server stored for it revoked — atomically.
    /// </summary>
    Task<PasswordResetResult> ResetAsync(string organizationCode, string loginName, string password, CancellationToken cancellationToken);
}

/// <summary>EF Core + Identity + OpenIddict implementation of <see cref="IPasswordResetStore"/>.</summary>
/// <remarks>
/// What the rotated stamp and the revocation end (authentication ADR; see
/// <c>AuthenticationServiceCollectionExtensions</c>): the old password; the Identity sign-in cookie, whose
/// stamp is checked every time <c>/connect/*</c> reads it, so it can no longer mint authorization codes;
/// authorization codes already issued but not yet redeemed, and refresh tokens (their token entries are
/// revoked; the token endpoint also refuses a refresh token whose sign-in carried the old stamp, #354). Access
/// tokens already issued are self-contained JWTs that the Api validates without a database lookup, so
/// they keep authenticating until they expire (at most 30 minutes) — but <c>PasswordChangeRequired</c> is
/// read per request, so they reach only <c>GET /api/v1/me</c> and <c>change-password</c>, which needs the
/// new one-time password.
/// </remarks>
public sealed class EfPasswordResetStore : IPasswordResetStore
{
    private readonly AppDbContext _dbContext;
    private readonly UserManager<Account> _userManager;
    private readonly ClaimsOrganizationContext _organizationContext;
    private readonly IOpenIddictTokenManager _tokenManager;

    public EfPasswordResetStore(
        AppDbContext dbContext,
        UserManager<Account> userManager,
        ClaimsOrganizationContext organizationContext,
        IOpenIddictTokenManager tokenManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _organizationContext = organizationContext;
        _tokenManager = tokenManager;
    }

    public async Task<PasswordResetResult> ResetAsync(
        string organizationCode, string loginName, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationCode);
        ArgumentNullException.ThrowIfNull(loginName);
        ArgumentNullException.ThrowIfNull(password);

        // A code outside [a-z0-9-] or too long cannot exist (codes are validated on creation).
        string code;
        try
        {
            code = Organization.NormalizeCode(organizationCode);
        }
        catch (ArgumentException)
        {
            return PasswordResetResult.OrganizationNotFound;
        }

        // Organizations is the tenant table itself, not organization filtered.
        var organization = await _dbContext.Organizations.SingleOrDefaultAsync(candidate => candidate.Code == code, cancellationToken);
        if (organization is null)
        {
            return PasswordResetResult.OrganizationNotFound;
        }

        // No request, so no organization yet: act for this one. The account query below is then
        // organization filtered (another organization's account with the same login name is never
        // seen), and the organization write guard accepts the update.
        _organizationContext.Pin(organization.Id);

        var normalizedLoginName = Account.NormalizeLoginName(loginName);
        var account = normalizedLoginName.Length == 0
            ? null
            : await _dbContext.Accounts.SingleOrDefaultAsync(candidate => candidate.NormalizedLoginName == normalizedLoginName, cancellationToken);
        if (account is null)
        {
            return PasswordResetResult.AccountNotFound;
        }

        // Identity's password rules with the host's options, as UserManager.CreateAsync runs them for setup.
        var errors = new List<string>();
        foreach (var validator in _userManager.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(_userManager, account, password);
            errors.AddRange(validation.Errors.Select(error => error.Description));
        }

        if (errors.Count > 0)
        {
            return PasswordResetResult.Rejected(errors);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        account.PasswordHash = _userManager.PasswordHasher.HashPassword(account, password);
        account.RequirePasswordChange();
        account.LockoutEnd = null;
        account.AccessFailedCount = 0;

        // Rotates the security stamp and saves everything above in one UPDATE (through UserManager, so
        // Identity's user validators and concurrency stamp apply as on any other account change).
        var updated = await _userManager.UpdateSecurityStampAsync(account);
        if (!updated.Succeeded)
        {
            return PasswordResetResult.Rejected(updated.Errors.Select(error => error.Description));
        }

        // Authorization codes, refresh tokens (#354) and access/identity token entries issued to this account so far.
        await _tokenManager.RevokeBySubjectAsync(account.Id.ToString("D"), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return PasswordResetResult.Done(account.Id, organization.Code, organization.Name, account.LoginName);
    }
}
