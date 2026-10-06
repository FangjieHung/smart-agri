using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Assistants;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Chat;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Line;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Line;

/// <summary>
/// Answers a LINE user's text question (M5b plan §3 D–G, issue #232), in the webhook processor's scope
/// acting for the assistant's organization — so the organization's own chat model (M6 #238), knowledge
/// and token usage apply.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per question</b>, in order:
/// <list type="number">
/// <item>In a group or room, only a message that mentions the bot (<c>isSelf</c>) is a question; the
/// bot's mention is removed from it. Anything else, and a blank question, gets nothing at all.</item>
/// <item>The serving state is derived again (the delivery's may be stale by a few questions): not
/// <see cref="ChannelServingState.Serving"/> — paused, acceptance not passed, monthly tokens used up… —
/// replies 「<see cref="LineAnswerMessages.NotServingReply"/>」 and never calls the model.</item>
/// <item><see cref="LineQuestionRateLimiter"/>: refused → 「<see cref="LineAnswerMessages.RateLimitedReply"/>」
/// once per partition and window, then silence; never the model.</item>
/// <item>A question over <see cref="ChatRunRules.QuestionMaxLength"/> characters gets
/// <see cref="ChatRunRules.QuestionTooLongMessage"/>.</item>
/// <item>One-to-one only, the 「輸入中」 animation (LINE refuses it in groups).</item>
/// <item><see cref="GroundedAnswerService.AnswerAsync"/> with the chat's remembered turns
/// (<see cref="ILineConversationHistory"/>), purpose <see cref="ModelInvocationPurpose.LineAnswer"/>
/// (outcome channel <c>line</c>), no account and no LINE id anywhere in the database.</item>
/// <item><see cref="LineAnswerMessages.Build"/>, then delivery: within
/// <see cref="PublicLineOptions.ReplyDeadlineSeconds"/> of receiving the event, a reply (the token is
/// used once). Past the deadline, or when LINE refuses the reply token, a one-to-one answer is pushed and
/// counted on the channel's 「本月補送次數」; a group's or room's is dropped (decision A: a push costs one
/// message per member). Any other LINE failure, <c>429</c> included, is logged and dropped — never
/// retried.</item>
/// <item>Once delivered, the question and the answer are remembered for the chat's next question.</item>
/// </list>
/// </para>
/// <para>
/// Nothing is written but the model call and the answer outcome (by the pipeline) and, on a push, the
/// channel's fallback counter. Logs carry the assistant id, outcomes, statuses and LINE's
/// <c>x-line-request-id</c> — never a token, a LINE id, a question or an answer.
/// </para>
/// </remarks>
internal sealed class LineQuestionHandler : ILineQuestionHandler
{
    private readonly AppDbContext _dbContext;
    private readonly OrganizationTokenUsage _tokenUsage;
    private readonly IAnswerKnowledgeBases _knowledgeBases;
    private readonly GroundedAnswerService _answers;
    private readonly ILineMessagingClient _line;
    private readonly ILineConversationHistory _history;
    private readonly LineQuestionRateLimiter _rateLimiter;
    private readonly LineAnswerMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly PublicLineOptions _options;
    private readonly ILogger<LineQuestionHandler> _logger;

    public LineQuestionHandler(
        AppDbContext dbContext,
        OrganizationTokenUsage tokenUsage,
        IAnswerKnowledgeBases knowledgeBases,
        GroundedAnswerService answers,
        ILineMessagingClient line,
        ILineConversationHistory history,
        LineQuestionRateLimiter rateLimiter,
        LineAnswerMetrics metrics,
        TimeProvider clock,
        IOptions<PublicChannelsOptions> options,
        ILogger<LineQuestionHandler> logger)
    {
        _dbContext = dbContext;
        _tokenUsage = tokenUsage;
        _knowledgeBases = knowledgeBases;
        _answers = answers;
        _line = line;
        _history = history;
        _rateLimiter = rateLimiter;
        _metrics = metrics;
        _clock = clock;
        _options = options.Value.Line;
        _logger = logger;
    }

    public async Task HandleAsync(LineQuestionContext context, CancellationToken cancellationToken)
    {
        var lineEvent = context.Event;
        var oneToOne = lineEvent.Source.IsOneToOne;
        if (lineEvent.Message is not { IsText: true } message || (!oneToOne && !message.MentionsSelf))
        {
            return;
        }

        var question = message.QuestionText;
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        var assistant = context.Assistant;
        var (serving, _) = await AssistantLineChannelEndpoints.ServingStateAsync(_dbContext, assistant, _tokenUsage, cancellationToken);
        if (serving != ChannelServingState.Serving)
        {
            await ReplyTextAsync(context, LineAnswerMessages.NotServingReply, "not-serving", cancellationToken);
            return;
        }

        using var permit = _rateLimiter.TryAcquire(assistant.Id, context.Chat.ChatId, oneToOne);
        if (!permit.Acquired)
        {
            _metrics.RecordRateLimited(permit.RefusedBy!.Value);
            _logger.LogInformation(
                "A LINE question for assistant {AssistantId} was refused by the {Limit} rate limit (notice sent: {Notified}).",
                assistant.Id, permit.RefusedBy, permit.Notify);
            if (permit.Notify)
            {
                await ReplyTextAsync(context, LineAnswerMessages.RateLimitedReply, "rate-limited", cancellationToken);
            }

            return;
        }

        var validated = ChatRunRules.ValidateQuestion(question);
        if (!validated.IsValid)
        {
            await ReplyTextAsync(context, ChatRunRules.QuestionTooLongMessage, "question-too-long", cancellationToken);
            return;
        }

        if (oneToOne)
        {
            await StartLoadingAsync(context, cancellationToken);
        }

        var history = _history.Get(context.Chat).Select(entry => entry.Turn).ToList();
        GroundedReply reply;
        try
        {
            var connected = await _knowledgeBases.ConnectedToAsync(assistant.Id, cancellationToken);
            var result = await _answers.AnswerAsync(
                new GroundedAnswerRequest(
                    GroundedAnswerProfile.For(assistant, connected),
                    validated.Value,
                    history,
                    AccountId: null,
                    assistant.Id,
                    ModelInvocationPurpose.LineAnswer),
                cancellationToken);
            reply = result.Reply;
        }
        catch (Exception exception) when (exception is ChatGenerationException or KnowledgeEmbeddingException)
        {
            _logger.LogWarning(
                exception.InnerException, "A LINE question for assistant {AssistantId} could not be answered: {Issue}", assistant.Id, exception.Message);
            await ReplyTextAsync(context, LineAnswerMessages.FailedReply, "answer-failed", cancellationToken);
            return;
        }

        var messages = LineAnswerMessages.Build(reply, assistant.ShowCitations);
        if (await DeliverAsync(context, messages, cancellationToken))
        {
            _history.Append(context.Chat, message.Id, new ConversationTurn(ConversationAuthor.Account, validated.Value));
            _history.Append(context.Chat, message.Id, new ConversationTurn(ConversationAuthor.Assistant, reply.Text));
        }
    }

    /// <summary>Sends the answer by reply or, past the deadline, by push (one-to-one only); whether it
    /// was delivered.</summary>
    private async Task<bool> DeliverAsync(LineQuestionContext context, IReadOnlyList<JsonObject> messages, CancellationToken cancellationToken)
    {
        var assistantId = context.Assistant.Id;
        var oneToOne = context.Event.Source.IsOneToOne;
        var elapsed = _clock.GetUtcNow() - context.ReceivedAt;
        if (elapsed < _options.ReplyDeadline && context.Event.ReplyToken is { Length: > 0 } replyToken)
        {
            var replied = await _line.ReplyAsync(context.AccessToken, replyToken, messages, cancellationToken);
            if (replied.Succeeded)
            {
                _metrics.RecordAnswer(_clock.GetUtcNow() - context.ReceivedAt, "reply", oneToOne);
                return true;
            }

            if (!IsReplyTokenRefused(replied))
            {
                LogDropped(assistantId, "reply", replied);
                _metrics.RecordAnswer(_clock.GetUtcNow() - context.ReceivedAt, "dropped", oneToOne);
                return false;
            }

            _logger.LogInformation(
                "LINE refused the reply token of an answer for assistant {AssistantId} (HTTP {StatusCode}, x-line-request-id {RequestId}).",
                assistantId, replied.StatusCode, replied.RequestId);
        }

        if (!oneToOne || context.Event.Source.UserId is not { Length: > 0 } userId)
        {
            // Decision A: a push to a group or room costs one message per member; the answer is dropped.
            _logger.LogInformation(
                "An answer for assistant {AssistantId} missed its reply deadline in a group or room after {ElapsedSeconds:F1} s and is not pushed.",
                assistantId, elapsed.TotalSeconds);
            _metrics.RecordAnswer(_clock.GetUtcNow() - context.ReceivedAt, "dropped", oneToOne);
            return false;
        }

        var pushed = await _line.PushAsync(context.AccessToken, userId, messages, retryKey: null, cancellationToken);
        if (!pushed.Succeeded)
        {
            LogDropped(assistantId, "push", pushed);
            _metrics.RecordAnswer(_clock.GetUtcNow() - context.ReceivedAt, "dropped", oneToOne);
            return false;
        }

        _metrics.RecordPushFallback();
        _metrics.RecordAnswer(_clock.GetUtcNow() - context.ReceivedAt, "push", oneToOne);
        await RecordPushFallbackAsync(context, cancellationToken);
        return true;
    }

    /// <summary>
    /// Counts one push on the channel row (「本月補送次數」) — <see cref="AssistantLineChannel.RecordPushFallback"/>'s
    /// rule (same month: +1; a new month: 1) as a single <c>UPDATE</c>, so answers pushed at the same time
    /// all count. A failure is logged: the answer has been sent.
    /// </summary>
    private async Task RecordPushFallbackAsync(LineQuestionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var month = (await _tokenUsage.GetAsync(context.Assistant.OrganizationId, cancellationToken)).Month;
            await _dbContext.AssistantLineChannels
                .Where(channel => channel.AssistantId == context.Assistant.Id)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(channel => channel.PushFallbackCount, channel => channel.PushFallbackMonth == month ? channel.PushFallbackCount + 1 : 1)
                        .SetProperty(channel => channel.PushFallbackMonth, month),
                    cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "The push fallback of assistant {AssistantId} could not be counted.", context.Assistant.Id);
        }
    }

    /// <summary>LINE's answer to a reply token it no longer accepts (used, expired, invalid):
    /// <c>400</c> 「Invalid reply token」.</summary>
    private static bool IsReplyTokenRefused(LineApiResult result) =>
        result is { Outcome: LineApiOutcome.HttpError, StatusCode: 400 };

    private async Task StartLoadingAsync(LineQuestionContext context, CancellationToken cancellationToken)
    {
        if (context.Event.Source.UserId is not { Length: > 0 } userId)
        {
            return;
        }

        // 5 to 60 seconds in steps of 5: until the reply deadline (the answer's arrival ends it sooner).
        var seconds = Math.Clamp((_options.ReplyDeadlineSeconds + 4) / 5 * 5, 5, 60);
        var started = await _line.StartLoadingAsync(context.AccessToken, userId, seconds, cancellationToken);
        if (!started.Succeeded)
        {
            _logger.LogInformation(
                "LINE refused the loading animation for assistant {AssistantId}: {Outcome} (HTTP {StatusCode}, x-line-request-id {RequestId}).",
                context.Assistant.Id, started.Outcome, started.StatusCode, started.RequestId);
        }
    }

    /// <summary>Replies once with a fixed <paramref name="text"/>; a failure is logged and dropped.</summary>
    private async Task ReplyTextAsync(LineQuestionContext context, string text, string what, CancellationToken cancellationToken)
    {
        if (context.Event.ReplyToken is not { Length: > 0 } replyToken)
        {
            return;
        }

        var result = await _line.ReplyAsync(context.AccessToken, replyToken, [LineMessages.Text(text)], cancellationToken);
        if (!result.Succeeded)
        {
            LogDropped(context.Assistant.Id, what, result);
        }
    }

    private void LogDropped(Guid assistantId, string what, LineApiResult result) =>
        _logger.LogWarning(
            "LINE refused a {What} for assistant {AssistantId}: {Outcome} (HTTP {StatusCode}, x-line-request-id {RequestId}); dropped, not retried.",
            what, assistantId, result.Outcome, result.StatusCode, result.RequestId);
}
