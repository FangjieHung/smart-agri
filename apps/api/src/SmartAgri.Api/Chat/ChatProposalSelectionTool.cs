using System.Diagnostics;
using Microsoft.Extensions.AI;
using SmartAgri.Api.Assistants;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Cases;
using SmartAgri.Application.Chat;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Observability;

namespace SmartAgri.Api.Chat;

/// <summary>What the combined selection decided: the form (already re-authorized), or the case draft, or
/// neither (both <see langword="null"/>).</summary>
public sealed record ChatProposalSelection(ChatFormRequestView? Form, CaseProposalDraft? Case)
{
    public static readonly ChatProposalSelection None = new(null, null);
}

/// <summary>
/// The combined form-or-case selection (issue #286; <see cref="ProposalSelectionRules"/>): with
/// <c>Chat:FormRequests:Trigger = Model</c>, when this request may be offered both the assistant's form
/// target and at least one case type, <b>one</b> model call offers both tools and the model calls one of
/// them or neither. Follows <see cref="ChatFormRequestTool"/> and <see cref="ChatCaseProposalTool"/>, which
/// still make the single call when only one of the two is offered (unchanged). The call also offers
/// <see cref="CaseProposalRules.NoMatchToolName"/> (#297): the model choosing it is neither the form nor a case
/// (the answer pipeline answers) — the same call, purpose, usage and <c>smartagri.form-check</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usage.</b> The call goes through the recording chat client like every model call, as
/// <see cref="ModelInvocationPurpose.ProposalSelection"/> (attributed to the asker and assistant; no content;
/// counted toward the monthly limit, <c>OrganizationTokenUsageRules</c>). A purpose of its own, not
/// <c>form-request</c> or <c>case-proposal</c>: one call decides both, so either name would misreport it,
/// and the single-tool calls keep their purposes and their meaning. The decision is one
/// <see cref="ActivityName"/> span with its outcome, never the question.
/// </para>
/// <para>
/// <b>Server checks.</b> Only an offered form id or case type id is accepted
/// (<see cref="ProposalSelectionRules.ParseCall"/>); a form is then authorized again for that id
/// (<see cref="AssistantFormRequests.FormRequestAsync"/>, exactly as #148/#164) — gone in between is no
/// proposal at all, not the case instead; a case type must be one of <paramref name="caseOffers"/>, which
/// step 1 re-read for this request (the confirm endpoint checks it again).
/// </para>
/// <para>
/// <b>Failure.</b> When the model call fails, the keywords decide in decision L's order
/// (<see cref="ProposalSelectionRules.KeywordFallback"/>: the form gate, then the case rule), so a model
/// outage never breaks the reply.
/// </para>
/// </remarks>
public sealed class ChatProposalSelectionTool
{
    public const string ActivityName = "smartagri.chat.proposal_selection";

    private readonly AssistantFormRequests _formRequests;
    private readonly IChatClient _chat;
    private readonly ILogger<ChatProposalSelectionTool> _logger;

    public ChatProposalSelectionTool(AssistantFormRequests formRequests, IChatClient chat, ILogger<ChatProposalSelectionTool> logger)
    {
        _formRequests = formRequests;
        _chat = chat;
        _logger = logger;
    }

    /// <summary>
    /// The proposal for <paramref name="question"/>: <paramref name="offeredForm"/> is the form
    /// <see cref="AssistantFormRequests.FormRequestAsync"/> authorized for this request, and
    /// <paramref name="caseOffers"/> (at least one) the types the assistant may propose now. Never throws for a
    /// model failure (keyword fallback).
    /// </summary>
    public async Task<ChatProposalSelection> SelectAsync(
        Assistant assistant,
        ChatFormRequestView offeredForm,
        IReadOnlyList<CaseProposalOffer> caseOffers,
        string question,
        Guid askerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(offeredForm);
        ArgumentNullException.ThrowIfNull(caseOffers);
        using var activity = SmartAgriActivitySource.Instance.StartActivity(ActivityName);

        IReadOnlyList<AssistantFormToolOffer> formOffers =
            [new AssistantFormToolOffer(offeredForm.Id, offeredForm.Title, offeredForm.Consent.Purpose)];
        var options = new ModelInvocationAttribution(ModelInvocationPurpose.ProposalSelection, askerId, assistant.Id).ToChatOptions();
        options.Tools = ProposalSelectionRules.Declarations(formOffers, caseOffers);
        options.ToolMode = ChatToolMode.Auto;
        options.AllowMultipleToolCalls = false;

        ProposalSelectionCall call;
        string status;
        try
        {
            var response = await _chat.GetResponseAsync(ProposalSelectionRules.SelectionPrompt(question), options, cancellationToken);
            call = ProposalSelectionRules.ParseCall(response, formOffers, caseOffers, question);
            status = call.Match switch
            {
                ProposalSelectionCallMatch.Form => "form",
                ProposalSelectionCallMatch.Case => "case",
                ProposalSelectionCallMatch.NoCall => "no-tool",
                ProposalSelectionCallMatch.NoMatch => "no-match",
                _ => "rejected",
            };
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(exception, "A conversation's combined proposal selection failed; the keywords decide instead.");
            activity?.SetStatus(ActivityStatusCode.Error);
            var fallback = ProposalSelectionRules.KeywordFallback(question, formOffers, caseOffers);
            activity?.SetTag("smartagri.proposal_selection.status", fallback.Match switch
            {
                ProposalSelectionCallMatch.Form => "fallback-form",
                ProposalSelectionCallMatch.Case => "fallback-case",
                _ => "fallback-none",
            });

            // Like ChatFormRequestTool's fallback: the form authorized for this request, no second read.
            return fallback.Match switch
            {
                ProposalSelectionCallMatch.Form => new ChatProposalSelection(offeredForm, null),
                ProposalSelectionCallMatch.Case => new ChatProposalSelection(null, fallback.CaseDraft),
                _ => ChatProposalSelection.None,
            };
        }

        ChatProposalSelection selection;
        switch (call.Match)
        {
            case ProposalSelectionCallMatch.Form:
                // Re-authorized for the id the model named, the same check a review or submission makes.
                var form = await _formRequests.FormRequestAsync(assistant, call.FormDatabaseId, cancellationToken);
                selection = form is null ? ChatProposalSelection.None : new ChatProposalSelection(form, null);
                if (form is null)
                {
                    status = "rejected";
                }

                break;
            case ProposalSelectionCallMatch.Case:
                selection = new ChatProposalSelection(null, call.CaseDraft);
                break;
            default:
                selection = ChatProposalSelection.None;
                break;
        }

        activity?.SetTag("smartagri.proposal_selection.status", status);
        return selection;
    }
}
