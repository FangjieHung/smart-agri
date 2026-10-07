using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Accounts;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Api.Assistants;

/// <summary>One 處理事項 (M3.5 plan §3/§4, issue #126).</summary>
/// <param name="TestRunId">For a <c>test-failure</c> issue: the run and result it was opened
/// from (plain ids — the run may since have been pruned; the issue keeps its own snapshot in
/// <paramref name="Question"/>, <paramref name="Answer"/> and
/// <paramref name="TestFailureReason"/>).</param>
/// <param name="ViewerIsAssistantOwner">The caller owns the assistant (and may manage
/// assistants); the frontend never compares ids itself.</param>
/// <param name="ViewerIsAssignee">The caller is the assignee (and may handle issues).</param>
/// <param name="ResolutionKind">How it was resolved (M7-7): <c>fixed</c>, or <c>not-assistant-issue</c>
/// (「非助理問題」) when a case was opened from it; <see langword="null"/> unless resolved.</param>
/// <param name="LinkedCaseId">The case opened from it (with <c>not-assistant-issue</c>).</param>
public sealed record AssistantIssueView(
    Guid Id,
    Guid AssistantId,
    string AssistantName,
    AssistantIssueSource Source,
    AssistantIssueStatus Status,
    string Title,
    Guid? AssigneeAccountId,
    string? AssigneeDisplayName,
    Guid? ReporterAccountId,
    string? ReporterDisplayName,
    DateTimeOffset? DueAt,
    Guid? TestRunId,
    Guid? TestResultId,
    AssistantTestFailureReason? TestFailureReason,
    string? Question,
    string? Answer,
    string? ResolutionNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt,
    bool ViewerIsAssistantOwner,
    bool ViewerIsAssignee,
    bool HandoffUnverified,
    AssistantIssueResolutionKind? ResolutionKind,
    Guid? LinkedCaseId);

/// <summary>Only the state and outcome of a member's own forwarded handoff (with M7-7's
/// <paramref name="ResolutionKind"/>, never the case itself: the member may not see it).</summary>
public sealed record ForwardedAssistantIssueView(
    Guid Id, Guid AssistantId, string AssistantName, AssistantIssueStatus Status,
    string? ResolutionNote, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt,
    AssistantIssueResolutionKind? ResolutionKind);

/// <summary>One entry of an issue's handling history, oldest first.</summary>
/// <param name="AssigneeAccountId">For <c>assigned</c> (and <c>created</c>): the assignee
/// afterwards, <see langword="null"/> when unassigned.</param>
/// <param name="Status">For <c>status-changed</c> and <c>created</c>: the status afterwards.</param>
/// <param name="DueAt">For <c>due-date-changed</c> (and <c>created</c>): the due date afterwards.</param>
public sealed record AssistantIssueEventView(
    Guid Id,
    AssistantIssueEventAction Action,
    Guid ActorAccountId,
    string ActorDisplayName,
    DateTimeOffset At,
    string? Note,
    Guid? AssigneeAccountId,
    string? AssigneeDisplayName,
    AssistantIssueStatus? Status,
    DateTimeOffset? DueAt);

/// <summary>The case opened from an issue (M7-7): only whether <b>you</b> can open it now (the case's
/// own visibility, <c>CaseVisibility</c>), never what it holds.</summary>
public sealed record AssistantIssueCaseLinkView(Guid CaseId, bool CanOpen);

/// <summary><c>GET /api/v1/issues/{id}</c> (and <c>PATCH</c>'s response): the issue and its
/// whole history.</summary>
/// <param name="LinkedCase">The case opened from it (M7-7); <see langword="null"/> when there is none.</param>
public sealed record AssistantIssueDetailView(
    AssistantIssueView Issue, IReadOnlyList<AssistantIssueEventView> Events, AssistantIssueCaseLinkView? LinkedCase);

/// <summary><c>GET /api/v1/issues/summary</c>: counts of the unresolved issues the caller can
/// see (the same set as <c>GET /api/v1/issues</c>'s default scope), for the home page's
/// 「待處理事項」 card.</summary>
/// <param name="AssignedToMeCount">Unresolved issues assigned to the caller.</param>
/// <param name="OverdueCount">Unresolved issues whose due date has passed.</param>
public sealed record AssistantIssueSummaryView(int OpenCount, int InProgressCount, int AssignedToMeCount, int OverdueCount);

/// <summary><c>POST /api/v1/assistants/{id}/issues</c>: open an issue from a failed test result
/// of that assistant. <see cref="Title"/> defaults to the question.</summary>
public sealed record CreateAssistantIssueRequest(
    Guid? TestResultId,
    string? Title = null,
    Guid? AssigneeAccountId = null,
    DateTimeOffset? DueAt = null);

/// <summary><c>PATCH /api/v1/issues/{id}</c>: a <see langword="null"/> (or absent) field is left
/// unchanged. <see cref="Unassign"/>/<see cref="ClearDueAt"/> clear the assignee/due date.
/// <see cref="Status"/> is a plain string (an unknown value is this endpoint's own
/// <c>422</c>). <see cref="Note"/> goes on the status change when the status changes (and
/// becomes the resolution note when resolving), otherwise it is recorded as a comment.</summary>
public sealed record UpdateAssistantIssueRequest(
    Guid? AssigneeAccountId = null,
    bool? Unassign = null,
    string? Status = null,
    string? Note = null,
    DateTimeOffset? DueAt = null,
    bool? ClearDueAt = null);

/// <summary>
/// 處理事項 (M3.5 plan §3, §5 Slice 4; issue #126).
/// </summary>
/// <remarks>
/// <para>
/// Creating one is the assistant's configuration (<c>S+MA+OWN</c>): an assistant that does not
/// exist, is another organization's or is not the caller's own gets the exact same
/// <c>403 assistant-configuration</c> as the test-run endpoints.
/// </para>
/// <para>
/// An issue is visible to — and may be changed by — the owner of its assistant while holding
/// <c>manage-assistants</c>, and its assignee while holding <c>handle-assistant-issues</c>.
/// Anyone else, and an id that does not exist or is another organization's, gets the same
/// <c>403 assistant-issue</c> (<see cref="ForbiddenReason.AssistantIssue"/>). The member who
/// forwarded a handoff (#127) is listed under <c>scope=forwarded</c>; what they may open is
/// #127's to add.
/// </para>
/// <para>
/// Only an account of the same organization holding <c>handle-assistant-issues</c> can be
/// assigned (plan §7 decision C); anyone else is <c>422 assignee-not-eligible</c>. Every change
/// records an <see cref="AssistantIssueEvent"/>.
/// </para>
/// </remarks>
public static partial class AssistantIssueEndpoints
{
    public const string TestResultNotFoundReason = "test-result-not-found";
    public const string TestResultPassedReason = "test-result-passed";
    public const string IssueAlreadyOpenReason = "issue-already-open";
    public const string AssigneeNotEligibleReason = "assignee-not-eligible";
    public const string IssueChangedReason = "issue-changed";

    public const string TestResultNotFoundMessage = "找不到這個助理的這筆測試結果。";
    public const string TestResultPassedMessage = "這一題已經通過，只有未通過的題目可以建立處理事項。";
    public const string IssueAlreadyOpenMessage = "這一題已經有尚未解決的處理事項。";
    public const string AssigneeNotEligibleMessage = "只能指派給同一個組織裡、可以處理助理處理事項的帳號。";
    public const string IssueChangedMessage = "這個處理事項剛被其他人更新，請重新整理後再試。";

    /// <summary>The <c>scope</c> values of <c>GET /api/v1/issues</c>.</summary>
    public static readonly IReadOnlyList<string> Scopes = ["all", "owned", "assigned", "forwarded"];

    public static IEndpointRouteBuilder MapAssistantIssueEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/assistants/{id:guid}/issues", CreateAsync)
            .RequireAuthorization()
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantIssueView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        var issues = endpoints.MapGroup("/api/v1/issues").RequireAuthorization();

        issues.MapGet("", ListAsync)
            .Produces<IReadOnlyList<AssistantIssueView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        issues.MapGet("/summary", SummaryAsync)
            .Produces<AssistantIssueSummaryView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        issues.MapGet("/{issueId:guid}", GetAsync)
            .Produces<AssistantIssueDetailView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        issues.MapPatch("/{issueId:guid}", UpdateAsync)
            .Produces<AssistantIssueDetailView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        MapOpenCase(issues);
        return endpoints;
    }

    /// <summary>Opens a <c>test-failure</c> issue (<c>201</c>). The result must be a failed one of
    /// this assistant's runs (<c>422 test-result-not-found</c> / <c>test-result-passed</c>); a result
    /// that already has an unresolved issue is <c>409 issue-already-open</c> (with
    /// <c>issueId</c>).</summary>
    internal static async Task<IResult> CreateAsync(
        Guid id,
        CreateAssistantIssueRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await dbContext.Assistants.AsNoTracking()
            .Where(AssistantAccess.ManageableBy(callerId))
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        if (ValidateTitle(request.Title) is { } titleError)
        {
            return titleError;
        }

        var found = request.TestResultId is { } resultId
            ? await (
                    from result in dbContext.AssistantTestResults.AsNoTracking()
                    join run in dbContext.AssistantTestRuns.AsNoTracking() on result.RunId equals run.Id
                    where result.Id == resultId && run.AssistantId == assistant.Id
                    select new { Result = result, Run = run })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (found is null)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity, TestResultNotFoundReason, TestResultNotFoundMessage, field: "testResultId");
        }

        if (found.Result.Passed)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity, TestResultPassedReason, TestResultPassedMessage, field: "testResultId");
        }

        if (request.AssigneeAccountId is { } assigneeId && !await IsEligibleAssigneeAsync(dbContext, assigneeId, cancellationToken))
        {
            return AssigneeNotEligible();
        }

        var existing = await dbContext.AssistantIssues.AsNoTracking()
            .Where(issue => issue.TestResultId == found.Result.Id && issue.Status != AssistantIssueStatus.Resolved)
            .Select(issue => (Guid?)issue.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is { } existingId)
        {
            return AlreadyOpen(existingId);
        }

        var (created, createdEvent) = AssistantIssue.OpenFromTestFailure(
            assistant.Id, found.Run, found.Result, request.Title, callerId, request.AssigneeAccountId, request.DueAt, clock.GetUtcNow());
        dbContext.AssistantIssues.Add(created);
        dbContext.AssistantIssueEvents.Add(createdEvent);
        await dbContext.SaveChangesAsync(cancellationToken);

        var (canManage, canHandle) = await PermissionsAsync(httpContext, callerId, cancellationToken);
        var view = (await ToViewsAsync(dbContext, [created], callerId, canManage, canHandle, cancellationToken))[0];
        return Results.Created($"/api/v1/issues/{created.Id}", view);
    }

    /// <summary>The caller's issues, newest first. <c>scope</c>: <c>all</c> (default; the caller's
    /// own assistants' issues and those assigned to the caller), <c>owned</c>, <c>assigned</c>, or
    /// <c>forwarded</c> (handoffs the caller forwarded, #127). <c>status</c> and
    /// <c>assistantId</c> narrow it further.</summary>
    internal static async Task<IResult> ListAsync(
        string? scope,
        string? status,
        Guid? assistantId,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var resolvedScope = scope ?? "all";
        if (!Scopes.Contains(resolvedScope))
        {
            var message = $"scope 必須是 {string.Join("、", Scopes)} 其中之一。";
            return ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["scope"] = [message] });
        }

        AssistantIssueStatus? statusFilter = null;
        if (status is not null)
        {
            if (!WireNames<AssistantIssueStatus>.All.Contains(status))
            {
                return InvalidStatus();
            }

            statusFilter = WireNames<AssistantIssueStatus>.Parse(status);
        }

        var (canManage, canHandle) = await PermissionsAsync(httpContext, callerId, cancellationToken);
        var ownedAssistantIds = dbContext.Assistants.Where(AssistantAccess.ManageableBy(callerId)).Select(assistant => assistant.Id);
        var query = dbContext.AssistantIssues.AsNoTracking();
        query = resolvedScope switch
        {
            "owned" => query.Where(issue => canManage && ownedAssistantIds.Contains(issue.AssistantId)),
            "assigned" => query.Where(issue => canHandle && issue.AssigneeAccountId == callerId),
            "forwarded" => query.Where(issue => issue.Source == AssistantIssueSource.Handoff && issue.ReporterAccountId == callerId),
            _ => Visible(dbContext, callerId, canManage, canHandle),
        };

        if (statusFilter is { } wanted)
        {
            query = query.Where(issue => issue.Status == wanted);
        }

        if (assistantId is { } onlyAssistant)
        {
            query = query.Where(issue => issue.AssistantId == onlyAssistant);
        }

        var rows = await query
            .OrderByDescending(issue => issue.CreatedAt)
            .ThenByDescending(issue => issue.Id)
            .ToListAsync(cancellationToken);
        if (resolvedScope == "forwarded")
            return Results.Ok(await ToForwardedViewsAsync(dbContext, rows, cancellationToken));
        return Results.Ok(await ToViewsAsync(dbContext, rows, callerId, canManage, canHandle, cancellationToken));
    }

    internal static async Task<IResult> SummaryAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var (canManage, canHandle) = await PermissionsAsync(httpContext, callerId, cancellationToken);
        var now = clock.GetUtcNow();
        var unresolved = await Visible(dbContext, callerId, canManage, canHandle)
            .Where(issue => issue.Status != AssistantIssueStatus.Resolved)
            .Select(issue => new { issue.Status, issue.AssigneeAccountId, issue.DueAt })
            .ToListAsync(cancellationToken);

        return Results.Ok(new AssistantIssueSummaryView(
            unresolved.Count(issue => issue.Status == AssistantIssueStatus.Open),
            unresolved.Count(issue => issue.Status == AssistantIssueStatus.InProgress),
            canHandle ? unresolved.Count(issue => issue.AssigneeAccountId == callerId) : 0,
            unresolved.Count(issue => issue.DueAt is { } due && due < now)));
    }

    internal static async Task<IResult> GetAsync(
        Guid issueId,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var (canManage, canHandle) = await PermissionsAsync(httpContext, callerId, cancellationToken);
        var issue = await Visible(dbContext, callerId, canManage, canHandle).AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == issueId, cancellationToken);
        if (issue is null)
        {
            var forwarded = await dbContext.AssistantIssues.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == issueId && candidate.Source == AssistantIssueSource.Handoff
                    && candidate.ReporterAccountId == callerId, cancellationToken);
            if (forwarded is null)
                return ApiErrors.NotFound(ForbiddenReason.AssistantIssue);
            var limited = await ToForwardedViewsAsync(dbContext, [forwarded], cancellationToken);
            return Results.Ok(new { Issue = limited[0], Events = Array.Empty<object>() });
        }

        return Results.Ok(await ToDetailAsync(httpContext, dbContext, issue, callerId, canManage, canHandle, cancellationToken));
    }

    /// <summary>Assigns, changes the due date, changes the status and/or adds a note, recording
    /// one event per actual change (in that order). Nothing to change at all is <c>422</c>; a
    /// concurrent change is <c>409 issue-changed</c>.</summary>
    internal static async Task<IResult> UpdateAsync(
        Guid issueId,
        UpdateAssistantIssueRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var (canManage, canHandle) = await PermissionsAsync(httpContext, callerId, cancellationToken);
        var issue = await Visible(dbContext, callerId, canManage, canHandle)
            .SingleOrDefaultAsync(candidate => candidate.Id == issueId, cancellationToken);
        if (issue is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantIssue);
        }

        var unassign = request.Unassign == true;
        var clearDueAt = request.ClearDueAt == true;
        if (unassign && request.AssigneeAccountId is not null)
        {
            const string message = "不能同時指派與取消指派。";
            return ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["unassign"] = [message] });
        }

        if (clearDueAt && request.DueAt is not null)
        {
            const string message = "不能同時設定與清除到期日。";
            return ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["clearDueAt"] = [message] });
        }

        AssistantIssueStatus? newStatus = null;
        if (request.Status is not null)
        {
            if (!WireNames<AssistantIssueStatus>.All.Contains(request.Status))
            {
                return InvalidStatus();
            }

            newStatus = WireNames<AssistantIssueStatus>.Parse(request.Status);
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > AssistantIssueEvent.NoteMaxLength })
        {
            var message = $"備註最多 {AssistantIssueEvent.NoteMaxLength} 字。";
            return ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["note"] = [message] });
        }

        if (!unassign && !clearDueAt && request.AssigneeAccountId is null && request.DueAt is null && newStatus is null && note is null)
        {
            const string message = "沒有要變更的內容。";
            return ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["body"] = [message] });
        }

        if (request.AssigneeAccountId is { } assigneeId
            && assigneeId != issue.AssigneeAccountId
            && !await IsEligibleAssigneeAsync(dbContext, assigneeId, cancellationToken))
        {
            return AssigneeNotEligible();
        }

        var now = clock.GetUtcNow();
        var events = new List<AssistantIssueEvent?>();
        if (unassign || request.AssigneeAccountId is not null)
        {
            events.Add(issue.Assign(unassign ? null : request.AssigneeAccountId, callerId, now));
        }

        if (clearDueAt || request.DueAt is not null)
        {
            events.Add(issue.SetDueAt(clearDueAt ? null : request.DueAt, callerId, now));
        }

        var statusEvent = newStatus is { } target ? issue.ChangeStatus(target, note, callerId, now) : null;
        events.Add(statusEvent);
        if (statusEvent is null && note is not null)
        {
            events.Add(issue.Comment(note, callerId, now));
        }

        dbContext.AssistantIssueEvents.AddRange(events.OfType<AssistantIssueEvent>());
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

        return Results.Ok(await ToDetailAsync(httpContext, dbContext, issue, callerId, canManage, canHandle, cancellationToken));
    }

    /// <summary>Unresolved issues (not <c>resolved</c>) across the organization, and the average
    /// hours from creation to resolution of those resolved within
    /// [<paramref name="fromUtc"/>, <paramref name="toExclusiveUtc"/>) — for
    /// <c>GET /api/v1/operations/summary</c>.</summary>
    public static async Task<(int OpenCount, double? AverageResolutionHours)> OperationsNumbersAsync(
        AppDbContext dbContext, DateTimeOffset fromUtc, DateTimeOffset toExclusiveUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var openCount = await dbContext.AssistantIssues.AsNoTracking()
            .CountAsync(issue => issue.Status != AssistantIssueStatus.Resolved, cancellationToken);
        var resolved = await dbContext.AssistantIssues.AsNoTracking()
            .Where(issue => issue.ResolvedAt != null && issue.ResolvedAt >= fromUtc && issue.ResolvedAt < toExclusiveUtc)
            .Select(issue => new { issue.CreatedAt, ResolvedAt = issue.ResolvedAt!.Value })
            .ToListAsync(cancellationToken);
        double? average = resolved.Count == 0
            ? null
            : resolved.Average(issue => (issue.ResolvedAt - issue.CreatedAt).TotalHours);
        return (openCount, average);
    }

    /// <summary>Whether <paramref name="callerId"/> could open issue <paramref name="issueId"/> now (the
    /// same rule as <c>GET /api/v1/issues/{id}</c>): a case's link to its issue (M7 plan §3 C) only
    /// says this, never what the issue holds.</summary>
    internal static async Task<bool> CanOpenAsync(
        HttpContext httpContext, AppDbContext dbContext, Guid callerId, Guid issueId, CancellationToken cancellationToken)
    {
        var (canManage, canHandle) = await PermissionsAsync(httpContext, callerId, cancellationToken);
        return await Visible(dbContext, callerId, canManage, canHandle).AnyAsync(issue => issue.Id == issueId, cancellationToken);
    }

    /// <summary>The issues the caller may open and change: its own assistants' (with
    /// <c>manage-assistants</c>) and those assigned to it (with
    /// <c>handle-assistant-issues</c>). The organization filter already hides every other
    /// organization's.</summary>
    private static IQueryable<AssistantIssue> Visible(AppDbContext dbContext, Guid callerId, bool canManage, bool canHandle)
    {
        var ownedAssistantIds = dbContext.Assistants.Where(AssistantAccess.ManageableBy(callerId)).Select(assistant => assistant.Id);
        return dbContext.AssistantIssues.Where(issue =>
            (canManage && ownedAssistantIds.Contains(issue.AssistantId))
            || (canHandle && issue.AssigneeAccountId == callerId));
    }

    private static async Task<(bool CanManage, bool CanHandle)> PermissionsAsync(
        HttpContext httpContext, Guid callerId, CancellationToken cancellationToken)
    {
        var granted = await httpContext.RequestServices.GetRequiredService<RequestAccountPermissions>()
            .GetAsync(callerId, cancellationToken);
        return (granted.Contains(AccountPermission.ManageAssistants), granted.Contains(AccountPermission.HandleAssistantIssues));
    }

    /// <summary>An account of the caller's organization (the query filter) holding
    /// <c>handle-assistant-issues</c>.</summary>
    private static Task<bool> IsEligibleAssigneeAsync(AppDbContext dbContext, Guid accountId, CancellationToken cancellationToken) =>
        dbContext.AccountPermissions.AsNoTracking().AnyAsync(
            grant => grant.AccountId == accountId && grant.Permission == AccountPermission.HandleAssistantIssues,
            cancellationToken);

    private static IResult? ValidateTitle(string? title)
    {
        if (title is not null && title.Trim().Length > AssistantIssue.TitleMaxLength)
        {
            var message = $"標題最多 {AssistantIssue.TitleMaxLength} 字。";
            return ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["title"] = [message] });
        }

        return null;
    }

    private static IResult InvalidStatus()
    {
        var message = $"status 必須是 {string.Join("、", WireNames<AssistantIssueStatus>.All)} 其中之一。";
        return ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["status"] = [message] });
    }

    private static IResult AssigneeNotEligible() =>
        ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity, AssigneeNotEligibleReason, AssigneeNotEligibleMessage, field: "assigneeAccountId");

    private static IResult AlreadyOpen(Guid issueId) =>
        ApiErrors.WithReason(
            StatusCodes.Status409Conflict,
            IssueAlreadyOpenReason,
            IssueAlreadyOpenMessage,
            extensions: [new KeyValuePair<string, string>("issueId", issueId.ToString())]);

    private static IResult Changed() =>
        ApiErrors.WithReason(StatusCodes.Status409Conflict, IssueChangedReason, IssueChangedMessage);

    private static async Task<AssistantIssueDetailView> ToDetailAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        AssistantIssue issue,
        Guid callerId,
        bool canManage,
        bool canHandle,
        CancellationToken cancellationToken)
    {
        var events = await dbContext.AssistantIssueEvents.AsNoTracking()
            .Where(issueEvent => issueEvent.IssueId == issue.Id)
            .OrderBy(issueEvent => issueEvent.Ordinal)
            .ToListAsync(cancellationToken);
        var names = await AccountNames.LoadAsync(
            dbContext,
            events.Select(issueEvent => (Guid?)issueEvent.ActorAccountId).Concat(events.Select(issueEvent => issueEvent.AssigneeAccountId)),
            cancellationToken);
        var view = (await ToViewsAsync(dbContext, [issue], callerId, canManage, canHandle, cancellationToken))[0];
        return new AssistantIssueDetailView(
            view,
            events.ConvertAll(issueEvent => new AssistantIssueEventView(
                issueEvent.Id,
                issueEvent.Action,
                issueEvent.ActorAccountId,
                names.NameOf(issueEvent.ActorAccountId),
                issueEvent.At,
                issueEvent.Note,
                issueEvent.AssigneeAccountId,
                names.NameOf(issueEvent.AssigneeAccountId),
                issueEvent.Status,
                issueEvent.DueAt)),
            issue.LinkedCaseId is { } caseId
                ? new AssistantIssueCaseLinkView(caseId, await CanOpenCaseAsync(httpContext, dbContext, caseId, cancellationToken))
                : null);
    }

    private static async Task<List<AssistantIssueView>> ToViewsAsync(
        AppDbContext dbContext,
        IReadOnlyList<AssistantIssue> issues,
        Guid callerId,
        bool canManage,
        bool canHandle,
        CancellationToken cancellationToken)
    {
        var assistantIds = issues.Select(issue => issue.AssistantId).Distinct().ToList();
        var assistants = await dbContext.Assistants.AsNoTracking()
            .Where(assistant => assistantIds.Contains(assistant.Id))
            .Select(assistant => new { assistant.Id, assistant.Name, assistant.OwnerAccountId })
            .ToDictionaryAsync(assistant => assistant.Id, cancellationToken);
        var names = await AccountNames.LoadAsync(
            dbContext, issues.Select(issue => issue.AssigneeAccountId).Concat(issues.Select(issue => issue.ReporterAccountId)), cancellationToken);

        return [.. issues.Select(issue =>
        {
            var assistant = assistants.GetValueOrDefault(issue.AssistantId);
            return new AssistantIssueView(
                issue.Id,
                issue.AssistantId,
                assistant?.Name ?? string.Empty,
                issue.Source,
                issue.Status,
                issue.Title,
                issue.AssigneeAccountId,
                names.NameOf(issue.AssigneeAccountId),
                issue.ReporterAccountId,
                names.NameOf(issue.ReporterAccountId),
                issue.DueAt,
                issue.TestRunId,
                issue.TestResultId,
                issue.TestFailureReason,
                issue.QuestionSnapshot,
                issue.AnswerSnapshot,
                issue.ResolutionNote,
                issue.CreatedAt,
                issue.UpdatedAt,
                issue.ResolvedAt,
                canManage && assistant is not null && assistant.OwnerAccountId == callerId,
                canHandle && issue.AssigneeAccountId == callerId,
                issue.HandoffUnverified,
                issue.ResolutionKind,
                issue.LinkedCaseId);
        })];
    }

    private static async Task<List<ForwardedAssistantIssueView>> ToForwardedViewsAsync(
        AppDbContext dbContext, IReadOnlyList<AssistantIssue> issues, CancellationToken cancellationToken)
    {
        var assistantIds = issues.Select(issue => issue.AssistantId).Distinct().ToList();
        var names = await dbContext.Assistants.AsNoTracking()
            .Where(assistant => assistantIds.Contains(assistant.Id))
            .ToDictionaryAsync(assistant => assistant.Id, assistant => assistant.Name, cancellationToken);
        return [.. issues.Select(issue => new ForwardedAssistantIssueView(
            issue.Id, issue.AssistantId, names.GetValueOrDefault(issue.AssistantId, string.Empty),
            issue.Status, issue.ResolutionNote, issue.CreatedAt, issue.UpdatedAt, issue.ResolvedAt, issue.ResolutionKind))];
    }
}
