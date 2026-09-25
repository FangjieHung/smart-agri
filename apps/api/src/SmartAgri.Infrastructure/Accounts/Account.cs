using Microsoft.AspNetCore.Identity;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Infrastructure.Accounts;

/// <summary>
/// A sign-in account. Belongs to exactly one organization; its <see cref="LoginName"/> is
/// only unique within that organization (unique index on
/// <c>(OrganizationId, NormalizedLoginName)</c>).
/// </summary>
/// <remarks>
/// Identity keeps a global unique index on <c>NormalizedUserName</c>, so
/// <see cref="IdentityUser{TKey}.UserName"/> is stored as the composite
/// <c>"{organizationCode}/{loginName}"</c> (authentication ADR). The composite is internal
/// bookkeeping only: screens and API responses show <see cref="LoginName"/>. Because
/// organization codes never contain <c>/</c>, the composite is unambiguous.
/// </remarks>
public class Account : IdentityUser<Guid>, IOrganizationScoped
{
    public const int LoginNameMaxLength = 64;
    public const int DisplayNameMaxLength = 200;

    /// <summary>For EF Core materialization.</summary>
    private Account()
    {
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>The name the account signs in with, as entered.</summary>
    public string LoginName { get; private set; } = string.Empty;

    /// <summary><see cref="LoginName"/> normalized the way Identity normalizes user names.</summary>
    public string NormalizedLoginName { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public AccountRole Role { get; private set; }

    /// <summary>
    /// The account signs in with a password someone else chose (the one-time password
    /// printed by the <c>setup</c> command) and must set its own before using anything
    /// but <c>GET /api/v1/me</c> and <c>POST /api/v1/auth/change-password</c>. Read from
    /// the database on every request, like permissions, never carried in a token.
    /// </summary>
    public bool PasswordChangeRequired { get; private set; }

    public static Account Create(Organization organization, string loginName, string displayName, AccountRole role)
    {
        ArgumentNullException.ThrowIfNull(organization);

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > DisplayNameMaxLength)
        {
            throw new ArgumentException($"A display name must be 1-{DisplayNameMaxLength} characters.", nameof(displayName));
        }

        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organization.Id,
            DisplayName = displayName.Trim(),
            Role = role,
            SecurityStamp = Guid.NewGuid().ToString("N"),

            // IdentityUser defaults this to false, and UserManager.CreateAsync only turns it
            // on for accounts it creates itself. Accounts can also be inserted directly
            // (seeding, setup, tests), and an account with lockout disabled could be
            // password-guessed forever, so every account starts with it on.
            LockoutEnabled = true,
        };
        account.SetLoginName(organization.Code, loginName);
        return account;
    }

    /// <summary>Marks the account's current password as temporary (see
    /// <see cref="PasswordChangeRequired"/>).</summary>
    public void RequirePasswordChange() => PasswordChangeRequired = true;

    /// <summary>Clears <see cref="PasswordChangeRequired"/>; only after the account has
    /// set a password of its own.</summary>
    public void ClearPasswordChangeRequirement() => PasswordChangeRequired = false;

    /// <summary>
    /// Same rule as Identity's default <c>UpperInvariantLookupNormalizer</c>, so the
    /// per-organization index and Identity's own user-name index agree on what counts as
    /// "the same name".
    /// </summary>
    public static string NormalizeLoginName(string loginName) =>
        loginName.Trim().Normalize().ToUpperInvariant();

    /// <summary>The internal Identity user name for a login name in an organization.</summary>
    public static string ComposeUserName(string organizationCode, string loginName) =>
        $"{Organization.NormalizeCode(organizationCode)}/{loginName.Trim()}";

    private void SetLoginName(string organizationCode, string loginName)
    {
        ArgumentNullException.ThrowIfNull(loginName);

        var trimmed = loginName.Trim();
        if (trimmed.Length is 0 or > LoginNameMaxLength)
        {
            throw new ArgumentException($"A login name must be 1-{LoginNameMaxLength} characters.", nameof(loginName));
        }

        LoginName = trimmed;
        NormalizedLoginName = NormalizeLoginName(trimmed);
        UserName = ComposeUserName(organizationCode, trimmed);
        NormalizedUserName = UserName.Normalize().ToUpperInvariant();
    }
}
