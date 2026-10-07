using AGUI.Abstractions;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Assistants;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Cases;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;

namespace SmartAgri.Api.Chat;

/// <summary>What a proposal sees of the request it may answer (M7-8).</summary>
/// <param name="Assistant">The assistant the caller may use (<c>ChatEndpoints.FindUsableAsync</c>, already checked).</param>
/// <param name="AskerId">The account asking.</param>
/// <param name="Question">The validated question.</param>
/// <param name="Trigger">How proposals are triggered: <c>Chat:FormRequests:Trigger</c>, shared by every proposal
/// (the setting keeps its name so a deployed <c>CHAT_FORM_REQUEST_TRIGGER</c> stays valid).</param>
internal sealed record ChatProposalContext(Assistant Assistant, Guid AskerId, string Question, ChatFormRequestTrigger Trigger);

/// <summary>
/// One kind of proposal the chat run may answer with instead of the answer pipeline (M7-8): a
/// server-built reply the member confirms, never something the model writes. The form request
/// (<see cref="ChatFormRequestProposal"/>) is the first; M7-9 adds the case proposal.
/// </summary>
internal interface IChatProposal
{
    /// <summary>
    /// Step 1, before the stream starts: what this request may be offered, authorized again on every
    /// request (nothing cached), or <see langword="null"/> when there is nothing to offer — then the
    /// proposal is skipped without a model call or an event.
    /// </summary>
    Task<ChatProposalCandidate?> FindAsync(ChatProposalContext context, CancellationToken cancellationToken);
}

/// <summary>What one proposal found for this request, deciding in the stream (step 2).</summary>
internal abstract class ChatProposalCandidate
{
    /// <summary>Events sent right before <see cref="DecideAsync"/> (the form's model mode sends
    /// <c>CUSTOM smartagri.form-check</c>, #171); none by default.</summary>
    public virtual IEnumerable<BaseEvent> BeforeDecision => [];

    /// <summary>
    /// Step 2: decides by the trigger (keyword or model) whether this request gets the proposal.
    /// <see langword="null"/> means no — the stage moves on to the next proposal. A model failure
    /// must not throw: each proposal falls back to its keyword rule.
    /// </summary>
    public abstract Task<ChatProposalReply?> DecideAsync(CancellationToken cancellationToken);
}

/// <summary>A proposal that was decided: step 3, the reply as it is streamed, saved and shown.</summary>
internal abstract class ChatProposalReply
{
    /// <summary>The reply's text: the one <c>TEXT_MESSAGE_CONTENT</c> and the saved message's text.</summary>
    public abstract string Text { get; }

    /// <summary>The message to save in a kept conversation (the run adds and saves it, in one
    /// <c>SaveChanges</c>, before <c>smartagri.reply</c>).</summary>
    public abstract ChatMessage CreateMessage(ChatThread thread, DateTimeOffset now);

    /// <summary>The saved message exactly as <c>GET chat</c> returns it.</summary>
    public abstract ChatMessageView ToView(ChatMessage saved);

    /// <summary>The reply of a conversation that is not kept, shaped exactly like a saved one;
    /// <paramref name="now"/> is already truncated to microseconds.</summary>
    public abstract ChatMessageView Transient(DateTimeOffset now);
}

/// <summary>
/// The chat run's proposal stage (M7-8; M7 plan §3 H, decision L). Precedence: a database query
/// (<see cref="ChatDatabaseQueries"/>, before this stage and not part of it) → the proposals here,
/// in order (form request, then — M7-9 — case) → the answer pipeline. At most one proposal per
/// reply: the first whose decision is yes ends the run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The combined selection (#286).</b> In model mode, when step 1 found <b>both</b> the form (a form target
/// the assistant may use now) and a case (types it may propose now, for an eligible asker in a kept
/// conversation), the two candidates are replaced by one, <see cref="CombinedSelection"/>: a single model call
/// offering both tools (<see cref="ChatProposalSelectionTool"/>, purpose <c>proposal-selection</c>), so the form
/// no longer takes every question a case type fits (#257: 16 of 16 missed). With only one of the two, its
/// single call is made exactly as before (#164's <c>form-request</c>, #254's <c>case-proposal</c>). Keyword
/// mode is untouched: each proposal's keyword rule, in order.
/// </para>
/// <para>
/// <b><c>smartagri.form-check</c> (#171)</b> keeps its name, value and place: one event right before the
/// selection call whenever that call offers the form — so the combined call sends it exactly where the form
/// call did, and a case-only call still sends none. The client (admin's conversation, <c>libs/chat</c>) shows
/// a generic 「助理正在整理你的問題」 on it and hides it at the first answer text or the reply, which a case
/// proposal also sends first, so nothing changes in the frontend. Renaming it would break deployed widgets
/// and #171's tests for no gain.
/// </para>
/// </remarks>
internal sealed class ChatProposalStage
{
    private readonly IReadOnlyList<IChatProposal> _proposals;
    private readonly IOptions<ChatFormRequestOptions> _triggerOptions;
    private readonly ChatProposalSelectionTool _selectionTool;

    public ChatProposalStage(
        ChatFormRequestProposal formRequest,
        ChatCaseProposal caseProposal,
        ChatProposalSelectionTool selectionTool,
        IOptions<ChatFormRequestOptions> triggerOptions)
    {
        // Decision L: the form request first, then the case proposal (M7-9).
        _proposals = [formRequest, caseProposal];
        _triggerOptions = triggerOptions;
        _selectionTool = selectionTool;
    }

    /// <summary>Step 1 for every proposal, in precedence order: the candidates found for this request.</summary>
    public async Task<IReadOnlyList<ChatProposalCandidate>> FindAsync(
        Assistant assistant, Guid askerId, string question, CancellationToken cancellationToken)
    {
        var trigger = _triggerOptions.Value.TriggerKind == ChatFormRequestTrigger.Model
            ? ChatFormRequestTrigger.Model
            : ChatFormRequestTrigger.Keyword;
        var context = new ChatProposalContext(assistant, askerId, question, trigger);

        List<ChatProposalCandidate> candidates = [];
        foreach (var proposal in _proposals)
        {
            if (await proposal.FindAsync(context, cancellationToken) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        // #286: the form and the case both found in model mode decide in one call, in the form's place.
        var form = candidates.OfType<ChatFormRequestProposal.ModelSelection>().FirstOrDefault();
        var caseSelection = candidates.OfType<ChatCaseProposal.ModelSelection>().FirstOrDefault();
        if (form is not null && caseSelection is not null)
        {
            var index = candidates.IndexOf(form);
            candidates.Remove(caseSelection);
            candidates[index] = new CombinedSelection(_selectionTool, context, form.Offered, caseSelection.Proposable, caseSelection.Offers);
        }

        return candidates;
    }

    /// <summary>
    /// Model mode with both the form and a case to offer (#286): one call, after <c>smartagri.form-check</c>
    /// (the call offers the form, #171); the form, the case proposal or nothing (the answer pipeline).
    /// </summary>
    private sealed class CombinedSelection(
        ChatProposalSelectionTool tool,
        ChatProposalContext context,
        ChatFormRequestView offeredForm,
        IReadOnlyList<ProposableCaseType> proposable,
        IReadOnlyList<CaseProposalOffer> offers) : ChatProposalCandidate
    {
        public override IEnumerable<BaseEvent> BeforeDecision => ChatFormRequestProposal.FormCheck;

        public override async Task<ChatProposalReply?> DecideAsync(CancellationToken cancellationToken)
        {
            var selection = await tool.SelectAsync(context.Assistant, offeredForm, offers, context.Question, context.AskerId, cancellationToken);
            return selection switch
            {
                { Form: { } form } => ChatFormRequestProposal.Reply(form),
                { Case: { } draft } => ChatCaseProposal.Reply(draft, proposable),
                _ => null,
            };
        }
    }
}

/// <summary>
/// The form request as a proposal (M4 #148 keyword gate, #164 model selection; M7-8 moved it here
/// unchanged). Keyword mode decides in step 1 — a question the gate does not match is not even
/// looked up; model mode finds the one form target the assistant may use now and lets
/// <see cref="ChatFormRequestTool"/> decide in the stream, right after <c>smartagri.form-check</c>.
/// </summary>
internal sealed class ChatFormRequestProposal(AssistantFormRequests formRequests, ChatFormRequestTool formTool) : IChatProposal
{
    /// <summary><c>CUSTOM smartagri.form-check</c> (#171), sent right before a selection call that offers the form.</summary>
    internal static IEnumerable<BaseEvent> FormCheck =>
        [new CustomEvent { Name = ChatRunEndpoints.FormCheckEventName, Value = ChatRunEndpoints.EmptyObject }];

    public async Task<ChatProposalCandidate?> FindAsync(ChatProposalContext context, CancellationToken cancellationToken)
    {
        if (context.Trigger == ChatFormRequestTrigger.Model)
        {
            return await formRequests.FormRequestAsync(context.Assistant, null, cancellationToken) is { } offered
                ? new ModelSelection(formTool, context, offered)
                : null;
        }

        if (!AssistantFormRequestRules.AsksForForm(context.Question))
        {
            return null;
        }

        return await formRequests.FormRequestAsync(context.Assistant, null, cancellationToken) is { } form
            ? new Decided(new FormReply(form))
            : null;
    }

    /// <summary>Keyword mode: already decided in step 1, no model call.</summary>
    private sealed class Decided(ChatProposalReply reply) : ChatProposalCandidate
    {
        public override Task<ChatProposalReply?> DecideAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ChatProposalReply?>(reply);
    }

    /// <summary>The reply of a decided form request (also the combined selection's form reply, #286).</summary>
    internal static ChatProposalReply Reply(ChatFormRequestView form) => new FormReply(form);

    /// <summary>Model mode (#164): one selection call; the server re-authorizes and builds the form. When a
    /// case may be proposed too, <see cref="ChatProposalStage"/> folds it into the combined selection (#286).</summary>
    internal sealed class ModelSelection(ChatFormRequestTool tool, ChatProposalContext context, ChatFormRequestView offered)
        : ChatProposalCandidate
    {
        /// <summary>The form authorized for this request.</summary>
        public ChatFormRequestView Offered => offered;

        /// <summary>#171: the client shows its "checking" state only on this event, and hides it at the
        /// first answer text or the form request (also after a keyword fallback).</summary>
        public override IEnumerable<BaseEvent> BeforeDecision => FormCheck;

        public override async Task<ChatProposalReply?> DecideAsync(CancellationToken cancellationToken) =>
            await tool.SelectAsync(context.Assistant, offered, context.Question, context.AskerId, cancellationToken) is { } form
                ? new FormReply(form)
                : null;
    }

    /// <summary>The form tool's reply: fixed text and the server's form. Only the database id is
    /// saved; the form is re-read and re-authorized whenever it is shown.</summary>
    private sealed class FormReply(ChatFormRequestView form) : ChatProposalReply
    {
        public override string Text => AssistantFormRequestRules.FormRequestText;

        public override ChatMessage CreateMessage(ChatThread thread, DateTimeOffset now) =>
            ChatMessage.FormRequest(thread, Text, form.Id, now);

        public override ChatMessageView ToView(ChatMessage saved) => ChatEndpoints.ToMessageView(saved, [], form);

        public override ChatMessageView Transient(DateTimeOffset now) =>
            new(
                Guid.CreateVersion7(),
                "assistant",
                null,
                new ChatReplyView("form-request", Text, [], null, [], form, null, null, null),
                now);
    }
}
