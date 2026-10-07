using Microsoft.Extensions.AI;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Cases;

namespace SmartAgri.Application.Chat;

/// <summary>How a model's reply to the combined selection call matched what was offered (#286).</summary>
public enum ProposalSelectionCallMatch
{
    /// <summary>The model called no tool: no proposal, the answer pipeline answers.</summary>
    NoCall,

    /// <summary><see cref="AssistantFormRequestRules.ToolName"/> with the offered form id.</summary>
    Form,

    /// <summary><see cref="CaseProposalRules.ToolName"/> with an offered case type id.</summary>
    Case,

    /// <summary>Any other tool, or either tool with a missing, malformed or unoffered id: no proposal.</summary>
    Rejected,
}

/// <summary>What the combined selection call chose: the form's database id, or the case draft, or neither.</summary>
public sealed record ProposalSelectionCall(ProposalSelectionCallMatch Match, Guid? FormDatabaseId, CaseProposalDraft? CaseDraft)
{
    public static readonly ProposalSelectionCall NoCall = new(ProposalSelectionCallMatch.NoCall, null, null);

    public static readonly ProposalSelectionCall Rejected = new(ProposalSelectionCallMatch.Rejected, null, null);
}

/// <summary>
/// The combined proposal selection (issue #286, decision L's alternative, chosen by the owner on
/// 2026-10-07): with <c>Chat:FormRequests:Trigger = Model</c>, when one request may be offered both the
/// assistant's form and at least one case type, <b>one</b> model call is offered both tools — the form tool
/// (<see cref="AssistantFormRequestRules.Declaration"/>) and the case tool
/// (<see cref="CaseProposalRules.Declaration"/>), unchanged — and calls at most one of them, or none.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> #257's evaluation found that a separate form call first took every question that should have
/// proposed a case (16 of 16 missed), because the form prompt alone cannot know a case type would fit better.
/// Offering both in one call lets the model choose between them. With only one of the two, the single call
/// of #164 (<see cref="AssistantFormRequestRules.SelectionPrompt"/>) or #254
/// (<see cref="CaseProposalRules.SelectionPrompt"/>) is unchanged.
/// </para>
/// <para>
/// <b>Precedence</b> stays decision L's (query → form → case → answer; at most one proposal per reply): the
/// prompt tells the model to prefer the form when both fit equally, the keyword fallback checks the form gate
/// first, and <see cref="ParseCall"/> reads only the first call.
/// </para>
/// <para>
/// <b>The server still decides.</b> <see cref="ParseCall"/> accepts only an offered form id or case type id
/// (each tool's own <c>ParseCall</c>); the caller re-authorizes the form for that id and builds both replies.
/// </para>
/// </remarks>
public static class ProposalSelectionRules
{
    /// <summary>Both tools, the form's first (the order of decision L).</summary>
    public static IList<AITool> Declarations(IReadOnlyList<AssistantFormToolOffer> formOffers, IReadOnlyList<CaseProposalOffer> caseOffers)
    {
        ArgumentNullException.ThrowIfNull(formOffers);
        ArgumentNullException.ThrowIfNull(caseOffers);
        return [AssistantFormRequestRules.Declaration(formOffers), CaseProposalRules.Declaration(caseOffers)];
    }

    /// <summary>
    /// The messages of the combined call: the role of each tool (the form records information a member
    /// reports; a case is something a person in the organization has to act on), how to choose between them
    /// (the form when both fit equally), the case tool's boundaries (<see cref="CaseProposalRules.SelectionBoundaries"/>),
    /// when to call neither, and the question. Only the offered form and types go with the call (in the tools' definitions).
    /// </summary>
    public static IReadOnlyList<ChatMessage> SelectionPrompt(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        const string system =
            "你是農業與小型企業助理的提議工具選擇器。這次有兩個工具，最多只能呼叫其中一個，也可以都不呼叫：" +
            "request_database_form 用在使用者想要回報、通報、登記、記錄或提交資料，而且這些資料符合工具中列出的表單用途時；" +
            "propose_case 用在使用者描述一件需要組織內的人後續處理、追蹤的事情（例如派人維修、核准採購、處理退換貨），" +
            "而且這件事符合工具中列出的某個案件類型時，標題與說明只根據使用者這次說的內容草擬，使用者確認前不會建立任何案件。" +
            "請依使用者要的是什麼來選：要留下紀錄或資料，用表單；要有人處理、核准或退換，用案件；兩者都同樣符合時，用表單。" +
            "只能使用工具定義列出的表單 id 與類型 id。" +
            CaseProposalRules.SelectionBoundaries +
            "如果使用者只是在詢問知識、詢問表單、資料或案件的規則、查詢過去的紀錄或進度，或想做的事與表單用途和案件類型都無關，" +
            "請不要呼叫工具，直接回覆「不需要表單或案件」。" +
            "你不能變更任何案件的狀態，也不能產生表單欄位、SQL 或任何未定義的參數。";
        return [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, question.Trim())];
    }

    /// <summary>
    /// Matches a model's reply back to what was offered: the first function call, if any, decides — the form
    /// tool through <see cref="AssistantFormRequestRules.ParseCall"/>, the case tool through
    /// <see cref="CaseProposalRules.ParseCall"/> (truncating its draft); any other tool, or an unoffered id, is
    /// <see cref="ProposalSelectionCallMatch.Rejected"/>.
    /// </summary>
    public static ProposalSelectionCall ParseCall(
        ChatResponse response, IReadOnlyList<AssistantFormToolOffer> formOffers, IReadOnlyList<CaseProposalOffer> caseOffers, string question)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(formOffers);
        ArgumentNullException.ThrowIfNull(caseOffers);
        ArgumentNullException.ThrowIfNull(question);
        var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().FirstOrDefault();
        if (call is null)
        {
            return ProposalSelectionCall.NoCall;
        }

        if (string.Equals(call.Name, AssistantFormRequestRules.ToolName, StringComparison.Ordinal))
        {
            var (match, databaseId) = AssistantFormRequestRules.ParseCall(response, formOffers);
            return match == AssistantFormToolCallMatch.Matched
                ? new ProposalSelectionCall(ProposalSelectionCallMatch.Form, databaseId, null)
                : ProposalSelectionCall.Rejected;
        }

        if (string.Equals(call.Name, CaseProposalRules.ToolName, StringComparison.Ordinal))
        {
            var (match, draft) = CaseProposalRules.ParseCall(response, caseOffers, question);
            return match == CaseProposalCallMatch.Matched
                ? new ProposalSelectionCall(ProposalSelectionCallMatch.Case, null, draft)
                : ProposalSelectionCall.Rejected;
        }

        return ProposalSelectionCall.Rejected;
    }

    /// <summary>
    /// The keyword fallback when the combined call fails (each tool's own fallback, in decision L's order):
    /// the form gate (<see cref="AssistantFormRequestRules.AsksForForm"/>) first, then the case rule
    /// (<see cref="CaseProposalRules.KeywordProposal"/>), else nothing.
    /// </summary>
    public static ProposalSelectionCall KeywordFallback(
        string question, IReadOnlyList<AssistantFormToolOffer> formOffers, IReadOnlyList<CaseProposalOffer> caseOffers)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(formOffers);
        ArgumentNullException.ThrowIfNull(caseOffers);
        if (formOffers.Count > 0 && AssistantFormRequestRules.AsksForForm(question))
        {
            return new ProposalSelectionCall(ProposalSelectionCallMatch.Form, formOffers[0].DatabaseId, null);
        }

        return CaseProposalRules.KeywordProposal(question, caseOffers) is { } draft
            ? new ProposalSelectionCall(ProposalSelectionCallMatch.Case, null, draft)
            : ProposalSelectionCall.NoCall;
    }
}
