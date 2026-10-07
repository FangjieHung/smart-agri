using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Assistants;
using SmartAgri.Application.Line;
using SmartAgri.Application.Observability;
using SmartAgri.Application.Organizations;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Line;

/// <summary>
/// Handles one webhook delivery's events (M5b plan §3 D's table), in a scope acting for the
/// assistant's organization (<see cref="LineWebhookProcessor"/>). The assistant, its LINE channel and
/// the serving state are read once per delivery; nothing is written to the database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per event:</b>
/// <list type="bullet">
/// <item><c>unfollow</c>, <c>leave</c>: the chat's remembered turns are forgotten
/// (<see cref="ILineConversationHistory.Clear"/>) — always, whatever the channel's state;</item>
/// <item><c>unsend</c>: that message is forgotten (<see cref="ILineConversationHistory.RemoveMessage"/>) — always;</item>
/// <item><c>follow</c>, <c>join</c>: the channel's welcome message (decision D) as a reply — only while
/// the channel is <see cref="ChannelServingState.Serving"/>: a draft channel answers nothing (it is still
/// being tested), and a paused or suspended one does not greet people it would not answer;</item>
/// <item>a <c>message</c> that is not text: in a one-to-one chat, while serving, the channel's
/// <see cref="AssistantLineChannel.NonTextReply"/> (#291; by default
/// 「<see cref="AssistantLineChannel.DefaultNonTextReply"/>」) as a reply — no model call, no token usage;
/// in a group or room nothing;</item>
/// <item>a text <c>message</c> of a published or paused channel (whatever its serving state; never a
/// draft's): handed to <see cref="ILineQuestionHandler"/>, which owns its reply token;</item>
/// <item>anything else (<c>postback</c>, <c>memberJoined</c>, <c>messageEdited</c>, unknown types):
/// ignored.</item>
/// </list>
/// An event in <c>standby</c> mode never causes a reply. Each reply token is used at most once; a LINE
/// failure is logged (event type, outcome, status, <c>x-line-request-id</c> — never a token or a body)
/// and dropped, never retried (the token is single-use and short-lived).
/// </para>
/// </remarks>
internal sealed class LineWebhookEventHandler
{
    private readonly AppDbContext _dbContext;
    private readonly OrganizationTokenUsage _tokenUsage;
    private readonly ISecretProtector _secretProtector;
    private readonly ILineMessagingClient _line;
    private readonly ILineConversationHistory _history;
    private readonly ILineQuestionHandler _questions;
    private readonly ILogger<LineWebhookEventHandler> _logger;

    public LineWebhookEventHandler(
        AppDbContext dbContext,
        OrganizationTokenUsage tokenUsage,
        ISecretProtector secretProtector,
        ILineMessagingClient line,
        ILineConversationHistory history,
        ILineQuestionHandler questions,
        ILogger<LineWebhookEventHandler> logger)
    {
        _dbContext = dbContext;
        _tokenUsage = tokenUsage;
        _secretProtector = secretProtector;
        _line = line;
        _history = history;
        _questions = questions;
        _logger = logger;
    }

    public async Task HandleAsync(LineWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var assistant = await _dbContext.Assistants
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == delivery.AssistantId, cancellationToken);
        if (assistant is null)
        {
            return;
        }

        var (serving, channel) = await AssistantLineChannelEndpoints.ServingStateAsync(_dbContext, assistant, _tokenUsage, cancellationToken);
        if (channel is null)
        {
            return;
        }

        var state = new DeliveryState(assistant, channel, serving, delivery.ReceivedAt);
        foreach (var lineEvent in delivery.Events)
        {
            try
            {
                await HandleEventAsync(state, lineEvent, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Only types: whatever failed was handling a LINE user's message, and a message can quote it.
                _logger.LogError(
                    "A LINE {EventType} event for assistant {AssistantId} failed and was dropped: {Failure}",
                    lineEvent.Type, assistant.Id, ExceptionSummary.Of(exception));
            }
        }
    }

    private async Task HandleEventAsync(DeliveryState state, LineWebhookEvent lineEvent, CancellationToken cancellationToken)
    {
        if (lineEvent.ChatId is not { Length: > 0 } chatId)
        {
            return;
        }

        var chat = new LineChatKey(state.Assistant.Id, chatId);
        switch (lineEvent.Type)
        {
            case LineWebhookEvent.Types.Unfollow:
            case LineWebhookEvent.Types.Leave:
                _history.Clear(chat);
                return;

            case LineWebhookEvent.Types.Unsend:
                if (lineEvent.UnsentMessageId is { Length: > 0 } messageId)
                {
                    _history.RemoveMessage(chat, messageId);
                }

                return;
        }

        if (lineEvent.IsStandby)
        {
            return;
        }

        switch (lineEvent.Type)
        {
            case LineWebhookEvent.Types.Follow:
            case LineWebhookEvent.Types.Join:
                if (state.Serving == ChannelServingState.Serving)
                {
                    await ReplyAsync(state, lineEvent, state.Channel.WelcomeMessage, cancellationToken);
                }

                return;

            case LineWebhookEvent.Types.Message when lineEvent.Message is { IsText: true }:
                if (state.Channel.State is LineChannelState.Published or LineChannelState.Paused
                    && AccessToken(state) is { } accessToken)
                {
                    await _questions.HandleAsync(
                        new LineQuestionContext(
                            state.Assistant, state.Channel, state.Serving, lineEvent, chat, accessToken, state.ReceivedAt),
                        cancellationToken);
                }

                return;

            case LineWebhookEvent.Types.Message when lineEvent.Message is not null:
                if (lineEvent.Source.IsOneToOne && state.Serving == ChannelServingState.Serving)
                {
                    await ReplyAsync(state, lineEvent, state.Channel.NonTextReply, cancellationToken);
                }

                return;
        }
    }

    /// <summary>Replies once with <paramref name="text"/>; a failure is logged and dropped.</summary>
    private async Task ReplyAsync(DeliveryState state, LineWebhookEvent lineEvent, string text, CancellationToken cancellationToken)
    {
        if (lineEvent.ReplyToken is not { Length: > 0 } replyToken || AccessToken(state) is not { } accessToken)
        {
            return;
        }

        var result = await _line.ReplyAsync(accessToken, replyToken, [LineMessages.Text(text)], cancellationToken);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "LINE refused the reply to a {EventType} event for assistant {AssistantId}: {Outcome} (HTTP {StatusCode}, x-line-request-id {RequestId}); dropped.",
                lineEvent.Type, state.Assistant.Id, result.Outcome, result.StatusCode, result.RequestId);
        }
    }

    /// <summary>The channel's access token, decrypted at most once per delivery; <see langword="null"/>
    /// (logged once) when it no longer decrypts — nothing can be sent until it is entered again.</summary>
    private string? AccessToken(DeliveryState state)
    {
        if (!state.AccessTokenRead)
        {
            state.AccessTokenRead = true;
            try
            {
                state.AccessToken = _secretProtector.Unprotect(AssistantLineChannel.AccessTokenPurpose, state.Channel.AccessToken);
            }
            catch (SecretUnprotectException)
            {
                _logger.LogWarning(
                    "The LINE access token of assistant {AssistantId} cannot be decrypted (key ring changed?); nothing is sent until it is entered again.",
                    state.Assistant.Id);
            }
        }

        return state.AccessToken;
    }

    private sealed class DeliveryState(Assistant assistant, AssistantLineChannel channel, ChannelServingState serving, DateTimeOffset receivedAt)
    {
        public Assistant Assistant { get; } = assistant;

        public AssistantLineChannel Channel { get; } = channel;

        public ChannelServingState Serving { get; } = serving;

        public DateTimeOffset ReceivedAt { get; } = receivedAt;

        public bool AccessTokenRead { get; set; }

        public string? AccessToken { get; set; }
    }
}
