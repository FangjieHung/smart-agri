using System.Diagnostics;
using Microsoft.Extensions.AI;
using SmartAgri.Api.Assistants;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Observability;

namespace SmartAgri.Api.Chat;

/// <summary>
/// The model-chosen form tool (M4 #164, <c>Chat:FormRequests:Trigger = Model</c>): the model decides
/// whether a question should get the assistant's form; the server decides which forms exist and
/// builds the form. Follows #149's design (<see cref="ChatDatabaseQueries"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Order of checks</b> (every request, nothing cached), the same as #148's: the caller may use the
/// assistant (<c>ChatEndpoints.FindUsableAsync</c>, before this runs) → the form target the assistant
/// may use right now (<see cref="AssistantFormRequests.FormRequestAsync"/>; none means the tool is not
/// offered and no model is called, exactly like a keyword-mode question without a target) → the
/// model is offered only that form (<see cref="AssistantFormRequestRules.Declaration"/>) and calls at
/// most one tool → the call must be <c>request_database_form</c> with the offered id
/// (<see cref="AssistantFormRequestRules.ParseCall"/>) → the form is authorized again for that id
/// (<see cref="AssistantFormRequests.FormRequestAsync"/>) and built by the server. Any other tool,
/// id (another organization's, a made-up one) or a target that went away in between is simply no
/// form: the question is answered as usual, and nothing says whether such a form exists.
/// </para>
/// <para>
/// <b>Usage.</b> The selection call goes through the recording chat client like every model call
/// (<see cref="ModelInvocationPurpose.FormRequest"/>, attributed to the asker and assistant; no
/// content). The decision is one <see cref="ActivityName"/> span with its outcome, never the question.
/// </para>
/// <para>
/// <b>Failure.</b> When the model call fails, the keyword gate decides instead
/// (<see cref="AssistantFormRequestRules.AsksForForm"/>, i.e. #148's behavior), so a model outage
/// never breaks the reply or hides a form a member asked for in so many words.
/// </para>
/// </remarks>
public sealed class ChatFormRequestTool
{
    public const string ActivityName = "smartagri.chat.form_request";

    private readonly AssistantFormRequests _formRequests;
    private readonly IChatClient _chat;
    private readonly ILogger<ChatFormRequestTool> _logger;

    public ChatFormRequestTool(AssistantFormRequests formRequests, IChatClient chat, ILogger<ChatFormRequestTool> logger)
    {
        _formRequests = formRequests;
        _chat = chat;
        _logger = logger;
    }

    /// <summary>
    /// The form to show for <paramref name="question"/>, or <see langword="null"/> for none.
    /// <paramref name="offered"/> is the form <see cref="AssistantFormRequests.FormRequestAsync"/>
    /// authorized for this request. Never throws for a model failure (keyword fallback).
    /// </summary>
    public async Task<ChatFormRequestView?> SelectAsync(
        Assistant assistant, ChatFormRequestView offered, string question, Guid askerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(offered);
        using var activity = SmartAgriActivitySource.Instance.StartActivity(ActivityName);

        IReadOnlyList<AssistantFormToolOffer> offers = [new AssistantFormToolOffer(offered.Id, offered.Title, offered.Consent.Purpose)];
        var options = new ModelInvocationAttribution(ModelInvocationPurpose.FormRequest, askerId, assistant.Id).ToChatOptions();
        options.Tools = [AssistantFormRequestRules.Declaration(offers)];
        options.ToolMode = ChatToolMode.Auto;
        options.AllowMultipleToolCalls = false;

        ChatResponse response;
        try
        {
            response = await _chat.GetResponseAsync(AssistantFormRequestRules.SelectionPrompt(question), options, cancellationToken);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(exception, "A conversation's form-tool selection failed; the keyword gate decides instead.");
            activity?.SetStatus(ActivityStatusCode.Error);
            var fallback = AssistantFormRequestRules.AsksForForm(question) ? offered : null;
            activity?.SetTag("smartagri.form_request.status", fallback is null ? "fallback-none" : "fallback-keyword");
            return fallback;
        }

        var (match, databaseId) = AssistantFormRequestRules.ParseCall(response, offers);
        if (match != AssistantFormToolCallMatch.Matched)
        {
            activity?.SetTag("smartagri.form_request.status", match == AssistantFormToolCallMatch.NoCall ? "no-tool" : "rejected");
            return null;
        }

        // Re-authorized for the id the model named, the same check a review or submission makes.
        var form = await _formRequests.FormRequestAsync(assistant, databaseId, cancellationToken);
        activity?.SetTag("smartagri.form_request.status", form is null ? "rejected" : "requested");
        return form;
    }
}
