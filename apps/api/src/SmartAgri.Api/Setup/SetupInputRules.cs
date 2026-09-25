using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Setup;

/// <summary>
/// Checks the values <c>setup</c> collects before anything is written, with messages an
/// operator can act on. Each rule defers to the rule the entity or Identity enforces
/// anyway, so a value accepted here is never rejected later.
/// </summary>
public sealed class SetupInputRules
{
    private readonly string _allowedLoginCharacters;

    /// <param name="identityAllowedUserNameCharacters">The host's
    /// <c>IdentityOptions.User.AllowedUserNameCharacters</c>.</param>
    public SetupInputRules(string identityAllowedUserNameCharacters)
    {
        ArgumentNullException.ThrowIfNull(identityAllowedUserNameCharacters);

        // Identity allows '/' only because the internal user name is
        // "{organizationCode}/{loginName}"; the first administrator's login name has no
        // need for it, and keeping it out keeps the composite trivially unambiguous.
        _allowedLoginCharacters = identityAllowedUserNameCharacters.Replace("/", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>The organization name to store, or an error.</summary>
    public static SetupInputResult OrganizationName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        return trimmed.Length is 0 or > Organization.NameMaxLength
            ? SetupInputResult.Invalid($"組織名稱需為 1-{Organization.NameMaxLength} 個字。")
            : SetupInputResult.Valid(trimmed);
    }

    /// <summary>The normalized organization code (<see cref="Organization.NormalizeCode"/>), or an error.</summary>
    public static SetupInputResult OrganizationCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return SetupInputResult.Valid(Organization.NormalizeCode(value));
        }
        catch (ArgumentException)
        {
            return SetupInputResult.Invalid(
                $"組織代碼只能使用英文字母 a-z、數字與 -，長度 1-{Organization.CodeMaxLength} 個字（大寫會轉成小寫）。");
        }
    }

    /// <summary>The trimmed login name, or an error. ASCII only: the characters Identity
    /// accepts in a user name, minus <c>/</c>.</summary>
    public SetupInputResult AdminLogin(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > Account.LoginNameMaxLength
            || trimmed.Any(character => !_allowedLoginCharacters.Contains(character, StringComparison.Ordinal)))
        {
            return SetupInputResult.Invalid(
                $"管理者帳號名稱只能使用英文字母、數字與 - . _ @ +，長度 1-{Account.LoginNameMaxLength} 個字。");
        }

        return SetupInputResult.Valid(trimmed);
    }

    /// <summary>The trimmed display name, or an error.</summary>
    public static SetupInputResult AdminDisplayName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        return trimmed.Length is 0 or > Account.DisplayNameMaxLength
            ? SetupInputResult.Invalid($"管理者顯示名稱需為 1-{Account.DisplayNameMaxLength} 個字。")
            : SetupInputResult.Valid(trimmed);
    }
}

/// <summary>A normalized value, or the reason it was rejected.</summary>
public sealed record SetupInputResult(string? Value, string? Error)
{
    public bool IsValid => Error is null;

    public static SetupInputResult Valid(string value) => new(value, null);

    public static SetupInputResult Invalid(string error) => new(null, error);
}
