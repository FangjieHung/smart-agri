using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Formatting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Ai;
using SmartAgri.Api.Assistants;
using SmartAgri.Api.Chat;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Knowledge;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Chat;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// <c>POST /api/v1/public/assistants/{id}/chat/runs</c> (M5a plan §3 D, issue #196): answers one
/// website visitor's question as an AG-UI event stream — the member endpoint's request and response
/// format (<see cref="ChatRunEndpoints"/>), knowledge-base answers only, nothing kept.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only <see cref="GroundedAnswerService"/>.</b> This handler composes the answer pipeline and
/// nothing else: the form tool, the database query tools and handoffs are not on this path at all
/// (not switched off by a flag), so neither keyword nor model form triggers, nor statistics
/// questions, ever offer the model a tool here. An architecture test keeps it that way.
/// </para>
/// <para>
/// <b>Refusals before the stream</b>, in this order: <c>401</c> (no, an invalid or an expired
/// visitor token; bodiless); <c>403 public-assistant</c> — the token's assistant is not the route's,
/// the assistant no longer exists, or its website channel is not
/// <see cref="WebsiteServingState.Serving"/> right now (re-derived on every question; byte-identical
/// to the session endpoint's refusal); <c>422</c> (no question, blank, or over
/// <see cref="ChatRunRules.QuestionMaxLength"/> characters, as for members); <c>503
/// chat-not-configured</c> / <c>503 embedding-not-configured</c>; <c>409 chat-run-in-progress</c>
/// (this visitor already has a reply being generated).
/// </para>
/// <para>
/// <b>Events</b>: <c>RUN_STARTED</c> → <c>TEXT_MESSAGE_START</c> → <c>TEXT_MESSAGE_CONTENT</c>… →
/// <c>TEXT_MESSAGE_END</c> → <c>CUSTOM smartagri.reply</c> (a <see cref="ChatMessageView"/>, never
/// saved; citations removed when the assistant does not show them) → <c>RUN_FINISHED</c>, or
/// <c>RUN_ERROR</c> once streaming has started. Never <c>smartagri.thread</c> or
/// <c>smartagri.form-check</c>.
/// </para>
/// <para>
/// <b>Nothing is kept</b>, whatever the assistant's <c>keepConversations</c>: no thread, no message.
/// The earlier turns are the request's own <c>messages</c> (at most 20, text only, as for an unsaved
/// member conversation); citations only ever come from this run's retrieval. The model call is
/// recorded as <see cref="ModelInvocationPurpose.PublicAnswer"/> with no account, and the outcome
/// as <c>website</c> — no content, no visitor.
/// </para>
/// </remarks>
public static class VisitorChatRunEndpoints
{
    private const string RunInProgressMessage = "上一個問題還在回覆中，請等回覆完成後再送出。";
    private const string InternalErrorMessage = "回覆途中發生錯誤，請稍後重試。";

    internal static async Task<IResult> RunAsync(
        string id,
        HttpContext httpContext,
        AppDbContext dbContext,
        OrganizationTokenUsage tokenUsage,
        IAnswerKnowledgeBases knowledgeBases,
        GroundedAnswerService answers,
        [FromKeyedServices(VisitorAuthentication.RunLocksKey)] ChatRunLocks locks,
        ChatClientProvider chatProvider,
        EmbeddingProvider embeddingProvider,
        TimeProvider clock,
        IOptions<JsonOptions> jsonOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (VisitorAuthentication.Read(httpContext.User) is not { } visitor)
        {
            return ApiErrors.Unauthorized();
        }

        // A token is good for the one assistant it was issued for.
        if (!Guid.TryParse(id, out var assistantId) || assistantId != visitor.AssistantId)
        {
            return VisitorSessionEndpoints.Refused();
        }

        var input = await ChatRunEndpoints.ReadInputAsync(httpContext.Request, cancellationToken);

        // Under the token's organization (ClaimsOrganizationContext reads it from the visitor claim).
        var assistant = await dbContext.Assistants
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == assistantId, cancellationToken);
        if (assistant is null)
        {
            return VisitorSessionEndpoints.Refused();
        }

        var (serving, _) = await AssistantWebsiteChannelEndpoints.ServingStateAsync(dbContext, assistant, tokenUsage, cancellationToken);
        if (serving != WebsiteServingState.Serving)
        {
            return VisitorSessionEndpoints.Refused();
        }

        var (questionText, _, earlierMessages) = ChatRunEndpoints.SplitQuestion(input);
        var question = ChatRunRules.ValidateQuestion(questionText);
        if (!question.IsValid)
        {
            return ApiErrors.ValidationFailed(question.Failures);
        }

        if (!chatProvider.IsConfigured)
        {
            return ChatErrors.ToApiResult(new ChatGenerationException(providerNotConfigured: true));
        }

        if (!embeddingProvider.IsConfigured)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status503ServiceUnavailable,
                KnowledgeRetrievalEndpoints.EmbeddingNotConfiguredReason,
                KnowledgeProcessingIssues.EmbeddingNotConfigured);
        }

        using var lease = locks.TryAcquire(visitor.VisitorId);
        if (lease is null)
        {
            return ApiErrors.WithReason(StatusCodes.Status409Conflict, ChatRunEndpoints.RunInProgressReason, RunInProgressMessage);
        }

        var connected = await knowledgeBases.ConnectedToAsync(assistant.Id, cancellationToken);
        var run = new VisitorRun(
            answers,
            clock,
            jsonOptions.Value.SerializerOptions,
            loggerFactory.CreateLogger(typeof(VisitorChatRunEndpoints).FullName!),
            new GroundedAnswerRequest(
                GroundedAnswerProfile.For(assistant, connected),
                question.Value,
                ChatRunEndpoints.ClientHistory(earlierMessages),
                AccountId: null,
                assistant.Id,
                ModelInvocationPurpose.PublicAnswer),
            assistant.ShowCitations,
            // AG-UI needs ids on RUN_STARTED/RUN_FINISHED: the client's own (opaque, never looked up) or fresh ones.
            string.IsNullOrEmpty(input?.ThreadId) ? Guid.NewGuid().ToString() : input.ThreadId,
            string.IsNullOrEmpty(input?.RunId) ? Guid.NewGuid().ToString() : input.RunId);

        await StreamAsync(httpContext, run, cancellationToken);
        return Results.Empty;
    }

    private static async Task StreamAsync(HttpContext httpContext, VisitorRun run, CancellationToken cancellationToken)
    {
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = SseEventStreamFormatter.ServerSentEventsMediaType;
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var formatter = new SseEventStreamFormatter();
        try
        {
            await formatter.WriteAsync(run.EventsAsync(cancellationToken), response.Body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The visitor went away: the model call has stopped, and there is nothing to save.
        }
        catch (Exception exception) when (!run.Ended)
        {
            run.Logger.LogError(exception, "A website visitor's chat run failed after its stream started.");
            await formatter.WriteAsync(
                Single(new RunErrorEvent { Code = ChatRunEndpoints.InternalErrorCode, Message = InternalErrorMessage }),
                response.Body,
                CancellationToken.None);
        }
    }

    private static async IAsyncEnumerable<BaseEvent> Single(BaseEvent item)
    {
        yield return item;
        await Task.CompletedTask;
    }

    /// <summary>One visitor run's state while it streams: the answer pipeline, and nothing else.</summary>
    private sealed class VisitorRun(
        GroundedAnswerService answers,
        TimeProvider clock,
        JsonSerializerOptions jsonOptions,
        ILogger logger,
        GroundedAnswerRequest request,
        bool showCitations,
        string threadId,
        string runId)
    {
        public ILogger Logger { get; } = logger;

        /// <summary>Whether a terminal event (<c>RUN_FINISHED</c> or <c>RUN_ERROR</c>) was sent.</summary>
        public bool Ended { get; private set; }

        public async IAsyncEnumerable<BaseEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new RunStartedEvent { ThreadId = threadId, RunId = runId };

            var streamMessageId = Guid.NewGuid().ToString();
            yield return new TextMessageStartEvent { MessageId = streamMessageId, Role = "assistant" };

            GroundedReply? reply = null;
            RunErrorEvent? error = null;
            var streamed = false;
            await using (var events = answers.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    GroundedAnswerEvent current;
                    try
                    {
                        if (!await events.MoveNextAsync())
                        {
                            break;
                        }

                        current = events.Current;
                    }
                    catch (KnowledgeEmbeddingException exception)
                    {
                        Logger.LogWarning(exception.InnerException, "A visitor run could not embed the question: {Issue}", exception.Message);
                        error = new RunErrorEvent
                        {
                            Code = exception.ProviderNotConfigured
                                ? KnowledgeRetrievalEndpoints.EmbeddingNotConfiguredReason
                                : KnowledgeRetrievalEndpoints.EmbeddingUnavailableReason,
                            Message = exception.Message,
                        };
                        break;
                    }
                    catch (ChatGenerationException exception)
                    {
                        Logger.LogWarning(exception.InnerException, "A visitor run's model call failed: {Issue}", exception.Message);
                        error = new RunErrorEvent
                        {
                            Code = exception.ProviderNotConfigured ? ChatErrors.ChatNotConfiguredReason : ChatErrors.ChatUnavailableReason,
                            Message = exception.Message,
                        };
                        break;
                    }

                    switch (current)
                    {
                        case GroundedAnswerTextDelta { Text.Length: > 0 } delta:
                            streamed = true;
                            yield return new TextMessageContentEvent { MessageId = streamMessageId, Delta = delta.Text };
                            break;
                        case GroundedAnswerCompleted completed:
                            reply = completed.Reply;
                            break;
                        case GroundedAnswerRejected rejected:
                            reply = rejected.Reply;
                            break;
                    }
                }
            }

            if (error is not null)
            {
                Ended = true;
                yield return error;
                yield break;
            }

            if (reply is null)
            {
                throw new InvalidOperationException("The answer stream ended without a final reply.");
            }

            if (!streamed)
            {
                // TEXT_MESSAGE_CONTENT needs a non-empty delta; a refusal below the threshold never
                // called the model and has nothing else to show while streaming.
                yield return new TextMessageContentEvent { MessageId = streamMessageId, Delta = reply.Text };
            }

            yield return new TextMessageEndEvent { MessageId = streamMessageId };

            var view = ChatRunEndpoints.TransientReplyView(reply, clock.GetUtcNow());
            if (view.Reply is { Kind: "no-result" } noResult)
            {
                // The default steps tell staff to contact the assistant's manager; a visitor gets the
                // visitor wording (the owner's contact details are in the refusal message itself).
                view = view with { Reply = noResult with { NextSteps = GroundedReply.VisitorNoResultNextSteps } };
            }

            if (!showCitations && view.Reply is { Citations.Count: > 0 } shown)
            {
                // The assistant hides its sources: the excerpts do not leave the server either.
                view = view with { Reply = shown with { Citations = [] } };
            }

            yield return new CustomEvent { Name = ChatRunEndpoints.ReplyEventName, Value = JsonSerializer.SerializeToElement(view, jsonOptions) };
            Ended = true;
            yield return new RunFinishedEvent { ThreadId = threadId, RunId = runId };
        }
    }
}
