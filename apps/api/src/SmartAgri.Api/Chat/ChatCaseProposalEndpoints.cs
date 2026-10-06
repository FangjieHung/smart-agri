using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Cases;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Cases;
using SmartAgri.Application.Chat;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Chat;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Chat;

/// <summary><c>POST …/chat/case-proposals/{messageId}:confirm</c>: the title and description as the asker
/// edited them (both are what the case keeps; nothing else of the conversation is copied).</summary>
public sealed record ConfirmChatCaseProposalRequest(string? Title, string? Description);

/// <summary>
/// Confirming or dismissing an assistant's case proposal (M7 plan §3 H; issue #254).
/// </summary>
/// <remarks>
/// <para>
/// Checks, in order: the caller may use the assistant (<c>403 assistant-use</c>); the caller is an
/// internal account (<c>403 case</c> — an external customer is never offered a case); the message is a
/// case proposal in one of the caller's own threads with this assistant (<c>403 chat-thread</c>, the same
/// bytes as an unknown id); it is still proposed (<c>409 case-proposal-closed</c>: confirmed or dismissed
/// already, so a second confirmation never creates a second case).
/// </para>
/// <para>
/// Confirming then validates the title and description (<c>422</c>, <see cref="CaseRules"/>' messages)
/// and re-checks the type: still active and still one the assistant may propose, otherwise <c>422
/// case-type-not-proposable</c> (the card already says 「無法建立」). The case is created in 待受理 by
/// the asker, origin <c>chat-proposal</c>, in the type's default group with its handling time counted
/// from now, linked to this thread; the proposal records the case in the same <c>SaveChanges</c>. The
/// snapshot is a concurrency token, so of two concurrent confirmations one gets <c>409</c> and creates
/// nothing. Dismissing only records 「不用了」.
/// </para>
/// </remarks>
public static class ChatCaseProposalEndpoints
{
    public const string ClosedReason = "case-proposal-closed";

    public const string ClosedMessage = "這個提議已經處理過了：已建立案件，或已選擇不用了。";

    public const string NotProposableReason = "case-type-not-proposable";

    public const string NotProposableMessage = "這個案件類型已停用，或助理已不再提議這個類型，無法建立案件。";

    public static IEndpointRouteBuilder MapChatCaseProposalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var proposals = endpoints.MapGroup("/api/v1/assistants/{id:guid}/chat/case-proposals").RequireAuthorization();

        proposals.MapPost("/{messageId:guid}:confirm", ConfirmAsync)
            .WithSummary("Create the case an assistant proposed, with the title and description the asker confirmed (#254)")
            .Produces<ChatMessageView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        proposals.MapPost("/{messageId:guid}:dismiss", DismissAsync)
            .WithSummary("Record that the asker declined an assistant's case proposal (#254)")
            .Produces<ChatMessageView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        return endpoints;
    }

    /// <summary>Creates the case and returns the proposal message as <c>GET chat</c> now shows it (confirmed, with its case).</summary>
    internal static async Task<IResult> ConfirmAsync(
        Guid id,
        Guid messageId,
        ConfirmChatCaseProposalRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        RequestAccountRole roles,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (callerId, message, refused) = await OpenAsync(id, messageId, httpContext, dbContext, permissions, roles, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var snapshot = message!.ReadCaseProposal()!;
        var failures = Validate(request, out var title, out var description);
        if (failures.Count > 0)
        {
            return ApiErrors.ValidationFailed(failures);
        }

        var proposable = await ChatCaseProposals.ProposableAsync(dbContext, id, cancellationToken);
        if (proposable.All(type => type.Offer.TypeId != snapshot.TypeId)
            || await CaseTypeEndpoints.FindActiveAsync(dbContext, snapshot.TypeId, cancellationToken) is not { } type)
        {
            return ApiErrors.WithReason(StatusCodes.Status422UnprocessableEntity, NotProposableReason, NotProposableMessage, field: CaseRules.TypeField);
        }

        var group = await dbContext.CaseGroups.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == type.DefaultGroupId, cancellationToken);
        if (group is null || group.IsArchived)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity, CaseTypeEndpoints.GroupArchivedReason, CaseTypeEndpoints.GroupArchivedMessage,
                field: CaseRules.GroupField);
        }

        var now = clock.GetUtcNow();
        var (created, createdEvent) = Case.Create(
            CaseOrigin.ChatProposal, type, group, callerId, title, description, now.AddHours(type.DefaultDueHours),
            new CaseLinks(ThreadAssistantId: id, ThreadId: message.ThreadId), now);
        dbContext.Cases.Add(created);
        dbContext.CaseEvents.Add(createdEvent);
        message.ConfirmCaseProposal(created.Id, title, description);
        if (!await TrySaveAsync(dbContext, cancellationToken))
        {
            return Closed();
        }

        return Results.Ok(await ViewAsync(dbContext, id, message, cancellationToken));
    }

    /// <summary>Records 「不用了」 and returns the proposal message as <c>GET chat</c> now shows it.</summary>
    internal static async Task<IResult> DismissAsync(
        Guid id,
        Guid messageId,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        RequestAccountRole roles,
        CancellationToken cancellationToken)
    {
        var (_, message, refused) = await OpenAsync(id, messageId, httpContext, dbContext, permissions, roles, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        message!.DismissCaseProposal();
        if (!await TrySaveAsync(dbContext, cancellationToken))
        {
            return Closed();
        }

        return Results.Ok(await ViewAsync(dbContext, id, message, cancellationToken));
    }

    /// <summary>The checks both actions share (see the class remarks), and the tracked, still-proposed message.</summary>
    private static async Task<(Guid CallerId, ChatMessage? Message, IResult? Refused)> OpenAsync(
        Guid assistantId,
        Guid messageId,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        RequestAccountRole roles,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return (Guid.Empty, null, ApiErrors.Unauthorized());
        }

        var assistant = await ChatEndpoints.FindUsableAsync(dbContext, permissions, assistantId, callerId, cancellationToken);
        if (assistant is null)
        {
            return (callerId, null, ApiErrors.NotFound(ForbiddenReason.AssistantUse));
        }

        if (await roles.GetAsync(callerId, cancellationToken) is not { } role || !CaseGroupRules.IsEligibleMember(role))
        {
            return (callerId, null, ApiErrors.Forbidden(ForbiddenReason.CaseFeature));
        }

        var ownThreads = dbContext.ChatThreads.Where(ChatThreadAccess.OwnedBy(callerId, assistant.Id)).Select(thread => thread.Id);
        var message = await dbContext.ChatMessages
            .Where(candidate => candidate.Id == messageId && candidate.ReplyKind == ChatReplyKind.CaseProposal)
            .Where(candidate => ownThreads.Contains(candidate.ThreadId))
            .SingleOrDefaultAsync(cancellationToken);
        if (message?.ReadCaseProposal() is not { } snapshot)
        {
            return (callerId, null, ApiErrors.NotFound(ForbiddenReason.ChatThread));
        }

        return snapshot.Status == ChatCaseProposalStatus.Proposed
            ? (callerId, message, null)
            : (callerId, null, Closed());
    }

    private static List<ValidationFailure> Validate(ConfirmChatCaseProposalRequest request, out string title, out string description)
    {
        title = (request.Title ?? string.Empty).Trim();
        description = (request.Description ?? string.Empty).Trim();
        var failures = new List<ValidationFailure>();
        if (title.Length == 0)
        {
            failures.Add(new ValidationFailure(CaseRules.TitleField, CaseRules.TitleRequiredMessage));
        }
        else if (title.Length > Case.TitleMaxLength)
        {
            failures.Add(new ValidationFailure(CaseRules.TitleField, CaseRules.TitleTooLongMessage));
        }

        if (description.Length > Case.DescriptionMaxLength)
        {
            failures.Add(new ValidationFailure(CaseRules.DescriptionField, CaseRules.DescriptionTooLongMessage));
        }

        return failures;
    }

    /// <summary>Saves; <see langword="false"/> when another request confirmed or dismissed it first
    /// (the snapshot changed), in which case nothing was written.</summary>
    private static async Task<bool> TrySaveAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    private static IResult Closed() => ApiErrors.WithReason(StatusCodes.Status409Conflict, ClosedReason, ClosedMessage);

    private static async Task<ChatMessageView> ViewAsync(AppDbContext dbContext, Guid assistantId, ChatMessage message, CancellationToken cancellationToken)
    {
        var views = await ChatCaseProposals.ViewsAsync(dbContext, assistantId, [message], cancellationToken);
        return ChatEndpoints.ToMessageView(message, [], caseProposal: views.GetValueOrDefault(message.Id));
    }
}
