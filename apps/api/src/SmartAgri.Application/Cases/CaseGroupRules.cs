using SmartAgri.Application.Validation;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Application.Cases;

/// <summary>
/// The rules of <c>/api/v1/case-groups</c> that need no database (M7 plan §3 A; issue #246): the
/// name, and who may be a member. Uniqueness of the name is the endpoint's (it needs the
/// organization's other groups).
/// </summary>
public static class CaseGroupRules
{
    public const string NameField = "name";

    public const string NameRequiredMessage = "請輸入承辦組名稱。";

    public static readonly string NameTooLongMessage = $"承辦組名稱請在 {CaseGroup.NameMaxLength} 個字以內。";

    /// <summary>The trimmed name, or a <c>422</c> under <see cref="NameField"/> when it is blank or
    /// longer than <see cref="CaseGroup.NameMaxLength"/> characters.</summary>
    public static ValidationResult<string> ValidateName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return ValidationResult<string>.Invalid(NameField, NameRequiredMessage);
        }

        if (trimmed.Length > CaseGroup.NameMaxLength)
        {
            return ValidationResult<string>.Invalid(NameField, NameTooLongMessage);
        }

        return ValidationResult<string>.Valid(trimmed);
    }

    /// <summary>
    /// Whether an account with <paramref name="role"/> may be a member: only the organization's own
    /// people, <c>smb-admin</c> and <c>internal-employee</c>. External customers never handle cases
    /// in the first version (case ADR「第一版只給組織內部帳號」).
    /// </summary>
    public static bool IsEligibleMember(AccountRole role) =>
        role is AccountRole.SmbAdmin or AccountRole.InternalEmployee;
}
