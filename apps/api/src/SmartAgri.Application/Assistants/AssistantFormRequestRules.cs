using System.Text.RegularExpressions;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>The form target an assistant's settings ask for: the connected database its form
/// requests fill in (<see langword="null"/> for none) and the purpose members are told.</summary>
public sealed record AssistantFormTarget(Guid? DatabaseId, string Purpose);

/// <summary>
/// The server-defined form tool of an assistant (M4 #148) and the rules around it: which connected
/// database collects forms (the settings' <c>rules.dataWriteDatabaseId</c> /
/// <c>rules.dataWritePurpose</c>), when a conversation turn becomes a form request, and the fixed
/// texts of the two form replies.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tool.</b> There is exactly one tool, <see cref="ToolName"/>, and its only parameter is
/// <i>which</i> form: chosen from the databases the server says the assistant may use right now,
/// never typed by a model. The fields, purpose, recipient and readers all come from the server
/// (the database's current form version and <c>DatabaseSubmissionService</c>'s terms); nothing a
/// model produces becomes a field, a query or SQL (assistant-access ADR).
/// </para>
/// <para>
/// <b>When it is called.</b> In this ticket the orchestration layer decides deterministically, with
/// no model call: the assistant has a form target and the question asks to fill something in
/// (<see cref="AsksForForm"/>). This works the same with the <c>Fake</c> and real models and keeps
/// the model out of the decision; #149 may let the model select this tool instead, through the same
/// definition, without changing what the tool does or how it is authorized.
/// </para>
/// </remarks>
public static partial class AssistantFormRequestRules
{
    /// <summary>The tool's name, as it would be offered to a model.</summary>
    public const string ToolName = "request_database_form";

    /// <summary>The tool's description, as it would be offered to a model.</summary>
    public const string ToolDescription =
        "Ask the member to fill in the assistant's connected form. The server supplies the form, its purpose, " +
        "recipient and readers; the member reviews and explicitly consents before anything is recorded.";

    public const string DataWritePurposeField = "dataWritePurpose";

    /// <summary>The settings field the frontend shows connection and target errors under (it has no
    /// field of its own for the target).</summary>
    public const string SourcesField = "sources";

    public const string TargetNotConnectedMessage = "只能把資料寫入已連接到這個助理、而且你仍可使用的資料庫。";

    public const string TargetInvalidMessage = "找不到要寫入的資料庫，請重新選擇。";

    /// <summary>The mock's message, word for word (<c>validateAssistantSettings</c>).</summary>
    public const string PurposeRequiredMessage = "寫入資料庫前，請說明收集目的，使用者同意前會看到這段說明。";

    public static readonly string PurposeTooLongMessage =
        $"收集目的最多 {AssistantDatabase.CollectionPurposeMaxLength} 個字。";

    /// <summary>The form request's text (the mock's <c>chat-order-issue</c> reply, made general).</summary>
    public const string FormRequestText = "可以的，請在下方表單填寫資料。送出前會先讓你確認資料會交給誰、做什麼用途。";

    /// <summary>Words that mean "I want to fill something in / report something".</summary>
    private static readonly string[] FormIntentWords = ["填寫", "填表", "表單", "回報", "登記", "報名", "留下資料", "提交資料"];

    /// <summary>Whether <paramref name="question"/> asks to fill something in; whitespace is
    /// ignored, like the mock's matcher.</summary>
    public static bool AsksForForm(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var normalized = Whitespace().Replace(question, string.Empty);
        return FormIntentWords.Any(word => normalized.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>The receipt message's text. Never contains an answer: the entries are shown from
    /// the submission itself, for its submitter only.</summary>
    public static string ReceiptText(string recipient, string receiptNumber) =>
        $"已送出。資料只會交給 {recipient}，回執編號 {receiptNumber}。";

    /// <summary>
    /// The form target after a settings update. <paramref name="requestedDatabaseId"/>:
    /// <see langword="null"/> keeps the current target, <c>""</c> clears it, otherwise it must be
    /// the id of one of <paramref name="usableConnectedIds"/>. <paramref name="requestedPurpose"/>:
    /// <see langword="null"/> keeps the current purpose. A target needs a non-blank purpose of at
    /// most <see cref="AssistantDatabase.CollectionPurposeMaxLength"/> characters; no target means
    /// no purpose.
    /// </summary>
    public static ValidationResult<AssistantFormTarget> ForUpdate(
        AssistantFormTarget current,
        string? requestedDatabaseId,
        string? requestedPurpose,
        IReadOnlyCollection<Guid> usableConnectedIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(usableConnectedIds);

        Guid? target = current.DatabaseId;
        if (requestedDatabaseId is not null)
        {
            if (requestedDatabaseId.Length == 0)
            {
                target = null;
            }
            else if (!Guid.TryParse(requestedDatabaseId, out var parsed))
            {
                return ValidationResult<AssistantFormTarget>.Invalid(SourcesField, TargetInvalidMessage);
            }
            else if (!usableConnectedIds.Contains(parsed))
            {
                return ValidationResult<AssistantFormTarget>.Invalid(SourcesField, TargetNotConnectedMessage);
            }
            else
            {
                target = parsed;
            }
        }

        if (target is null)
        {
            return ValidationResult<AssistantFormTarget>.Valid(new AssistantFormTarget(null, string.Empty));
        }

        var purpose = (requestedPurpose ?? current.Purpose).Trim();
        if (purpose.Length == 0)
        {
            return ValidationResult<AssistantFormTarget>.Invalid(DataWritePurposeField, PurposeRequiredMessage);
        }

        if (purpose.Length > AssistantDatabase.CollectionPurposeMaxLength)
        {
            return ValidationResult<AssistantFormTarget>.Invalid(DataWritePurposeField, PurposeTooLongMessage);
        }

        return ValidationResult<AssistantFormTarget>.Valid(new AssistantFormTarget(target, purpose));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
