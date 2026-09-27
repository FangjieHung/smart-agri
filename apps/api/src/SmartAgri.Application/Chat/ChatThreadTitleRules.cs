using SmartAgri.Domain.Chat;
using SmartAgri.Application.Validation;

namespace SmartAgri.Application.Chat;

/// <summary>
/// Validates a <c>PATCH .../conversations/{threadId}</c> title (mapping §5.5): blank or over
/// <see cref="ChatThread.TitleMaxLength"/> characters (after trimming) is a <c>422</c> with only
/// a <c>message</c> — the same shape <c>renameChatThread</c>'s mock used.
/// </summary>
public static class ChatThreadTitleRules
{
    public static ValidationResult<string> Validate(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return ValidationResult<string>.Invalid("title", "請輸入對話名稱。");
        }

        if (trimmed.Length > ChatThread.TitleMaxLength)
        {
            return ValidationResult<string>.Invalid("title", $"對話名稱請在 {ChatThread.TitleMaxLength} 個字以內。");
        }

        return ValidationResult<string>.Valid(trimmed);
    }
}
