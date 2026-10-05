using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Assistants;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Chat;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Databases;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Chat;

/// <summary>
/// <c>POST .../chat/forms/{databaseId}/review</c> request: the answers to check before the member
/// is asked to consent, and the form version they were filled in (the form request's
/// <c>formVersion</c>).
/// </summary>
public sealed record ReviewChatFormRequest(int? FormVersionNumber, IReadOnlyDictionary<string, JsonElement>? Answers);

/// <summary>
/// <c>POST .../chat/forms/{databaseId}/submissions</c> request; lenient members, like
/// <see cref="SubmitDatabaseEntryRequest"/>, so a missing one is a <c>422</c>.
/// </summary>
/// <param name="SubmissionId">The idempotency key: generated once per form fill, sent again with
/// every retry.</param>
/// <param name="ThreadId">The conversation the form request was shown in, when the assistant keeps
/// conversations: the receipt message is saved there. Omitted, the most recently active one.</param>
public sealed record SubmitChatFormRequest(
    Guid? SubmissionId,
    int? FormVersionNumber,
    bool? Consent,
    IReadOnlyDictionary<string, JsonElement>? Answers,
    Guid? ThreadId = null);

/// <summary>
/// A successful submission from a conversation: the real receipt (#145's view, snapshot at
/// submission) and the <c>submission-receipt</c> message that shows it — saved in the conversation
/// when the assistant keeps conversations, transient (not stored anywhere) otherwise.
/// </summary>
public sealed record ChatFormSubmissionView(DatabaseSubmissionReceiptView Receipt, ChatMessageView Message);

/// <summary>
/// <c>POST .../chat/forms/{databaseId}/dismissals</c> request (M4 #171): the conversation the closed
/// form was offered in, when the assistant keeps conversations — only ever the caller's own. Omitted
/// (or an assistant that does not keep conversations), nothing about a conversation is checked.
/// The body itself may be omitted.
/// </summary>
public sealed record DismissChatFormRequest(Guid? ThreadId = null);

/// <summary>
/// An assistant's in-conversation form (M4 #148): the member reviews the answers, explicitly
/// consents and submits, in the same conversation. A second entry point to #145's
/// <see cref="DatabaseSubmissionService"/> (<see cref="DatabaseSubmissionSource.AssistantConversation"/>):
/// the same field validation, consent rule, idempotency and receipt.
/// </summary>
/// <remarks>
/// <para>
/// <b>Entry authorization</b> is this class's job, in <see cref="AssistantFormRequests"/>' order:
/// <c>401</c> → the caller may use the assistant (<c>403 assistant-use</c>) → the named conversation is
/// the caller's own (<c>403 chat-thread</c>) → the database is the assistant's connected form and its
/// owner may still use it (<c>403 assistant-form</c>, byte-identical for not connected, revoked,
/// deleted, another organization's or a made-up id). Then the service: <c>409 form-version-changed</c>,
/// <c>409 submission-key-reused</c>, <c>422</c> (missing key/version, answers, <c>consent-required</c>).
/// Nothing is written on any refusal, and nothing about the conversation is part of the record.
/// </para>
/// <para>
/// <b>The forms a member may open from the conversation's 「回報資料」 entry</b> (M4 #171,
/// <c>GET .../chat/forms</c>): the assistant's form target exactly as a form request would show it
/// right now (<see cref="AssistantFormRequests.FormRequestAsync"/>, re-authorized on every request),
/// as a list — empty when there is none (the entry is then not shown), at most one today because an
/// assistant has at most one form target. Same refusals as the rest: <c>401</c>, then
/// <c>403 assistant-use</c> for an assistant the caller may not use, another organization's or one
/// that does not exist, byte-identical.
/// </para>
/// <para>
/// <b>Dismissals</b> (M4 #171, <c>POST .../chat/forms/{databaseId}/dismissals</c>): the member closed
/// a form the conversation offered (「不用了」 or ×). One <see cref="ChatFormDismissal"/> row —
/// assistant, form, time; never the account, the conversation or anything typed — then <c>204</c>.
/// Same order of checks as a submission: <c>401</c> → <c>403 assistant-use</c> → <c>403 chat-thread</c>
/// (a named conversation that is not the caller's own) → <c>403 assistant-form</c>.
/// </para>
/// <para>
/// <b>Conversation saving is independent</b>: the record is stored in the database whatever the
/// assistant's <c>keepConversations</c>; only the receipt <i>message</i> follows the conversation
/// rule. A retry with the same <c>submissionId</c> returns the same receipt and the message already
/// saved for it (or saves it then, if the first attempt stopped in between).
/// </para>
/// </remarks>
public static class ChatFormEndpoints
{
    public static IEndpointRouteBuilder MapChatFormEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/assistants/{id:guid}/chat/forms", ListAsync)
            .RequireAuthorization()
            .WithSummary("The forms the caller may open from this assistant's conversation (#171)")
            .Produces<IReadOnlyList<ChatFormRequestView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        var forms = endpoints.MapGroup("/api/v1/assistants/{id:guid}/chat/forms/{databaseId:guid}")
            .RequireAuthorization();

        forms.MapPost("/review", ReviewAsync)
            .Produces<DatabaseTrialPreviewView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        forms.MapPost("/submissions", SubmitAsync)
            .Produces<ChatFormSubmissionView>(StatusCodes.Status201Created)
            .Produces<ChatFormSubmissionView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        forms.MapPost("/dismissals", DismissAsync)
            .WithSummary("Record that the caller closed an offered form without filling it in (#171)")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>The forms the caller may open from this assistant's conversation right now.</summary>
    internal static async Task<IResult> ListAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        AssistantFormRequests formRequests,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await ChatEndpoints.FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        var form = await formRequests.FormRequestAsync(assistant, null, cancellationToken);
        IReadOnlyList<ChatFormRequestView> forms = form is null ? [] : [form];
        return Results.Ok(forms);
    }

    /// <summary>Records one dismissal of an offered form; never anything the member typed.</summary>
    internal static async Task<IResult> DismissAsync(
        Guid id,
        Guid databaseId,
        DismissChatFormRequest? request,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        AssistantFormRequests formRequests,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await ChatEndpoints.FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        // Only the member in that conversation: a named thread must be the caller's own (ignored for
        // an assistant that does not keep conversations, as chat runs and submissions ignore it).
        if (assistant.KeepConversations && request?.ThreadId is { } threadId
            && !await dbContext.ChatThreads.AsNoTracking()
                .Where(ChatThreadAccess.OwnedBy(viewerId, assistant.Id))
                .AnyAsync(thread => thread.Id == threadId, cancellationToken))
        {
            return ApiErrors.NotFound(ForbiddenReason.ChatThread);
        }

        if (await formRequests.FormTargetAsync(assistant, databaseId, cancellationToken) is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantForm);
        }

        dbContext.ChatFormDismissals.Add(
            ChatFormDismissal.Record(assistant.OrganizationId, assistant.Id, databaseId, clock.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Checks the answers before consent; writes nothing.</summary>
    internal static async Task<IResult> ReviewAsync(
        Guid id,
        Guid databaseId,
        ReviewChatFormRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        AssistantFormRequests formRequests,
        DatabaseSubmissionService submissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await ChatEndpoints.FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        if (await formRequests.FormTargetAsync(assistant, databaseId, cancellationToken) is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantForm);
        }

        var outcome = await submissions.ReviewAsync(
            databaseId, request.FormVersionNumber, DatabaseEndpoints.ToAnswerInputs(request.Answers), cancellationToken);
        return outcome switch
        {
            DatabaseSubmissionOutcome.Reviewed reviewed => Results.Ok(new DatabaseTrialPreviewView(
                false,
                reviewed.FormVersionNumber,
                [.. reviewed.Entries.Select(entry => new DatabaseTrialEntryView(entry.Field.Id, entry.Field.Label, entry.Display))])),
            DatabaseSubmissionOutcome.Invalid invalid => ApiErrors.ValidationFailed(invalid.Failures),
            DatabaseSubmissionOutcome.FormChanged => FormChanged(),
            _ => ApiErrors.NotFound(ForbiddenReason.AssistantForm),
        };
    }

    internal static async Task<IResult> SubmitAsync(
        Guid id,
        Guid databaseId,
        SubmitChatFormRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        AssistantFormRequests formRequests,
        DatabaseSubmissionService submissions,
        ChatRunLocks locks,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await ChatEndpoints.FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        // The conversation the receipt message goes to: only ever the caller's own, and only when
        // the assistant keeps conversations (a thread id sent to one that does not is ignored, like
        // chat runs ignore it). Read untracked: the service may clear the change tracker.
        Guid? threadId = null;
        if (assistant.KeepConversations)
        {
            var threads = dbContext.ChatThreads.AsNoTracking().Where(ChatThreadAccess.OwnedBy(viewerId, assistant.Id));
            if (request.ThreadId is { } requested)
            {
                if (!await threads.AnyAsync(thread => thread.Id == requested, cancellationToken))
                {
                    return ApiErrors.NotFound(ForbiddenReason.ChatThread);
                }

                threadId = requested;
            }
            else
            {
                threadId = await threads
                    .OrderByDescending(thread => thread.LastActivityAt)
                    .ThenByDescending(thread => thread.CreatedAt)
                    .Select(thread => (Guid?)thread.Id)
                    .FirstOrDefaultAsync(cancellationToken);
            }
        }

        var target = await formRequests.FormTargetAsync(assistant, databaseId, cancellationToken);
        if (target is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantForm);
        }

        // The receipt message takes the thread's next sequence number: not while a reply is being
        // generated in it. Checked before anything is recorded.
        using var lease = threadId is { } lockedId ? locks.TryAcquire(lockedId) : null;
        if (threadId is not null && lease is null)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status409Conflict, ChatRunEndpoints.RunInProgressReason, "這段對話還在回覆上一個問題，請等回覆完成後再送出。");
        }

        var outcome = await submissions.SubmitAsync(
            new DatabaseSubmissionCommand(
                databaseId,
                viewerId,
                request.SubmissionId,
                request.FormVersionNumber,
                request.Consent,
                DatabaseEndpoints.ToAnswerInputs(request.Answers),
                DatabaseSubmissionSource.AssistantConversation,
                target.CollectionPurpose),
            cancellationToken);

        switch (outcome)
        {
            case DatabaseSubmissionOutcome.Created created:
                return Results.Created(
                    $"{DatabaseSubmissionEndpoints.SubmissionsPath}/{created.Receipt.Id}",
                    new ChatFormSubmissionView(
                        created.Receipt,
                        await ReceiptMessageAsync(dbContext, assistant, threadId, created.Receipt, clock, cancellationToken)));
            case DatabaseSubmissionOutcome.Replayed replayed:
                return Results.Ok(new ChatFormSubmissionView(
                    replayed.Receipt,
                    await ReceiptMessageAsync(dbContext, assistant, threadId, replayed.Receipt, clock, cancellationToken)));
            case DatabaseSubmissionOutcome.Invalid invalid:
                return ApiErrors.ValidationFailed(invalid.Failures);
            case DatabaseSubmissionOutcome.ConsentMissing:
                return ApiErrors.Refused(
                    DatabaseSubmissionEndpoints.ConsentRequiredReason,
                    DatabaseSubmissionRules.ConsentMissingMessage,
                    [new(DatabaseSubmissionRules.ConsentKey, DatabaseSubmissionRules.ConsentRequiredMessage)]);
            case DatabaseSubmissionOutcome.FormChanged:
                return FormChanged();
            case DatabaseSubmissionOutcome.KeyReused:
                return ApiErrors.WithReason(
                    StatusCodes.Status409Conflict,
                    DatabaseSubmissionEndpoints.KeyReusedReason,
                    DatabaseSubmissionEndpoints.KeyReusedMessage);
            default:
                return ApiErrors.NotFound(ForbiddenReason.AssistantForm);
        }
    }

    /// <summary>
    /// The receipt's message: the one already saved for this submission in the thread, else a new
    /// one saved there; transient when there is no thread (the assistant does not keep
    /// conversations). The thread is loaded only now, after the submission was written.
    /// </summary>
    private static async Task<ChatMessageView> ReceiptMessageAsync(
        AppDbContext dbContext,
        Assistant assistant,
        Guid? threadId,
        DatabaseSubmissionReceiptView receipt,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var text = AssistantFormRequestRules.ReceiptText(receipt.Recipient, receipt.ReceiptNumber);
        if (threadId is null)
        {
            var now = clock.GetUtcNow();
            return new ChatMessageView(
                Guid.CreateVersion7(),
                "assistant",
                null,
                new ChatReplyView("submission-receipt", text, [], null, [], null, receipt, null),
                now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond)));
        }

        var existing = await dbContext.ChatMessages.AsNoTracking()
            .Where(message => message.ThreadId == threadId && message.SubmissionId == receipt.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return ChatEndpoints.ToMessageView(existing, [], null, receipt);
        }

        var thread = await dbContext.ChatThreads
            .Where(candidate => candidate.AssistantId == assistant.Id)
            .SingleAsync(candidate => candidate.Id == threadId, cancellationToken);
        var saved = ChatMessage.SubmissionReceipt(thread, text, receipt.DatabaseId, receipt.Id, clock.GetUtcNow());
        dbContext.ChatMessages.Add(saved);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ChatEndpoints.ToMessageView(saved, [], null, receipt);
    }

    private static IResult FormChanged() =>
        ApiErrors.WithReason(
            StatusCodes.Status409Conflict, DatabaseEndpoints.FormChangedReason, DatabaseSubmissionEndpoints.FormChangedMessage);
}
