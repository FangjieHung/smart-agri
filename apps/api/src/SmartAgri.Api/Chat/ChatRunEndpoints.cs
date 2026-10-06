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
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Knowledge;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Chat;
using SmartAgri.Application.Databases;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Chat;

/// <summary>The <c>smartagri.thread</c> custom event's value: the thread a saved run wrote to
/// (created, or the one it continued) and its title after this run.</summary>
public sealed record ChatRunThreadView(Guid ThreadId, string Title);

/// <summary>
/// <c>POST /api/v1/assistants/{id}/chat/runs</c> (M3 plan §3 and Slice 7; ticket #77): answers
/// one question as an AG-UI event stream (<c>text/event-stream</c>, written by
/// <see cref="SseEventStreamFormatter"/>) and saves the exchange within the same request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Request.</b> The body is AG-UI's <see cref="RunAgentInput"/>. The question is the last
/// <c>user</c> message. <c>threadId</c> is optional, with <c>sendChatMessage</c>'s meaning
/// (mapping §2.4): omitted (absent, <c>null</c> or <c>""</c>) continues the most recently active
/// thread, or starts one when there is none; given, it must be one of the caller's own threads
/// with this assistant — anything else, including a value that is not a GUID, is
/// <c>403 chat-thread</c>, byte-identical to an unknown id (<see cref="ChatThreadAccess"/>).
/// </para>
/// <para>
/// <b>Refusals before the stream</b> are ordinary JSON responses, in this order: <c>401</c>;
/// <c>403 assistant-use</c>; <c>403 chat-thread</c>; <c>422</c> (no question, blank, or over
/// <see cref="ChatRunRules.QuestionMaxLength"/> characters — <c>message</c> plus
/// <c>errors.question</c>); <c>503 chat-not-configured</c> / <c>503 embedding-not-configured</c>;
/// <c>409 chat-run-in-progress</c> (<see cref="ChatRunLocks"/>). Nothing is saved for any of them.
/// </para>
/// <para>
/// <b>Events</b> (M3 plan §3 「AG-UI 事件對應」): <c>RUN_STARTED</c> → <c>TEXT_MESSAGE_START</c> →
/// one or more <c>TEXT_MESSAGE_CONTENT</c> (the model's text as it arrives, unvalidated; when
/// nothing was streamed — e.g. a below-threshold refusal that never calls the model — one event
/// carrying the final reply's text) → <c>TEXT_MESSAGE_END</c> → <c>CUSTOM smartagri.reply</c>
/// (the final <see cref="ChatMessageView"/>: what <c>GET chat</c> returns for this message; it
/// replaces the streamed text — M3 plan §7 C) → <c>CUSTOM smartagri.thread</c>
/// (<see cref="ChatRunThreadView"/>, only when the assistant keeps conversations) →
/// <c>RUN_FINISHED</c>. A failure once streaming has started ends the stream with
/// <c>RUN_ERROR</c> instead (its <c>code</c> is the same reason a <c>503</c> would carry:
/// <c>chat-unavailable</c>, <c>embedding-unavailable</c>, …; <c>internal-error</c> for anything
/// unexpected), with no <c>smartagri.reply</c> and nothing saved but the question.
/// </para>
/// <para>
/// <b>Saving</b> (M3 plan §3 「對話保存」). With <see cref="Assistant.KeepConversations"/>: the
/// question (and a new thread, titled by <see cref="ChatRunRules.TitleFromQuestion"/>) is saved
/// before the stream starts; the reply and its citation snapshots are saved in one
/// <c>SaveChanges</c> (one transaction) just before <c>smartagri.reply</c> is sent. If the client
/// disconnects, the request's <see cref="CancellationToken"/> stops the model call and only the
/// question stays. A retry of that failed run (ticket #105) — same thread, same question, and
/// the same id on the <c>RunAgentInput</c> user message — recognizes the already-saved question
/// (<see cref="ChatRunRules.ValidateClientMessageId"/>) instead of saving a duplicate: only when
/// it is still the thread's very last message, i.e. nothing answered it since. The earlier turns
/// the model sees come from the saved thread; the client's
/// <c>messages</c> other than the question are ignored. Without it, nothing is written to any
/// conversation table: the client's earlier <c>user</c>/<c>assistant</c> messages are the only
/// context (text only, capped by <see cref="GroundedAnswerPrompt.RecentHistory"/>, citation
/// markers removed), and citations only ever come from this run's retrieval — whatever the client
/// sends is never read as a citation or a reply. Model calls are recorded either way (without
/// content).
/// </para>
/// <para>
/// <b>Form requests</b> (M4 #148). The orchestration layer, not the model, calls the assistant's one
/// server-defined form tool (<see cref="AssistantFormRequestRules"/>): when the question asks to fill
/// something in and <see cref="AssistantFormRequests.FormRequestAsync"/> finds a form target the
/// assistant may use right now, the run answers with a <c>form-request</c> reply carrying the
/// server's form (fields, version, purpose, recipient, actual readers) — same events, same saving
/// rules, no model call (so no model invocation is recorded). Otherwise, including right after the
/// database was disconnected or its designation revoked, the question is answered as usual.
/// With <c>Chat:FormRequests:Trigger = Model</c> (#164, <see cref="ChatFormRequestTool"/>) the model
/// decides instead of the keywords: whenever the assistant has a form target it may use right now,
/// one selection call (purpose <c>form-request</c>) is offered that one form; the server re-checks the
/// id the model names and builds the form. A model failure falls back to the keyword gate. Same
/// events, same saving rules, plus one <c>CUSTOM smartagri.form-check</c> (empty value; #171) right
/// before the selection call — after <c>TEXT_MESSAGE_START</c> and after a statistics query that did
/// not answer — so the client can show that it is checking for a form. Keyword mode and assistants
/// without a form target never send it. Since M7-8 the form request is the first proposal of the
/// <see cref="ChatProposalStage"/> (find, decide by the trigger, compose and save), unchanged.
/// </para>
/// <para>
/// <b>Database queries</b> (M4 #149, <see cref="ChatDatabaseQueries"/>). When the question asks for a
/// count, total or statistics (<see cref="DatabaseQueryTools.AsksForStatistics"/>) and the assistant
/// has a database it may use now, the model is offered the fixed query tools for the databases the
/// <b>asker</b> may read and chooses one tool and its parameters; the server validates them, runs the
/// query as the asker and answers with a <c>database-query</c> reply whose text and figures are
/// composed from the result (the model never sees it). Same events and saving rules; the reply is
/// saved as a snapshot (<c>ChatMessages.DatabaseQuery</c>), re-checked whenever it is read, never
/// handed off, and replaced by a placeholder in the history later model calls see. Precedence:
/// a database query first, then the proposal stage (the form request), then the answer pipeline — so 「本月回報了幾筆？」
/// is a query while 「我要回報」 is a form; a question the model decides not to query (no tool call)
/// falls through to the form request or the answer. A model failure here ends the stream with
/// <c>RUN_ERROR chat-unavailable</c>, as in the answer pipeline.
/// </para>
/// </remarks>
public static class ChatRunEndpoints
{
    /// <summary>The <c>409</c> reason when the thread already has a reply being generated.</summary>
    public const string RunInProgressReason = "chat-run-in-progress";

    /// <summary>The custom event carrying the final reply.</summary>
    public const string ReplyEventName = "smartagri.reply";

    /// <summary>The custom event carrying the thread a saved run wrote to.</summary>
    public const string ThreadEventName = "smartagri.thread";

    /// <summary>
    /// The custom event sent just before model mode's form selection call (M4 #171): only with
    /// <c>Chat:FormRequests:Trigger = Model</c> and a form target the assistant may use right now;
    /// never in keyword mode or for an assistant without one. Its value is an empty object.
    /// </summary>
    public const string FormCheckEventName = "smartagri.form-check";

    /// <summary><see cref="FormCheckEventName"/>'s value.</summary>
    internal static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary><c>RUN_ERROR</c>'s code for a failure that is not a model/embedding one.</summary>
    public const string InternalErrorCode = "internal-error";

    private const string RunInProgressMessage = "這段對話還在回覆上一個問題，請等回覆完成後再送出。";
    private const string InternalErrorMessage = "回覆途中發生錯誤，請稍後重試。";

    /// <summary>How many of the client's earlier messages (unsaved conversations) are read at
    /// all; <see cref="GroundedAnswerPrompt.RecentHistory"/> keeps fewer still.</summary>
    private const int ClientHistoryMaxMessages = 20;

    /// <summary>How many saved messages are loaded as context; the prompt keeps fewer still.</summary>
    private const int SavedHistoryMaxMessages = 12;

    public static IEndpointRouteBuilder MapChatRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/assistants/{id:guid}/chat/runs", RunAsync)
            .RequireAuthorization()
            .WithSummary("Answer one question as an AG-UI event stream")
            .WithDescription(
                "Body: AG-UI RunAgentInput (use @ag-ui/core's type; this schema is only a sketch). " +
                "200: text/event-stream of AG-UI events (RUN_STARTED, TEXT_MESSAGE_*, CUSTOM smartagri.reply / " +
                "smartagri.thread, smartagri.form-check in model form-trigger mode, RUN_FINISHED or RUN_ERROR); " +
                "see ChatRunEndpoints' remarks.")
            .Accepts<RunAgentInput>("application/json")
            .Produces<string>(StatusCodes.Status200OK, SseEventStreamFormatter.ServerSentEventsMediaType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    internal static async Task<IResult> RunAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        IAnswerKnowledgeBases knowledgeBases,
        GroundedAnswerService answers,
        ChatProposalStage proposals,
        ChatDatabaseQueries databaseQueries,
        ChatRunLocks locks,
        ChatClientProvider chatProvider,
        EmbeddingProvider embeddingProvider,
        TimeProvider clock,
        IOptions<JsonOptions> jsonOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var input = await ReadInputAsync(httpContext.Request, cancellationToken);

        var assistant = await ChatEndpoints.FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        ChatThread? thread = null;
        if (assistant.KeepConversations)
        {
            if (!string.IsNullOrEmpty(input?.ThreadId))
            {
                thread = Guid.TryParse(input.ThreadId, out var threadId)
                    ? await ChatEndpoints.FindThreadAsync(dbContext, assistant.Id, viewerId, threadId, cancellationToken)
                    : null;
                if (thread is null)
                {
                    return ApiErrors.NotFound(ForbiddenReason.ChatThread);
                }
            }
            else
            {
                thread = await dbContext.ChatThreads
                    .Where(ChatThreadAccess.OwnedBy(viewerId, assistant.Id))
                    .OrderByDescending(candidate => candidate.LastActivityAt)
                    .ThenByDescending(candidate => candidate.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
            }
        }

        var (questionText, questionMessageId, earlierMessages) = SplitQuestion(input);
        var question = ChatRunRules.ValidateQuestion(questionText);
        if (!question.IsValid)
        {
            return ApiErrors.ValidationFailed(question.Failures);
        }

        // #105: a retry after a mid-stream failure sends the same question with the same
        // RunAgentInput user-message id; an invalid or absent one just means "always save".
        var clientMessageId = ChatRunRules.ValidateClientMessageId(questionMessageId);

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

        IDisposable? lease = null;
        if (thread is not null)
        {
            lease = locks.TryAcquire(thread.Id);
            if (lease is null)
            {
                return ApiErrors.WithReason(StatusCodes.Status409Conflict, RunInProgressReason, RunInProgressMessage);
            }
        }

        try
        {
            var history = !assistant.KeepConversations ? ClientHistory(earlierMessages)
                : thread is not null ? await SavedHistoryAsync(dbContext, thread.Id, cancellationToken)
                : [];

            if (assistant.KeepConversations)
            {
                var now = clock.GetUtcNow();
                if (thread is null)
                {
                    thread = new ChatThread(assistant, viewerId, ChatRunRules.TitleFromQuestion(question.Value), now);
                    dbContext.ChatThreads.Add(thread);
                    // A brand-new thread's id is known only to this request until the stream
                    // tells the client, so no other run can hold it; lock it all the same.
                    lease = locks.TryAcquire(thread.Id);
                }
                else if (ChatRunRules.TitleForFirstQuestion(thread, question.Value) is { } title)
                {
                    thread.Rename(title, now);
                }

                // #105: a retry sends the same client message id as the failed attempt that
                // only got the question saved (mid-stream RUN_ERROR); when the thread's last
                // message is exactly that — the same id, an account turn, nothing after it —
                // reuse it instead of saving a duplicate question. A brand-new thread has no
                // rows yet, so this is always "no match" for it.
                var lastMessage = clientMessageId is not null
                    ? await dbContext.ChatMessages
                        .Where(message => message.ThreadId == thread.Id)
                        .OrderByDescending(message => message.Sequence)
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;
                var reusesLastQuestion = lastMessage is { Author: ChatMessageAuthor.Account } message
                    && message.ClientMessageId == clientMessageId;

                if (!reusesLastQuestion)
                {
                    dbContext.ChatMessages.Add(ChatMessage.Account(thread, question.Value, now, clientMessageId));
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            // The proposal stage (M7-8), step 1: what this request may be offered (the form tool, #148),
            // re-authorized on this request, never cached. Keyword mode decides here; model mode (#164)
            // only finds what to offer and lets the model decide in the run.
            var proposalCandidates = await proposals.FindAsync(assistant, viewerId, question.Value, cancellationToken);

            // The query tools (#149): offered only for a statistics question, scoped to this request.
            var queryScope = DatabaseQueryTools.AsksForStatistics(question.Value)
                ? await databaseQueries.ScopeAsync(assistant, viewerId, cancellationToken)
                : null;

            var connected = await knowledgeBases.ConnectedToAsync(assistant.Id, cancellationToken);
            var run = new ChatRun(
                dbContext,
                answers,
                clock,
                jsonOptions.Value.SerializerOptions,
                loggerFactory.CreateLogger(typeof(ChatRunEndpoints).FullName!),
                new GroundedAnswerRequest(
                    GroundedAnswerProfile.For(assistant, connected), question.Value, history, viewerId, assistant.Id),
                viewerId,
                thread,
                ThreadIdForEvents(thread, input),
                string.IsNullOrEmpty(input?.RunId) ? Guid.NewGuid().ToString() : input.RunId,
                proposalCandidates,
                databaseQueries,
                queryScope);

            await StreamAsync(httpContext, run, cancellationToken);
            return Results.Empty;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>The request body as AG-UI's own JSON contract (<see cref="AGUIJsonSerializerContext"/>,
    /// not the API's JSON options), or <see langword="null"/> when it is not one — which then
    /// fails as "no question" (<c>422</c>) after the access checks.</summary>
    internal static async Task<RunAgentInput?> ReadInputAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(request.Body, AGUIJsonSerializerContext.Default.RunAgentInput, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The last <c>user</c> message's text (the question), its AG-UI message id
    /// (ticket #105 — a retry's dedupe key, not yet validated), and the messages before it.</summary>
    internal static (string? Question, string? MessageId, IReadOnlyList<AGUIMessage> Earlier) SplitQuestion(RunAgentInput? input)
    {
        var messages = input?.Messages ?? [];
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index] is AGUIUserMessage user)
            {
                return (user.Content.ToString(), user.Id, [.. messages.Take(index)]);
            }
        }

        return (null, null, []);
    }

    /// <summary>An unsaved conversation's context: the client's earlier <c>user</c> and
    /// <c>assistant</c> messages as plain text. Every other role (system, developer, tool, …) is
    /// dropped — a client never gets to add instructions — and so is anything but text.</summary>
    internal static IReadOnlyList<ConversationTurn> ClientHistory(IReadOnlyList<AGUIMessage> earlier) =>
    [
        .. earlier
            .TakeLast(ClientHistoryMaxMessages)
            .Select(message => message switch
            {
                AGUIUserMessage user => new ConversationTurn(ConversationAuthor.Account, user.Content.ToString()),
                AGUIAssistantMessage assistant => new ConversationTurn(ConversationAuthor.Assistant, assistant.Content ?? string.Empty),
                _ => null,
            })
            .OfType<ConversationTurn>()
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Text)),
    ];

    /// <summary>A saved thread's newest messages before this question, oldest first.</summary>
    private static async Task<IReadOnlyList<ConversationTurn>> SavedHistoryAsync(
        AppDbContext dbContext, Guid threadId, CancellationToken cancellationToken)
    {
        var newest = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(message => message.ThreadId == threadId)
            .OrderByDescending(message => message.Sequence)
            .Take(SavedHistoryMaxMessages)
            .Select(message => new { message.Author, message.Text, message.ReplyKind })
            .ToListAsync(cancellationToken);
        newest.Reverse();
        return
        [
            .. newest.Select(message => new ConversationTurn(
                message.Author == ChatMessageAuthor.Account ? ConversationAuthor.Account : ConversationAuthor.Assistant,
                // Query results never reach a model (#149), not even as history.
                message.ReplyKind == ChatReplyKind.DatabaseQuery ? DatabaseQueryTools.HistoryPlaceholder : message.Text)),
        ];
    }

    /// <summary>AG-UI requires a thread id on <c>RUN_STARTED</c>/<c>RUN_FINISHED</c>: the saved
    /// thread's; unsaved, the client's own (opaque, never looked up) or a fresh one.</summary>
    private static string ThreadIdForEvents(ChatThread? thread, RunAgentInput? input) =>
        thread is not null ? thread.Id.ToString()
        : !string.IsNullOrEmpty(input?.ThreadId) ? input.ThreadId
        : Guid.NewGuid().ToString();

    private static async Task StreamAsync(HttpContext httpContext, ChatRun run, CancellationToken cancellationToken)
    {
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = SseEventStreamFormatter.ServerSentEventsMediaType;
        response.Headers.CacheControl = "no-cache";
        // Reverse proxies (nginx) must not hold events back until the response ends.
        response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var formatter = new SseEventStreamFormatter();
        try
        {
            await formatter.WriteAsync(run.EventsAsync(cancellationToken), response.Body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away: the model call has stopped, nothing more to send or save.
        }
        catch (Exception exception) when (!run.Ended)
        {
            // Unexpected (e.g. the database failed while saving the reply): the stream has
            // started, so the only way left to say so is a final RUN_ERROR.
            run.Logger.LogError(exception, "A chat run failed after its stream started.");
            await formatter.WriteAsync(
                Single(new RunErrorEvent { Code = InternalErrorCode, Message = InternalErrorMessage }),
                response.Body,
                CancellationToken.None);
        }
    }

    private static async IAsyncEnumerable<BaseEvent> Single(BaseEvent item)
    {
        yield return item;
        await Task.CompletedTask;
    }

    /// <summary>One run's state while it streams.</summary>
    private sealed class ChatRun(
        AppDbContext dbContext,
        GroundedAnswerService answers,
        TimeProvider clock,
        JsonSerializerOptions jsonOptions,
        ILogger logger,
        GroundedAnswerRequest request,
        Guid askerId,
        ChatThread? thread,
        string threadIdForEvents,
        string runId,
        IReadOnlyList<ChatProposalCandidate> proposalCandidates,
        ChatDatabaseQueries databaseQueries,
        ChatDatabaseQueryScope? queryScope)
    {
        public ILogger Logger { get; } = logger;

        /// <summary>Whether a terminal event (<c>RUN_FINISHED</c> or <c>RUN_ERROR</c>) was sent.</summary>
        public bool Ended { get; private set; }

        public async IAsyncEnumerable<BaseEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new RunStartedEvent { ThreadId = threadIdForEvents, RunId = runId };

            // The streamed text's id. The saved message's id (smartagri.reply) is the one that
            // lasts: the client replaces this message with it.
            var streamMessageId = Guid.NewGuid().ToString();
            yield return new TextMessageStartEvent { MessageId = streamMessageId, Role = "assistant" };

            if (queryScope is not null)
            {
                // The query tool (#149): the model chooses, the server runs and writes the reply.
                ChatDatabaseQueryAnswer? query = null;
                RunErrorEvent? queryError = null;
                try
                {
                    query = await databaseQueries.AnswerAsync(
                        queryScope, request.Question, askerId, request.AssistantId!.Value, cancellationToken);
                }
                catch (ChatGenerationException exception)
                {
                    Logger.LogWarning(exception.InnerException, "A chat run's query selection failed: {Issue}", exception.Message);
                    queryError = new RunErrorEvent
                    {
                        Code = exception.ProviderNotConfigured ? ChatErrors.ChatNotConfiguredReason : ChatErrors.ChatUnavailableReason,
                        Message = exception.Message,
                    };
                }

                if (queryError is not null)
                {
                    Ended = true;
                    yield return queryError;
                    yield break;
                }

                if (query is not null)
                {
                    yield return new TextMessageContentEvent { MessageId = streamMessageId, Delta = query.Text };
                    yield return new TextMessageEndEvent { MessageId = streamMessageId };
                    var queryView = thread is not null
                        ? await SaveQueryAsync(thread, query, cancellationToken)
                        : new ChatMessageView(Guid.CreateVersion7(), "assistant", null, ChatEndpoints.QueryReplyView(query), MicrosecondNow());
                    foreach (var finalEvent in FinalEvents(queryView))
                    {
                        yield return finalEvent;
                    }

                    yield break;
                }
            }

            // The proposal stage (M7-8), steps 2 and 3, in precedence order: the first proposal decided
            // ends the run with its server-built reply (no model call in keyword mode).
            foreach (var candidate in proposalCandidates)
            {
                foreach (var beforeDecision in candidate.BeforeDecision)
                {
                    yield return beforeDecision;
                }

                if (await candidate.DecideAsync(cancellationToken) is not { } proposal)
                {
                    continue;
                }

                yield return new TextMessageContentEvent { MessageId = streamMessageId, Delta = proposal.Text };
                yield return new TextMessageEndEvent { MessageId = streamMessageId };
                var proposalView = thread is not null
                    ? await SaveProposalAsync(thread, proposal, cancellationToken)
                    : proposal.Transient(MicrosecondNow());
                foreach (var finalEvent in FinalEvents(proposalView))
                {
                    yield return finalEvent;
                }

                yield break;
            }

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
                        Logger.LogWarning(exception.InnerException, "A chat run could not embed the question: {Issue}", exception.Message);
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
                        Logger.LogWarning(exception.InnerException, "A chat run's model call failed: {Issue}", exception.Message);
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
                // AG-UI's TEXT_MESSAGE_CONTENT needs a non-empty delta, and a reply that never
                // called the model (below the threshold) has nothing else to show while streaming.
                yield return new TextMessageContentEvent { MessageId = streamMessageId, Delta = reply.Text };
            }

            yield return new TextMessageEndEvent { MessageId = streamMessageId };

            var view = thread is not null
                ? await SaveAsync(thread, reply, cancellationToken)
                : Transient(reply);
            foreach (var finalEvent in FinalEvents(view))
            {
                yield return finalEvent;
            }
        }

        /// <summary><c>smartagri.reply</c>, <c>smartagri.thread</c> (saved runs only) and
        /// <c>RUN_FINISHED</c>, after the reply has been saved (or not, for an unsaved run).</summary>
        private IEnumerable<BaseEvent> FinalEvents(ChatMessageView view)
        {
            yield return new CustomEvent { Name = ReplyEventName, Value = JsonSerializer.SerializeToElement(view, jsonOptions) };

            if (thread is not null)
            {
                yield return new CustomEvent
                {
                    Name = ThreadEventName,
                    Value = JsonSerializer.SerializeToElement(new ChatRunThreadView(thread.Id, thread.Title), jsonOptions),
                };
            }

            Ended = true;
            yield return new RunFinishedEvent { ThreadId = threadIdForEvents, RunId = runId };
        }

        /// <summary>Saves a proposal's reply (a form request saves only the database id; the form is
        /// re-read and re-authorized whenever it is shown) and returns it exactly as <c>GET chat</c> will.</summary>
        private async Task<ChatMessageView> SaveProposalAsync(
            ChatThread savedThread, ChatProposalReply proposal, CancellationToken cancellationToken)
        {
            var message = proposal.CreateMessage(savedThread, clock.GetUtcNow());
            dbContext.ChatMessages.Add(message);
            await dbContext.SaveChangesAsync(cancellationToken);
            return proposal.ToView(message);
        }

        /// <summary>Saves a query answer (its text and the structured snapshot) and returns it exactly
        /// as <c>GET chat</c> will while its database stays visible to the asker.</summary>
        private async Task<ChatMessageView> SaveQueryAsync(
            ChatThread savedThread, ChatDatabaseQueryAnswer query, CancellationToken cancellationToken)
        {
            var message = ChatMessage.DatabaseQuery(
                savedThread, query.Text, ChatDatabaseQueryJson.Serialize(query.View), clock.GetUtcNow());
            dbContext.ChatMessages.Add(message);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ChatEndpoints.ToMessageView(message, [], query: query);
        }

        private DateTimeOffset MicrosecondNow()
        {
            var now = clock.GetUtcNow();
            return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        }

        /// <summary>Saves the reply and its citation snapshots in one <c>SaveChanges</c> and
        /// returns it exactly as <c>GET chat</c> will.</summary>
        private async Task<ChatMessageView> SaveAsync(ChatThread savedThread, GroundedReply reply, CancellationToken cancellationToken)
        {
            var message = ChatMessage.Assistant(
                savedThread, reply.Text, ToReplyKind(reply.Kind), reply.Notice, reply.NextSteps, clock.GetUtcNow());
            dbContext.ChatMessages.Add(message);
            List<ChatMessageCitation> citations =
            [
                .. reply.Citations.Select(citation => new ChatMessageCitation(
                    message,
                    citation.Ordinal,
                    citation.ChunkId,
                    citation.KnowledgeBaseId,
                    citation.KnowledgeBaseName,
                    citation.DocumentId,
                    citation.DocumentName,
                    citation.VersionId,
                    citation.VersionNumber,
                    citation.VersionEffectiveFrom,
                    citation.LocationLabel,
                    citation.Excerpt,
                    citation.Text)),
            ];
            dbContext.ChatMessageCitations.AddRange(citations);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ChatEndpoints.ToMessageView(message, citations);
        }

        /// <summary>An unsaved reply (<see cref="TransientReplyView"/>).</summary>
        private ChatMessageView Transient(GroundedReply reply) => TransientReplyView(reply, clock.GetUtcNow());

        private static ChatReplyKind ToReplyKind(GroundedReplyKind kind) => kind switch
        {
            GroundedReplyKind.CompanyData => ChatReplyKind.CompanyData,
            GroundedReplyKind.GeneralKnowledge => ChatReplyKind.GeneralKnowledge,
            GroundedReplyKind.NoResult => ChatReplyKind.NoResult,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown reply kind."),
        };
    }

    /// <summary>An unsaved reply, shaped exactly like a saved one (<c>smartagri.reply</c> of a
    /// conversation that is not kept, and of every website visitor's run, #196). Its id and citation
    /// ids are not stored anywhere, so the citation-detail endpoint cannot resolve them: an unsaved
    /// conversation shows citations from the excerpt alone.</summary>
    internal static ChatMessageView TransientReplyView(GroundedReply reply, DateTimeOffset now)
    {
        var messageId = Guid.CreateVersion7();
        var view = reply.Kind switch
        {
            GroundedReplyKind.CompanyData => new ChatReplyView(
                "company-data",
                reply.Text,
                [
                    .. reply.Citations.Select(citation => new ChatCitationView(
                        ChatEndpoints.CitationId(messageId, citation.Ordinal),
                        citation.KnowledgeBaseName,
                        citation.DocumentName,
                        citation.Excerpt,
                        ChatEndpoints.UpdatedLabel(citation.VersionEffectiveFrom))),
                ],
                null,
                [],
                null,
                null,
                null),
            GroundedReplyKind.GeneralKnowledge => new ChatReplyView("general-knowledge", reply.Text, [], reply.Notice, [], null, null, null),
            _ => new ChatReplyView("no-result", reply.Text, [], null, reply.NextSteps, null, null, null),
        };
        // Microseconds, like every saved timestamp (PostgreSQL's precision).
        return new ChatMessageView(messageId, "assistant", null, view, now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond)));
    }
}
