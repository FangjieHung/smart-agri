using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Accounts;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Cases;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Cases;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Api.Cases;

/// <summary><c>:accept</c>: only the version the screen showed.</summary>
/// <param name="EventCount">The case's <c>eventCount</c> as shown; another value is <c>409 case-changed</c>.</param>
public sealed record AcceptCaseRequest(int? EventCount);

/// <summary><c>:request-info</c> (the note is required) and <c>:resume</c> (optional).</summary>
public sealed record CaseNoteRequest(int? EventCount, string? Note);

/// <summary><c>:complete</c>: the 處理結果 is required.</summary>
public sealed record CompleteCaseRequest(int? EventCount, string? Resolution);

/// <summary><c>:cancel</c>: the reason is required except from the creator before acceptance.</summary>
public sealed record CancelCaseRequest(int? EventCount, string? Reason);

/// <summary><c>:transfer</c>: another, not archived group; the note is optional.</summary>
public sealed record TransferCaseRequest(int? EventCount, Guid? GroupId, string? Note);

/// <summary><c>:set-due</c>: the new due time, not earlier than now (decision H); the note is optional.</summary>
public sealed record SetCaseDueRequest(int? EventCount, DateTimeOffset? DueAt, string? Note);

/// <summary><c>POST …/comments</c>: the note is required.</summary>
public sealed record CaseCommentRequest(int? EventCount, string? Note);

/// <summary>
/// The action table (M7 plan §3 D, decisions I and J; issue #249): <c>POST /api/v1/cases/{id}:&lt;action&gt;</c>
/// and <c>POST /api/v1/cases/{id}/comments</c>. Each answers with the detail (<c>200</c>) and writes
/// exactly one <see cref="CaseEvent"/>.
/// </summary>
/// <remarks>
/// <para>Checks, in order (every refusal leaves the case untouched and writes no event):</para>
/// <list type="number">
/// <item>an internal account that sees the case (<see cref="CaseVisibility"/>) — otherwise the one
/// <c>403 case</c>, byte for byte like a case that does not exist;</item>
/// <item>the request's shape: <c>eventCount</c> present, texts at most 2,000 characters, the transfer's
/// group and the new due time present (<c>422</c>, no reason);</item>
/// <item><c>eventCount</c> equals the case's — otherwise <c>409 case-changed</c> (decision I);</item>
/// <item><see cref="CaseActionRules.Check"/>: a status where no one may do this (every closed case) is
/// <c>409 case-changed</c>; a status where the caller may not is <c>403 case-action</c>;</item>
/// <item>what the action itself needs: <c>422 note-required</c> / <c>resolution-required</c> /
/// <c>reason-required</c>; a transfer to an unknown (<c>case-group-not-found</c>), archived
/// (<c>case-group-archived</c>) or the same group (<c>case-group-unchanged</c>); a due time in the past
/// (<c>due-in-past</c>).</item>
/// </list>
/// <para>
/// Two concurrent actions on the same version cannot both succeed: <see cref="Case.EventCount"/> is a
/// concurrency token and <c>(CaseId, Ordinal)</c> is unique, so the second save is <c>409 case-changed</c>.
/// </para>
/// </remarks>
public static class CaseActionEndpoints
{
    public const string ChangedReason = "case-changed";

    public const string ChangedMessage = "這件案件剛被其他人更新，請重新整理後再試。";

    public const string GroupUnchangedReason = "case-group-unchanged";

    public const string GroupUnchangedMessage = "案件已經在這個承辦組，請選擇其他承辦組。";

    internal static RouteGroupBuilder MapCaseActionEndpoints(this RouteGroupBuilder cases)
    {
        Describe(cases.MapPost("/{id:guid}:accept", AcceptAsync));
        Describe(cases.MapPost("/{id:guid}:request-info", RequestInfoAsync));
        Describe(cases.MapPost("/{id:guid}:resume", ResumeAsync));
        Describe(cases.MapPost("/{id:guid}:complete", CompleteAsync));
        Describe(cases.MapPost("/{id:guid}:cancel", CancelAsync));
        Describe(cases.MapPost("/{id:guid}:transfer", TransferAsync));
        Describe(cases.MapPost("/{id:guid}:set-due", SetDueAsync));
        Describe(cases.MapPost("/{id:guid}/comments", CommentAsync));
        return cases;
    }

    internal static Task<IResult> AcceptAsync(
        Guid id, AcceptCaseRequest request, HttpContext httpContext, AppDbContext dbContext, RequestAccountRole roles,
        RequestAccountPermissions permissions, TimeProvider clock, CancellationToken cancellationToken) =>
        RunAsync(
            new ActionRequest(id, CaseAction.Accept, request.EventCount, CaseRules.ValidateAction(request.EventCount)),
            context => Done(context.Case.Accept(context.Caller.Id, context.Now)),
            new Services(httpContext, dbContext, roles, permissions, clock),
            cancellationToken);

    internal static Task<IResult> RequestInfoAsync(
        Guid id, CaseNoteRequest request, HttpContext httpContext, AppDbContext dbContext, RequestAccountRole roles,
        RequestAccountPermissions permissions, TimeProvider clock, CancellationToken cancellationToken) =>
        RunAsync(
            new ActionRequest(id, CaseAction.RequestInfo, request.EventCount, CaseRules.ValidateAction(request.EventCount, (CaseRules.NoteField, request.Note))),
            context => CaseRules.Trimmed(request.Note) is { } note
                ? Done(context.Case.RequestInfo(context.Caller.Id, note, context.Now))
                : Refused(CaseRules.NoteRequiredReason, CaseRules.RequestInfoNoteRequiredMessage, CaseRules.NoteField),
            new Services(httpContext, dbContext, roles, permissions, clock),
            cancellationToken);

    internal static Task<IResult> ResumeAsync(
        Guid id, CaseNoteRequest request, HttpContext httpContext, AppDbContext dbContext, RequestAccountRole roles,
        RequestAccountPermissions permissions, TimeProvider clock, CancellationToken cancellationToken) =>
        RunAsync(
            new ActionRequest(id, CaseAction.Resume, request.EventCount, CaseRules.ValidateAction(request.EventCount, (CaseRules.NoteField, request.Note))),
            context => Done(context.Case.Resume(context.Caller.Id, request.Note, context.Now)),
            new Services(httpContext, dbContext, roles, permissions, clock),
            cancellationToken);

    internal static Task<IResult> CompleteAsync(
        Guid id, CompleteCaseRequest request, HttpContext httpContext, AppDbContext dbContext, RequestAccountRole roles,
        RequestAccountPermissions permissions, TimeProvider clock, CancellationToken cancellationToken) =>
        RunAsync(
            new ActionRequest(id, CaseAction.Complete, request.EventCount, CaseRules.ValidateAction(request.EventCount, (CaseRules.ResolutionField, request.Resolution))),
            context => CaseRules.Trimmed(request.Resolution) is { } resolution
                ? Done(context.Case.Complete(context.Caller.Id, resolution, context.Now))
                : Refused(CaseRules.ResolutionRequiredReason, CaseRules.ResolutionRequiredMessage, CaseRules.ResolutionField),
            new Services(httpContext, dbContext, roles, permissions, clock),
            cancellationToken);

    internal static Task<IResult> CancelAsync(
        Guid id, CancelCaseRequest request, HttpContext httpContext, AppDbContext dbContext, RequestAccountRole roles,
        RequestAccountPermissions permissions, TimeProvider clock, CancellationToken cancellationToken) =>
        RunAsync(
            new ActionRequest(id, CaseAction.Cancel, request.EventCount, CaseRules.ValidateAction(request.EventCount, (CaseRules.ReasonField, request.Reason))),
            context => CaseRules.Trimmed(request.Reason) is null && CaseActionRules.CancelReasonRequired(context.Case.Status, context.Actor)
                ? Refused(CaseRules.ReasonRequiredReason, CaseRules.ReasonRequiredMessage, CaseRules.ReasonField)
                : Done(context.Case.Cancel(context.Caller.Id, request.Reason, context.Now)),
            new Services(httpContext, dbContext, roles, permissions, clock),
            cancellationToken);

    internal static Task<IResult> TransferAsync(
        Guid id, TransferCaseRequest request, HttpContext httpContext, AppDbContext dbContext, RequestAccountRole roles,
        RequestAccountPermissions permissions, TimeProvider clock, CancellationToken cancellationToken)
    {
        var shape = CaseRules.ValidateAction(request.EventCount, (CaseRules.NoteField, request.Note)).ToList();
        if (request.GroupId is not { } targetId || targetId == Guid.Empty)
        {
            shape.Add(new ValidationFailure(CaseRules.GroupField, CaseRules.GroupRequiredMessage));
        }

        return RunAsync(
            new ActionRequest(id, CaseAction.Transfer, request.EventCount, shape),
            async context =>
            {
                var target = await dbContext.CaseGroups.AsNoTracking()
                    .SingleOrDefaultAsync(group => group.Id == request.GroupId, cancellationToken);
                if (target is null)
                {
                    return await Refused(CaseTypeEndpoints.GroupNotFoundReason, CaseTypeEndpoints.GroupNotFoundMessage, CaseRules.GroupField);
                }

                if (target.IsArchived)
                {
                    return await Refused(CaseTypeEndpoints.GroupArchivedReason, CaseTypeEndpoints.GroupArchivedMessage, CaseRules.GroupField);
                }

                if (target.Id == context.Case.GroupId)
                {
                    return await Refused(GroupUnchangedReason, GroupUnchangedMessage, CaseRules.GroupField);
                }

                return await Done(context.Case.Transfer(context.Caller.Id, target, request.Note, context.Now));
            },
            new Services(httpContext, dbContext, roles, permissions, clock),
            cancellationToken);
    }

    internal static Task<IResult> SetDueAsync(
        Guid id, SetCaseDueRequest request, HttpContext httpContext, AppDbContext dbContext, RequestAccountRole roles,
        RequestAccountPermissions permissions, TimeProvider clock, CancellationToken cancellationToken)
    {
        var shape = CaseRules.ValidateAction(request.EventCount, (CaseRules.NoteField, request.Note)).ToList();
        if (request.DueAt is null)
        {
            shape.Add(new ValidationFailure(CaseRules.DueAtField, CaseRules.DueAtRequiredMessage));
        }

        return RunAsync(
            new ActionRequest(id, CaseAction.SetDue, request.EventCount, shape),
            context => CaseRules.IsDueInPast(request.DueAt!.Value, context.Now)
                ? Refused(CaseEndpoints.DueInPastReason, CaseRules.DueInPastMessage, CaseRules.DueAtField)
                : Done(context.Case.SetDue(context.Caller.Id, request.DueAt.Value, request.Note, context.Now)),
            new Services(httpContext, dbContext, roles, permissions, clock),
            cancellationToken);
    }

    /// <summary>補充: the creator or the case owner adds a note. The creator's note on a 待補件 case also
    /// moves it back to 處理中 (<see cref="CaseActionRules.CommentResumes"/>); the owner's never does.</summary>
    internal static Task<IResult> CommentAsync(
        Guid id, CaseCommentRequest request, HttpContext httpContext, AppDbContext dbContext, RequestAccountRole roles,
        RequestAccountPermissions permissions, TimeProvider clock, CancellationToken cancellationToken) =>
        RunAsync(
            new ActionRequest(id, CaseAction.Comment, request.EventCount, CaseRules.ValidateAction(request.EventCount, (CaseRules.NoteField, request.Note))),
            context => CaseRules.Trimmed(request.Note) is { } note
                ? Done(context.Case.Comment(
                    context.Caller.Id, note, CaseActionRules.CommentResumes(context.Case.Status, context.Actor), context.Now))
                : Refused(CaseRules.NoteRequiredReason, CaseRules.CommentRequiredMessage, CaseRules.NoteField),
            new Services(httpContext, dbContext, roles, permissions, clock),
            cancellationToken);

    private static async Task<IResult> RunAsync(
        ActionRequest request,
        Func<ActionContext, Task<Outcome>> apply,
        Services services,
        CancellationToken cancellationToken)
    {
        var (httpContext, dbContext, roles, permissions, clock) = services;
        if (await CaseEndpoints.CallerAsync(httpContext, roles, cancellationToken) is not { } caller)
        {
            return ApiErrors.Unauthorized();
        }

        if (!caller.IsInternal)
        {
            return CaseEndpoints.Denied();
        }

        // Tracked: the action changes it, and EventCount is the concurrency token of the UPDATE.
        var item = await dbContext.Cases
            .Where(CaseVisibility.VisibleTo(caller.Id, caller.IsManager, dbContext.CaseGroupMembers, dbContext.CaseEvents))
            .SingleOrDefaultAsync(candidate => candidate.Id == request.CaseId, cancellationToken);
        if (item is null)
        {
            return CaseEndpoints.Denied();
        }

        if (request.Shape.Count > 0)
        {
            return ApiErrors.ValidationFailed(request.Shape);
        }

        if (request.EventCount != item.EventCount)
        {
            return Changed();
        }

        var actor = await CaseEndpoints.ActorAsync(dbContext, caller, item, cancellationToken);
        switch (CaseActionRules.Check(request.Action, item.Status, actor))
        {
            case CaseActionCheck.WrongStatus:
                return Changed();
            case CaseActionCheck.NotYours:
                return ApiErrors.Forbidden(ForbiddenReason.CaseAction);
        }

        var outcome = await apply(new ActionContext(item, actor, caller, clock.GetUtcNow()));
        if (outcome.Refusal is { } refusal)
        {
            return refusal;
        }

        dbContext.CaseEvents.Add(outcome.Event!);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Changed();
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception))
        {
            return Changed();
        }

        return Results.Ok(await CaseEndpoints.DetailAsync(httpContext, dbContext, permissions, caller, item, cancellationToken));
    }

    private static void Describe(RouteHandlerBuilder action) =>
        action.Produces<CaseDetailView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

    private static IResult Changed() => ApiErrors.WithReason(StatusCodes.Status409Conflict, ChangedReason, ChangedMessage);

    private static Task<Outcome> Done(CaseEvent caseEvent) => Task.FromResult(new Outcome(caseEvent, null));

    private static Task<Outcome> Refused(string reason, string message, string field) =>
        Task.FromResult(new Outcome(null, CaseEndpoints.Refuse(reason, message, field)));

    private sealed record ActionRequest(Guid CaseId, CaseAction Action, int? EventCount, IReadOnlyList<ValidationFailure> Shape);

    private sealed record ActionContext(Case Case, CaseActor Actor, CaseCaller Caller, DateTimeOffset Now);

    private sealed record Outcome(CaseEvent? Event, IResult? Refusal);

    private sealed record Services(
        HttpContext HttpContext, AppDbContext DbContext, RequestAccountRole Roles, RequestAccountPermissions Permissions, TimeProvider Clock);
}
