using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Cases;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Cases;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Cases;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Api.Assistants;

/// <summary><c>POST /api/v1/issues/{issueId}:open-case</c> (M7-7): the new case's fields, as
/// <c>POST /api/v1/cases</c> takes them (the screen fills in the issue's title and question copy; both
/// may be changed). The case links the issue by itself; no other link is taken here.</summary>
public sealed record OpenCaseFromIssueRequest(
    Guid? TypeId,
    Guid? GroupId,
    DateTimeOffset? DueAt,
    string? Title,
    string? Description);

/// <summary>The response of 「另開案件」 (<c>201</c>, <c>Location</c> is the case): the new case's id and
/// the issue as it is now (resolved as <c>not-assistant-issue</c>, linked to the case).</summary>
public sealed record AssistantIssueOpenedCaseView(Guid CaseId, AssistantIssueDetailView Issue);

public static partial class AssistantIssueEndpoints
{
    public const string CaseAlreadyOpenedReason = "case-already-opened";

    public const string CaseAlreadyOpenedMessage = "這個處理事項已經另開過案件。";

    /// <summary>
    /// 「另開案件」 (M7 plan §3 G, decision N; issue #252): a 處理事項 that is not the assistant's fault
    /// but business work becomes a case, and the issue is resolved as 「非助理問題」.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whoever may change the issue (<see cref="Visible"/>: the assistant's owner with
    /// <c>manage-assistants</c>, the assignee with <c>handle-assistant-issues</c>) may do it, if they
    /// are also an internal account (cases are internal only). Anyone else — including an external
    /// customer, an id that does not exist and another organization's — gets the same
    /// <c>403 assistant-issue</c>.
    /// </para>
    /// <para>
    /// An issue that already opened a case is <c>409 case-already-opened</c> with that case's
    /// <c>caseId</c> (even after the issue was reopened: one case per issue, a unique index); a
    /// resolved issue is <c>409 issue-changed</c>. The case's fields are then checked exactly like
    /// <c>POST /api/v1/cases</c> (the same <c>422</c>s, in the same order).
    /// </para>
    /// <para>
    /// The case (origin <c>assistant-issue</c>, created by the caller, linking the issue), its
    /// <c>created</c> event, the issue's resolution (<c>not-assistant-issue</c>, linking the case) and
    /// the issue's <c>case-opened</c> event are one <c>SaveChanges</c>, so one transaction: if any of
    /// it fails nothing is written. A concurrent change of the issue (its <c>EventCount</c>
    /// concurrency token) or a concurrent second 「另開案件」 (the unique index) is <c>409</c>.
    /// </para>
    /// </remarks>
    internal static async Task<IResult> OpenCaseAsync(
        Guid issueId,
        OpenCaseFromIssueRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountRole roles,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await CaseEndpoints.CallerAsync(httpContext, roles, cancellationToken) is not { } caller)
        {
            return ApiErrors.Unauthorized();
        }

        var (canManage, canHandle) = await PermissionsAsync(httpContext, caller.Id, cancellationToken);
        var issue = await Visible(dbContext, caller.Id, canManage, canHandle)
            .SingleOrDefaultAsync(candidate => candidate.Id == issueId, cancellationToken);
        if (issue is null || !caller.IsInternal)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantIssue);
        }

        if (await ExistingCaseIdAsync(dbContext, issue.Id, cancellationToken) is { } existingCaseId)
        {
            return CaseAlreadyOpened(existingCaseId);
        }

        if (issue.Status == AssistantIssueStatus.Resolved)
        {
            return Changed();
        }

        var fields = CaseRules.ValidateCreate(
            request.TypeId, request.GroupId, request.DueAt, request.Title, request.Description,
            databaseId: null, submissionId: null, assistantId: null, threadId: null, previousCaseId: null);
        if (!fields.IsValid)
        {
            return ApiErrors.ValidationFailed(fields.Failures);
        }

        var value = fields.Value;
        var now = clock.GetUtcNow();
        if (CaseRules.IsDueInPast(value.DueAt, now))
        {
            return CaseEndpoints.Refuse(CaseEndpoints.DueInPastReason, CaseRules.DueInPastMessage, CaseRules.DueAtField);
        }

        if (await CaseTypeEndpoints.FindActiveAsync(dbContext, value.TypeId, cancellationToken) is not { } type)
        {
            return CaseEndpoints.Refuse(CaseEndpoints.TypeInactiveReason, CaseEndpoints.TypeInactiveMessage, CaseRules.TypeField);
        }

        var group = await dbContext.CaseGroups.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == value.GroupId, cancellationToken);
        if (group is null)
        {
            return CaseEndpoints.Refuse(CaseTypeEndpoints.GroupNotFoundReason, CaseTypeEndpoints.GroupNotFoundMessage, CaseRules.GroupField);
        }

        if (group.IsArchived)
        {
            return CaseEndpoints.Refuse(CaseTypeEndpoints.GroupArchivedReason, CaseTypeEndpoints.GroupArchivedMessage, CaseRules.GroupField);
        }

        var (created, createdEvent) = Case.Create(
            CaseOrigin.AssistantIssue, type, group, caller.Id, value.Title, value.Description, value.DueAt,
            new CaseLinks(AssistantIssueId: issue.Id), now);
        var opened = issue.OpenCase(created.Id, caller.Id, now);
        dbContext.Cases.Add(created);
        dbContext.CaseEvents.Add(createdEvent);
        dbContext.AssistantIssueEvents.Add(opened);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException
                                          || (exception is DbUpdateException update && DatabaseErrors.IsUniqueViolation(update)))
        {
            // Someone changed the issue, or opened its case, at the same moment: nothing of ours was written.
            dbContext.ChangeTracker.Clear();
            return await ExistingCaseIdAsync(dbContext, issueId, cancellationToken) is { } raced
                ? CaseAlreadyOpened(raced)
                : Changed();
        }

        var detail = await ToDetailAsync(httpContext, dbContext, issue, caller.Id, canManage, canHandle, cancellationToken);
        return Results.Created($"{CaseEndpoints.Path}/{created.Id}", new AssistantIssueOpenedCaseView(created.Id, detail));
    }

    private static void MapOpenCase(RouteGroupBuilder issues) =>
        issues.MapPost("/{issueId:guid}:open-case", OpenCaseAsync)
            .Produces<AssistantIssueOpenedCaseView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

    /// <summary>The case opened from <paramref name="issueId"/>, whoever may see it (the organization
    /// filter still applies).</summary>
    private static Task<Guid?> ExistingCaseIdAsync(AppDbContext dbContext, Guid issueId, CancellationToken cancellationToken) =>
        dbContext.Cases.AsNoTracking()
            .Where(item => item.AssistantIssueId == issueId)
            .Select(item => (Guid?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Whether the caller could open case <paramref name="caseId"/> now (<c>CaseVisibility</c>;
    /// never for an external customer).</summary>
    private static async Task<bool> CanOpenCaseAsync(
        HttpContext httpContext, AppDbContext dbContext, Guid caseId, CancellationToken cancellationToken)
    {
        var roles = httpContext.RequestServices.GetRequiredService<RequestAccountRole>();
        return await CaseEndpoints.CallerAsync(httpContext, roles, cancellationToken) is { IsInternal: true } caller
            && await CaseEndpoints.Visible(dbContext, caller).AnyAsync(item => item.Id == caseId, cancellationToken);
    }

    private static IResult CaseAlreadyOpened(Guid caseId) =>
        ApiErrors.WithReason(
            StatusCodes.Status409Conflict,
            CaseAlreadyOpenedReason,
            CaseAlreadyOpenedMessage,
            extensions: [new KeyValuePair<string, string>("caseId", caseId.ToString())]);
}
