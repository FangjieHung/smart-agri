using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>The form target an assistant's settings ask for: the connected database its form
/// requests fill in (<see langword="null"/> for none) and the purpose members are told.</summary>
public sealed record AssistantFormTarget(Guid? DatabaseId, string Purpose);

/// <summary>A form the server offers the model in one conversation turn (M4 #164): the assistant's
/// form target as authorized for this request — its database id (the only value the model may send
/// back), title and collection purpose, all the server's.</summary>
public sealed record AssistantFormToolOffer(Guid DatabaseId, string Title, string Purpose);

/// <summary>How a model's reply to the form tool matched what was offered (M4 #164).</summary>
public enum AssistantFormToolCallMatch
{
    /// <summary>The model called no tool: no form.</summary>
    NoCall,

    /// <summary><see cref="AssistantFormRequestRules.ToolName"/> with an offered form id.</summary>
    Matched,

    /// <summary>Any other tool name, or the form tool with a missing, malformed or unoffered id
    /// (another organization's, a made-up one, a disconnected one) — all alike, no form.</summary>
    Rejected,
}

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
/// <b>When it is called.</b> By default (<c>Chat:FormRequests:Trigger = Keyword</c>, #148) the
/// orchestration layer decides deterministically, with no model call: the assistant has a form
/// target and the question asks to fill something in (<see cref="AsksForForm"/>). With
/// <c>Trigger = Model</c> (#164) the model is offered this tool (<see cref="Declaration"/>, its
/// <see cref="FormIdParameter"/> an <c>enum</c> of the forms the server lists for this request) and
/// decides whether to call it; the server matches the call back (<see cref="ParseCall"/>) and
/// re-authorizes the form exactly as #148 does. The tool does the same thing either way.
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

    /// <summary>The tool's only parameter: which offered form (the form target's database id).</summary>
    public const string FormIdParameter = "databaseId";

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

    /// <summary>The form tool as offered to a model (M4 #164): one parameter,
    /// <see cref="FormIdParameter"/>, an <c>enum</c> of <paramref name="offers"/>' ids (at least one);
    /// <c>additionalProperties</c> is false. Nothing in it carries fields, SQL or free text.</summary>
    public static AIFunctionDeclaration Declaration(IReadOnlyList<AssistantFormToolOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        if (offers.Count == 0)
        {
            throw new ArgumentException("At least one form must be offered.", nameof(offers));
        }

        var forms = string.Join("；", offers.Select(offer => $"{offer.DatabaseId}＝「{offer.Title}」（收集目的：{offer.Purpose}）"));
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [FormIdParameter] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "要請使用者填寫的表單 id。",
                    ["enum"] = new JsonArray([.. offers.Select(offer => JsonValue.Create(offer.DatabaseId.ToString()))]),
                },
            },
            ["required"] = new JsonArray(JsonValue.Create(FormIdParameter)),
            ["additionalProperties"] = false,
        };

        return AIFunctionFactory.CreateDeclaration(
            ToolName,
            $"{ToolDescription} 只能請求這些表單：{forms}。",
            JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString()));
    }

    /// <summary>The messages of the form-tool selection call (M4 #164): the role (call the tool only
    /// when the member wants to report, register or hand in information through the offered form;
    /// never for a question about it) and the question. Only the offered forms' titles and purposes go
    /// with the call (in the tool's definition) — never a record, a field value or another form.</summary>
    public static IReadOnlyList<ChatMessage> SelectionPrompt(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        const string system =
            "你是農業與小型企業助理的表單工具選擇器。只有在使用者想要回報、通報、登記、記錄或提交資料，" +
            "而且這些資料符合工具中列出的表單用途時，才呼叫 request_database_form，並且只能使用工具定義列出的表單 id。" +
            "如果使用者只是在詢問知識、詢問表單或資料的規則、查詢過去的紀錄，或想要的資料與表單用途無關，請不要呼叫工具，直接回覆「不需要表單」。" +
            "你不能產生表單欄位、SQL 或任何未定義的參數。";
        return [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, question.Trim())];
    }

    /// <summary>Matches a model's reply back to <paramref name="offers"/>: the first function call,
    /// if any, must be <see cref="ToolName"/> with an offered <see cref="FormIdParameter"/>.</summary>
    public static (AssistantFormToolCallMatch Match, Guid? DatabaseId) ParseCall(
        ChatResponse response, IReadOnlyList<AssistantFormToolOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(offers);
        var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().FirstOrDefault();
        if (call is null)
        {
            return (AssistantFormToolCallMatch.NoCall, null);
        }

        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal)
            || call.Arguments is null
            || !call.Arguments.TryGetValue(FormIdParameter, out var raw)
            || !Guid.TryParse(AsText(raw), out var databaseId)
            || offers.All(offer => offer.DatabaseId != databaseId))
        {
            return (AssistantFormToolCallMatch.Rejected, null);
        }

        return (AssistantFormToolCallMatch.Matched, databaseId);
    }

    private static string? AsText(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonValue node when node.TryGetValue<string>(out var text) => text,
        _ => null,
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
