using System.Text.RegularExpressions;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Chat;

namespace SmartAgri.Application.Chat;

/// <summary>
/// What <c>POST /api/v1/assistants/{id}/chat/runs</c> (M3 plan Slice 7; ticket #77) checks about
/// the question before anything is saved or streamed, and how a thread it creates is titled.
/// The messages follow the frontend's mock <c>sendChatMessage</c> (mapping §2.4: a <c>422</c> with
/// only a <c>message</c>); the length cap is the M3 plan's 2,000 characters, not the mock's 500.
/// </summary>
public static partial class ChatRunRules
{
    public const string QuestionField = "question";

    /// <summary>The longest question, in characters after trimming (M3 plan Slice 7).</summary>
    public const int QuestionMaxLength = 2000;

    /// <summary>A new thread's title is the question's first this many characters (the mock's
    /// <c>CHAT_THREAD_TITLE_MAX_LENGTH</c>), followed by 「…」 when cut.</summary>
    public const int TitleFromQuestionMaxLength = 24;

    /// <summary>The title of a thread that has no question yet (<c>POST .../conversations</c>);
    /// the first question replaces it (<see cref="TitleForFirstQuestion"/>).</summary>
    public const string DefaultThreadTitle = "新的對話";

    public const string QuestionRequiredMessage = "請先輸入問題。";

    public static readonly string QuestionTooLongMessage = $"問題請在 {QuestionMaxLength} 個字以內。";

    /// <summary>The longest accepted <c>RunAgentInput</c> user-message id (ticket #105): well
    /// over <c>crypto.randomUUID()</c>'s 36 characters, generous for any client id scheme.</summary>
    public const int ClientMessageIdMaxLength = 200;

    /// <summary>A non-blank question of at most <see cref="QuestionMaxLength"/> characters
    /// (trimmed).</summary>
    public static ValidationResult<string> ValidateQuestion(string? question)
    {
        var trimmed = (question ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return ValidationResult<string>.Invalid(QuestionField, QuestionRequiredMessage);
        }

        if (trimmed.Length > QuestionMaxLength)
        {
            return ValidationResult<string>.Invalid(QuestionField, QuestionTooLongMessage);
        }

        return ValidationResult<string>.Valid(trimmed);
    }

    /// <summary>
    /// A retry's question is recognized, not re-saved (ticket #105), by the id the client gave
    /// the <c>RunAgentInput</c> user message: letters, digits, <c>-</c>, <c>_</c>, <c>.</c> or
    /// <c>:</c> only, 1-<see cref="ClientMessageIdMaxLength"/> characters — covers
    /// <c>crypto.randomUUID()</c> and similar client-generated ids. Anything else (missing,
    /// blank, too long, or an unexpected character — a client could send arbitrary AG-UI
    /// message ids) is treated the same as no id at all: <see langword="null"/>, so the
    /// question is always saved as a new message.
    /// </summary>
    public static string? ValidateClientMessageId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return ClientMessageIdPattern().IsMatch(id) ? id : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]{1,200}$")]
    private static partial Regex ClientMessageIdPattern();

    /// <summary>
    /// A thread's title derived from its first question, exactly like the mock's
    /// <c>deriveThreadTitle</c>: whitespace runs collapsed to one space, trimmed, and cut to
    /// <see cref="TitleFromQuestionMaxLength"/> characters plus 「…」. Always a valid
    /// <see cref="ChatThread"/> title (1–<see cref="ChatThread.TitleMaxLength"/> characters).
    /// </summary>
    public static string TitleFromQuestion(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var text = Whitespace().Replace(question, " ").Trim();
        if (text.Length == 0)
        {
            return DefaultThreadTitle;
        }

        return text.Length > TitleFromQuestionMaxLength ? text[..TitleFromQuestionMaxLength] + "…" : text;
    }

    /// <summary>
    /// The title <paramref name="thread"/> should get now that <paramref name="question"/> is
    /// being asked in it, or <see langword="null"/> to keep its title. Only a thread with no
    /// message yet that still has <see cref="DefaultThreadTitle"/> (a blank thread from
    /// <c>POST .../conversations</c>) is retitled; a thread the account renamed keeps its name.
    /// The mock tracks "renamed" explicitly (<c>titleSource</c>); the backend has no such column,
    /// so a blank thread the account renamed to exactly 「新的對話」 would also be retitled.
    /// </summary>
    public static string? TitleForFirstQuestion(ChatThread thread, string question)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return thread.MessageCount == 0 && thread.Title == DefaultThreadTitle ? TitleFromQuestion(question) : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
