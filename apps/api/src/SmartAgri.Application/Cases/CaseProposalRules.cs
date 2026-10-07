using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Application.Cases;

/// <summary>A case type the server offers in one conversation turn (M7-9, #254): one of the
/// assistant's proposable types that is active right now — its id (the only value the model may send
/// back), name and description, all the server's.</summary>
public sealed record CaseProposalOffer(Guid TypeId, string Name, string Description);

/// <summary>What a proposal decision chose: the offered type and the draft title and description,
/// already trimmed and truncated to the case's limits.</summary>
public sealed record CaseProposalDraft(CaseProposalOffer Offer, string Title, string Description);

/// <summary>How a model's reply to the case tool matched what was offered (M7-9).</summary>
public enum CaseProposalCallMatch
{
    /// <summary>The model called no tool: no proposal.</summary>
    NoCall,

    /// <summary><see cref="CaseProposalRules.ToolName"/> with an offered type id.</summary>
    Matched,

    /// <summary>Any other tool, or the case tool with a missing, malformed or unoffered type id.</summary>
    Rejected,

    /// <summary><see cref="CaseProposalRules.NoMatchToolName"/> (#297): the model said, explicitly, that no
    /// offered type fits. No proposal, exactly like <see cref="NoCall"/> — never a failure, so never the keyword
    /// fallback.</summary>
    NoMatch,
}

/// <summary>
/// When a conversation turn gets a case proposal, and its draft (M7 plan §3 H, decisions L and T; issue
/// #254). Shared by both triggers of <c>Chat:FormRequests:Trigger</c>:
/// <list type="bullet">
/// <item><b>Keyword</b> (decision T): the question names a case intent (<see cref="AsksForCase"/>) and
/// either contains an offered type's name or the assistant offers exactly one type; the title is the
/// question's first <see cref="Case.TitleMaxLength"/> characters and the description is left for the
/// asker to write (no conversation text is copied beyond the title).</item>
/// <item><b>Model</b>: one selection call (<see cref="Declarations"/>, <see cref="SelectionPrompt"/>) when the
/// assistant has no form to offer, or the case tool next to the form tool in the one combined selection call
/// (<c>ProposalSelectionRules</c>, #286); <see cref="ParseCall"/> accepts only an offered type id and
/// truncates the model's draft to the case's limits. Both calls also offer the explicit 「都不符合」 tool
/// (<see cref="NoMatchToolName"/>, #297).</item>
/// </list>
/// The asker always confirms (and may edit) before any case exists.
/// </summary>
public static partial class CaseProposalRules
{
    /// <summary>The case tool's name (model mode).</summary>
    public const string ToolName = "propose_case";

    public const string TypeIdParameter = "caseTypeId";

    public const string TitleParameter = "title";

    public const string DescriptionParameter = "description";

    /// <summary>
    /// The explicit 「都不符合」 tool (#297), offered next to <see cref="ToolName"/> in the case selection call and
    /// in the combined selection call: calling it means no proposal. #293's evaluation found that a model offered
    /// <see cref="ToolName"/> alone nearly always calls it — the type descriptions only changed which type it
    /// picked (crop pests 6 of 6 proposed as 設備報修, even with 「不包括作物病蟲害」 written in) — while the same
    /// questions offered a second tool (the form) went to that tool 6 of 6. So declining gets a tool of its own,
    /// not just a sentence in the prompt. It takes no parameters (nothing to validate, nothing kept).
    /// </summary>
    public const string NoMatchToolName = "no_matching_type";

    /// <summary>The model's draft description is cut to this many characters (the asker may write more).</summary>
    public const int DraftDescriptionMaxLength = 1000;

    public const string ToolDescription =
        "當使用者描述一件需要專人後續處理的事（例如設備報修、申請、請款、退貨、派人或安排處理），而且符合下列某個案件類型時，提議建立一件案件。";

    /// <summary>Words that mean "this needs someone to handle it" (decision T).</summary>
    private static readonly string[] CaseIntentWords =
        ["報修", "維修", "修理", "叫修", "申請", "請款", "退貨", "派人", "安排", "開案", "立案"];

    /// <summary>The reply's text: 「這件事可以開一件「設備報修」案件，請確認內容。」</summary>
    public static string ProposalText(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return $"這件事可以開一件「{typeName}」案件，請確認內容。";
    }

    /// <summary>Whether <paramref name="question"/> names a case intent; whitespace is ignored.</summary>
    public static bool AsksForCase(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var normalized = Normalize(question);
        return CaseIntentWords.Any(word => normalized.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// Keyword mode (decision T): the proposal for <paramref name="question"/>, or <see langword="null"/>.
    /// A question naming several offered types takes the longest name (「冷藏設備報修」 over 「報修」);
    /// a tie keeps the offers' order.
    /// </summary>
    public static CaseProposalDraft? KeywordProposal(string question, IReadOnlyList<CaseProposalOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(offers);
        if (offers.Count == 0 || !AsksForCase(question))
        {
            return null;
        }

        var normalized = Normalize(question);
        var named = offers
            .Where(offer => Normalize(offer.Name) is { Length: > 0 } name && normalized.Contains(name, StringComparison.Ordinal))
            .OrderByDescending(offer => Normalize(offer.Name).Length)
            .FirstOrDefault();
        var offer = named ?? (offers.Count == 1 ? offers[0] : null);
        return offer is null ? null : new CaseProposalDraft(offer, TitleFromQuestion(question), string.Empty);
    }

    /// <summary>The question's first <see cref="Case.TitleMaxLength"/> characters, trimmed, with runs of
    /// whitespace collapsed; never splits a surrogate pair.</summary>
    public static string TitleFromQuestion(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return Truncate(Whitespace().Replace(question.Trim(), " "), Case.TitleMaxLength);
    }

    /// <summary>The tools of the case selection call (<see cref="SelectionPrompt"/>): <see cref="Declaration"/>,
    /// then <see cref="NoMatchDeclaration"/> (#297).</summary>
    public static IList<AITool> Declarations(IReadOnlyList<CaseProposalOffer> offers) =>
        [Declaration(offers), NoMatchDeclaration(NoMatchToolDescription)];

    /// <summary><see cref="NoMatchToolName"/>'s description in the case selection call.</summary>
    public const string NoMatchToolDescription =
        "當使用者說的事不符合 propose_case 列出的任何案件類型說明（包括說明寫明不包括的事），或屬於不需要開案的情況時，呼叫這個工具，表示不提議案件。不需要任何參數。";

    /// <summary><see cref="NoMatchToolName"/> with <paramref name="description"/> (the combined selection
    /// describes it for both the form and the case types): an object with no properties, <c>additionalProperties</c> false.</summary>
    public static AIFunctionDeclaration NoMatchDeclaration(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["additionalProperties"] = false,
        };
        return AIFunctionFactory.CreateDeclaration(NoMatchToolName, description, JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString()));
    }

    /// <summary>The case tool as offered to a model: <see cref="TypeIdParameter"/> is an <c>enum</c> of the
    /// offered ids (at least one), with each type's name and description in the tool's description; a
    /// short title and description draft; <c>additionalProperties</c> is false.</summary>
    public static AIFunctionDeclaration Declaration(IReadOnlyList<CaseProposalOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        if (offers.Count == 0)
        {
            throw new ArgumentException("At least one case type must be offered.", nameof(offers));
        }

        var types = string.Join("；", offers.Select(offer =>
            offer.Description.Length > 0 ? $"{offer.TypeId}＝「{offer.Name}」（{offer.Description}）" : $"{offer.TypeId}＝「{offer.Name}」"));
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [TypeIdParameter] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "要提議的案件類型 id。",
                    ["enum"] = new JsonArray([.. offers.Select(offer => JsonValue.Create(offer.TypeId.ToString()))]),
                },
                [TitleParameter] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = $"案件標題草稿，一句話、{Case.TitleMaxLength} 字以內，只寫事情本身。",
                },
                [DescriptionParameter] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = $"案件說明草稿，{DraftDescriptionMaxLength} 字以內，只根據使用者這次的問題。",
                },
            },
            ["required"] = new JsonArray(JsonValue.Create(TypeIdParameter), JsonValue.Create(TitleParameter), JsonValue.Create(DescriptionParameter)),
            ["additionalProperties"] = false,
        };

        return AIFunctionFactory.CreateDeclaration(
            ToolName,
            $"{ToolDescription} 只能使用這些類型：{types}。",
            JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString()));
    }

    /// <summary>
    /// When <see cref="ToolName"/> must <b>not</b> be called even though the question sounds like a case
    /// (issue #286, from #257's evaluation): asking how an existing case or request is doing, cancelling one
    /// already sent, asking for statistics, or only complaining or describing something without asking
    /// anyone to handle it; and something no offered type covers (crop pests, say) is never forced into the
    /// closest type. Part of <see cref="SelectionPrompt"/> and of the combined selection's prompt
    /// (<c>ProposalSelectionRules.SelectionPrompt</c>).
    /// </summary>
    public const string SelectionBoundaries =
        "以下情況不要呼叫 propose_case：詢問既有案件或申請的進度或結果、想取消已經送出的申請、詢問統計數字或筆數、" +
        "只是抱怨或陳述狀況而沒有要求任何人處理。" +
        "只有當這件事符合某個類型的說明時才提議；不屬於任何列出類型的事（例如作物病蟲害），不要套用最接近的類型。";

    /// <summary>
    /// What still counts as "needs handling" now that declining has a tool of its own (#297): with
    /// <see cref="NoMatchToolName"/> offered, the first evaluation run read <see cref="SelectionBoundaries"/>'
    /// 「只是陳述狀況」 so strictly that a bare report of something broken (no one named to handle it) was declined
    /// too. Something an offered type's description covers is proposed even when the asker does not say who should
    /// handle it — the asker confirms before any case exists; declining is for what no description covers. Part of
    /// <see cref="SelectionPrompt"/> and of the combined selection's prompt.
    /// </summary>
    public const string ProposeWhenItFits =
        "使用者說某樣東西壞了、出了問題，或表示要報修、要申請，而且符合某個類型的說明時，即使沒有說要誰來處理，也算需要處理，請提議。" +
        NoMatchToolName + " 只用在這件事不屬於任何類型的說明，或屬於上面列出不要提議的情況。";

    /// <summary>The messages of the case selection call: the role (propose only for something someone has
    /// to act on, matching an offered type; never for a question or a request already answered), the
    /// boundaries (<see cref="SelectionBoundaries"/>, #286), the explicit way to decline
    /// (<see cref="NoMatchToolName"/>, #297) and the question. Only the offered types go with the call (in the
    /// tool's definition).</summary>
    public static IReadOnlyList<ChatMessage> SelectionPrompt(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        const string system =
            "你是農業與小型企業助理的開案工具選擇器。只有在使用者描述一件需要組織內的人後續處理、追蹤的事情，" +
            "而且這件事符合工具中列出的某個案件類型時，才呼叫 propose_case，並且只能使用工具定義列出的類型 id；" +
            "標題與說明只根據使用者這次說的內容草擬，使用者確認前不會建立任何案件。" +
            SelectionBoundaries +
            "請先判斷這件事是否落在某個類型說明的範圍內；如果不符合任何類型的說明（包括說明寫明不包括的事）、屬於上面不要提議的情況，" +
            "或使用者只是在詢問知識、規則、進度，請呼叫 " + NoMatchToolName + "，不要呼叫 propose_case。" +
            ProposeWhenItFits +
            "每次只呼叫 propose_case 或 " + NoMatchToolName + " 其中一個。" +
            "你不能變更任何案件的狀態，也不能產生未定義的參數。";
        return [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, question.Trim())];
    }

    /// <summary>
    /// Matches a model's reply back to <paramref name="offers"/>: the first function call, if any, must
    /// be <see cref="ToolName"/> with an offered <see cref="TypeIdParameter"/>, or <see cref="NoMatchToolName"/>
    /// (<see cref="CaseProposalCallMatch.NoMatch"/>, whatever its arguments: it proposes nothing). The title is trimmed and
    /// cut to <see cref="Case.TitleMaxLength"/> (an empty one becomes <see cref="TitleFromQuestion"/>);
    /// the description to <see cref="DraftDescriptionMaxLength"/>.
    /// </summary>
    public static (CaseProposalCallMatch Match, CaseProposalDraft? Draft) ParseCall(
        ChatResponse response, IReadOnlyList<CaseProposalOffer> offers, string question)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(offers);
        ArgumentNullException.ThrowIfNull(question);
        var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().FirstOrDefault();
        if (call is null)
        {
            return (CaseProposalCallMatch.NoCall, null);
        }

        if (string.Equals(call.Name, NoMatchToolName, StringComparison.Ordinal))
        {
            return (CaseProposalCallMatch.NoMatch, null);
        }

        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal)
            || call.Arguments is null
            || !call.Arguments.TryGetValue(TypeIdParameter, out var raw)
            || !Guid.TryParse(AsText(raw), out var typeId)
            || offers.FirstOrDefault(offer => offer.TypeId == typeId) is not { } offer)
        {
            return (CaseProposalCallMatch.Rejected, null);
        }

        var title = Truncate(Whitespace().Replace((Argument(call, TitleParameter) ?? string.Empty).Trim(), " "), Case.TitleMaxLength);
        var description = Truncate((Argument(call, DescriptionParameter) ?? string.Empty).Trim(), DraftDescriptionMaxLength);
        return (CaseProposalCallMatch.Matched, new CaseProposalDraft(
            offer, title.Length > 0 ? title : TitleFromQuestion(question), description));
    }

    private static string? Argument(FunctionCallContent call, string name) =>
        call.Arguments is not null && call.Arguments.TryGetValue(name, out var value) ? AsText(value) : null;

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..length].TrimEnd();
    }

    private static string Normalize(string text) => Whitespace().Replace(text, string.Empty);

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
