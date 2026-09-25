using Microsoft.AspNetCore.Identity;

namespace SmartAgri.Api.Authentication;

/// <summary>
/// Traditional Chinese messages for the Identity errors an API caller or the <c>setup</c>
/// operator can actually see (password rules and user-name rules). They reach the
/// frontend verbatim through <c>422</c> <c>errors</c>. Error <em>codes</em> are unchanged.
/// </summary>
public sealed class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError PasswordMismatch() =>
        new() { Code = nameof(PasswordMismatch), Description = "目前密碼不正確。" };

    public override IdentityError PasswordTooShort(int length) =>
        new() { Code = nameof(PasswordTooShort), Description = $"密碼至少需要 {length} 個字元。" };

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        new() { Code = nameof(PasswordRequiresUniqueChars), Description = $"密碼至少需要 {uniqueChars} 種不同的字元。" };

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        new() { Code = nameof(PasswordRequiresNonAlphanumeric), Description = "密碼需要包含至少一個符號（例如 ! 或 #）。" };

    public override IdentityError PasswordRequiresDigit() =>
        new() { Code = nameof(PasswordRequiresDigit), Description = "密碼需要包含至少一個數字。" };

    public override IdentityError PasswordRequiresLower() =>
        new() { Code = nameof(PasswordRequiresLower), Description = "密碼需要包含至少一個小寫英文字母。" };

    public override IdentityError PasswordRequiresUpper() =>
        new() { Code = nameof(PasswordRequiresUpper), Description = "密碼需要包含至少一個大寫英文字母。" };

    public override IdentityError InvalidUserName(string? userName) =>
        new() { Code = nameof(InvalidUserName), Description = "帳號名稱含有不允許的字元。" };

    public override IdentityError DuplicateUserName(string userName) =>
        new() { Code = nameof(DuplicateUserName), Description = "這個帳號名稱已經有人使用。" };
}
