using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Accounts;
using SmartAgri.Api.Assistants;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Cases;
using SmartAgri.Application.Chat;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Cases;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Cases;

/// <summary>An account on a case screen: its display name now, or 「已停用的帳號」.</summary>
public sealed record CaseAccountView(Guid Id, string DisplayName);

/// <summary>A case's type by its current name.</summary>
public sealed record CaseTypeRefView(Guid Id, string Name);

/// <summary>A case group by its current name (an archived group still shows on the cases that point at it).</summary>
public sealed record CaseGroupRefView(Guid Id, string Name, bool Archived);

/// <summary>One row of <c>GET /api/v1/cases</c> (no description: the list stays small).</summary>
/// <param name="CreatedBy"><see langword="null"/> only for a case opened from a database submission.</param>
/// <param name="Owner">The case owner (案件負責人); <see langword="null"/> until accepted.</param>
public sealed record CaseSummaryView(
    Guid Id,
    string Title,
    CaseStatus Status,
    CaseOrigin Origin,
    CaseTypeRefView Type,
    CaseGroupRefView Group,
    CaseAccountView? CreatedBy,
    CaseAccountView? Owner,
    DateTimeOffset DueAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary><c>GET /api/v1/cases/attention</c> (逾期提示, issue #250): what needs the caller's attention now.</summary>
/// <param name="OverdueCount">The side navigation's number: <paramref name="OwnedOverdueCount"/> plus
/// <paramref name="GroupPendingOverdueCount"/> (the two never overlap).</param>
/// <param name="OwnedOverdueCount">Overdue cases the caller owns (案件負責人); the list's
/// <c>scope=owned&amp;overdue=true</c>.</param>
/// <param name="GroupPendingOverdueCount">Overdue 待受理 cases in the caller's case groups; the list's
/// <c>scope=my-groups&amp;status=pending&amp;overdue=true</c>.</param>
/// <param name="PendingForMeCount">待我受理: every 待受理 case in the caller's case groups, overdue or not;
/// the list's <c>scope=my-groups&amp;status=pending</c>.</param>
public sealed record CaseAttentionView(int OverdueCount, int OwnedOverdueCount, int GroupPendingOverdueCount, int PendingForMeCount);

/// <summary>One case, as the detail shows it.</summary>
/// <param name="EventCount">The version M7-4's actions send back as <c>eventCount</c>.</param>
public sealed record CaseView(
    Guid Id,
    string Title,
    string Description,
    CaseStatus Status,
    CaseOrigin Origin,
    CaseTypeRefView Type,
    CaseGroupRefView Group,
    CaseAccountView? CreatedBy,
    CaseAccountView? Owner,
    DateTimeOffset DueAt,
    string? Resolution,
    string? CancelReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    int EventCount);

/// <summary>One entry of the case's history, oldest first.</summary>
/// <param name="Actor"><see langword="null"/> when no person did it (a case opened automatically).</param>
public sealed record CaseEventView(
    Guid Id,
    int Ordinal,
    CaseEventAction Action,
    CaseAccountView? Actor,
    DateTimeOffset At,
    string? Note,
    CaseStatus? Status,
    CaseAccountView? Owner,
    CaseGroupRefView? FromGroup,
    CaseGroupRefView? ToGroup,
    DateTimeOffset? DueAt);

/// <summary>What the linked database record is now.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaseRecordLinkState>))]
public enum CaseRecordLinkState
{
    /// <summary>The record is there.</summary>
    [JsonStringEnumMemberName("available")]
    Available,

    /// <summary>The submitter withdrew it (「紀錄已撤回」): the trail stays, its content is gone.</summary>
    [JsonStringEnumMemberName("withdrawn")]
    Withdrawn,

    /// <summary>It can no longer be found (its database was deleted).</summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

/// <summary>The linked database record: its state for everyone who sees the case, and whether
/// <b>you</b> may read it (the case's visibility never widens the record's, case ADR).</summary>
public sealed record CaseRecordLinkView(Guid DatabaseId, Guid SubmissionId, CaseRecordLinkState State, bool CanRead);

/// <summary>The linked conversation: only whether you can open it (yours, and not deleted by
/// retention). Never its title or any of its text.</summary>
public sealed record CaseThreadLinkView(Guid AssistantId, Guid ThreadId, bool CanOpen);

/// <summary>The 處理事項 the case was opened from (M7-7): only whether you can open it.</summary>
public sealed record CaseIssueLinkView(Guid IssueId, bool CanOpen);

/// <summary>The closed case this one continues: only whether you can open it.</summary>
public sealed record CasePreviousLinkView(Guid CaseId, bool CanOpen);

/// <summary>A case's links; each is <see langword="null"/> when the case has none.</summary>
public sealed record CaseLinksView(
    CaseRecordLinkView? Record,
    CaseThreadLinkView? Thread,
    CaseIssueLinkView? AssistantIssue,
    CasePreviousLinkView? PreviousCase);

/// <summary><c>GET /api/v1/cases/{id}</c> (and the response of creating and of every action).</summary>
/// <param name="AllowedActions">What <b>you</b> may do now (<c>CaseActionRules</c>), so the screen shows
/// only those; empty for a closed case (only 「另開新案」 remains).</param>
/// <param name="CancelReasonRequired">Whether <c>:cancel</c> needs a reason from you (not from the
/// creator before acceptance).</param>
public sealed record CaseDetailView(
    CaseView Case,
    IReadOnlyList<CaseEventView> Events,
    CaseLinksView Links,
    IReadOnlyList<CaseAction> AllowedActions,
    bool CancelReasonRequired);

/// <summary><c>POST /api/v1/cases</c>. The type fills in the group and the due time on the screen;
/// both may be changed. Each link is optional; a pair is both set or both absent.</summary>
public sealed record CreateCaseRequest(
    Guid? TypeId,
    Guid? GroupId,
    DateTimeOffset? DueAt,
    string? Title,
    string? Description,
    Guid? DatabaseId = null,
    Guid? SubmissionId = null,
    Guid? AssistantId = null,
    Guid? ThreadId = null,
    Guid? PreviousCaseId = null);

/// <summary>
/// 案件 (M7 plan §3 C, §5 Slice M7-3; issue #248): manual creation, the list and the detail.
/// </summary>
/// <remarks>
/// <para>
/// Internal accounts only. An external customer, and a case that does not exist, belongs to another
/// organization or is not visible (<see cref="CaseVisibility"/>), all get the very same <c>403 case</c>
/// (<see cref="ForbiddenReason.CaseFeature"/>), byte for byte.
/// </para>
/// <para>
/// Creating checks, in order: the fields (every failure at once), the due time (<c>422 due-in-past</c>,
/// decision H), the type (<c>422 case-type-inactive</c>, also for an unknown or foreign id), the group
/// (<c>422 case-group-not-found</c> / <c>case-group-archived</c>) and each link — the record must be one
/// the caller may read now, the thread the caller's own, the previous case visible and closed —
/// otherwise <c>422 link-not-available</c>. The actions (M7-4) are <see cref="CaseActionEndpoints"/>.
/// </para>
/// <para>
/// No response ever carries conversation text: a thread link is only its ids and <c>canOpen</c>.
/// </para>
/// </remarks>
public static class CaseEndpoints
{
    public const string Path = "/api/v1/cases";

    public const string TypeInactiveReason = "case-type-inactive";

    public const string TypeInactiveMessage = "這個案件類型已停用或不存在，請選擇其他類型。";

    public const string DueInPastReason = "due-in-past";

    public const string LinkNotAvailableReason = "link-not-available";

    public const string RecordNotAvailableMessage = "這筆紀錄目前無法連結：你沒有讀取它的權限，或它已撤回、不存在。";

    public const string ThreadNotAvailableMessage = "只能連結你自己的對話，而且對話必須還在。";

    public const string PreviousCaseNotAvailableMessage = "只能連結你看得到、而且已結案的案件。";

    /// <summary>The <c>scope</c> values of <c>GET /api/v1/cases</c>: every case you can see, the ones you
    /// created, the ones you own (案件負責人), and the ones in your case groups.</summary>
    public static readonly IReadOnlyList<string> Scopes = ["all", "created", "owned", "my-groups"];

    /// <summary>The <c>status</c> values besides each status's own wire name: <c>open</c> (the default:
    /// 待受理、處理中、待補件), <c>closed</c> and <c>all</c>.</summary>
    public static readonly IReadOnlyList<string> StatusGroups = ["open", "closed", "all"];

    public static IEndpointRouteBuilder MapCaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var cases = endpoints.MapGroup(Path).RequireAuthorization();

        cases.MapPost(string.Empty, CreateAsync)
            .Produces<CaseDetailView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        cases.MapGet(string.Empty, ListAsync)
            .Produces<List<CaseSummaryView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        cases.MapGet("/attention", AttentionAsync)
            .Produces<CaseAttentionView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        cases.MapGet("/{id:guid}", GetAsync)
            .Produces<CaseDetailView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        cases.MapCaseActionEndpoints();
        return endpoints;
    }

    /// <summary>Creates a <c>manual</c> case in 待受理 with its <c>created</c> event (<c>201</c>, the detail).</summary>
    internal static async Task<IResult> CreateAsync(
        CreateCaseRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountRole roles,
        RequestAccountPermissions permissions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(httpContext, roles, cancellationToken) is not { } caller)
        {
            return ApiErrors.Unauthorized();
        }

        if (!caller.IsInternal)
        {
            return Denied();
        }

        var fields = CaseRules.ValidateCreate(
            request.TypeId, request.GroupId, request.DueAt, request.Title, request.Description,
            request.DatabaseId, request.SubmissionId, request.AssistantId, request.ThreadId, request.PreviousCaseId);
        if (!fields.IsValid)
        {
            return ApiErrors.ValidationFailed(fields.Failures);
        }

        var value = fields.Value;
        var now = clock.GetUtcNow();
        if (CaseRules.IsDueInPast(value.DueAt, now))
        {
            return Refuse(DueInPastReason, CaseRules.DueInPastMessage, CaseRules.DueAtField);
        }

        if (await CaseTypeEndpoints.FindActiveAsync(dbContext, value.TypeId, cancellationToken) is not { } type)
        {
            return Refuse(TypeInactiveReason, TypeInactiveMessage, CaseRules.TypeField);
        }

        var group = await dbContext.CaseGroups.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == value.GroupId, cancellationToken);
        if (group is null)
        {
            return Refuse(CaseTypeEndpoints.GroupNotFoundReason, CaseTypeEndpoints.GroupNotFoundMessage, CaseRules.GroupField);
        }

        if (group.IsArchived)
        {
            return Refuse(CaseTypeEndpoints.GroupArchivedReason, CaseTypeEndpoints.GroupArchivedMessage, CaseRules.GroupField);
        }

        if (await LinkRefusalAsync(dbContext, permissions, caller, value.Links, cancellationToken) is { } refused)
        {
            return refused;
        }

        var (created, createdEvent) = Case.Create(
            CaseOrigin.Manual, type, group, caller.Id, value.Title, value.Description, value.DueAt, value.Links, now);
        dbContext.Cases.Add(created);
        dbContext.CaseEvents.Add(createdEvent);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"{Path}/{created.Id}",
            await DetailAsync(httpContext, dbContext, permissions, caller, created, cancellationToken));
    }

    /// <summary>
    /// The cases the caller can see, newest first, not paged (decision Q). <c>scope</c> narrows them
    /// (<see cref="Scopes"/>); <c>status</c> defaults to <c>open</c>; <c>typeId</c> and <c>groupId</c>
    /// filter by the current type and group; <c>overdue=true</c> keeps only the overdue ones
    /// (<see cref="CaseAttention.Overdue"/>, issue #250; left out or <c>false</c> filters nothing). An
    /// unknown <c>scope</c> or <c>status</c> is <c>422</c>.
    /// </summary>
    internal static async Task<IResult> ListAsync(
        string? scope,
        string? status,
        Guid? typeId,
        Guid? groupId,
        bool? overdue,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountRole roles,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(httpContext, roles, cancellationToken) is not { } caller)
        {
            return ApiErrors.Unauthorized();
        }

        if (!caller.IsInternal)
        {
            return Denied();
        }

        var resolvedScope = scope ?? "all";
        if (!Scopes.Contains(resolvedScope))
        {
            var message = $"scope 必須是 {string.Join("、", Scopes)} 其中之一。";
            return ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["scope"] = [message] });
        }

        var resolvedStatus = status ?? "open";
        IReadOnlyList<CaseStatus>? statuses = resolvedStatus switch
        {
            "open" => CaseStatuses.Open,
            "closed" => CaseStatuses.Closed,
            "all" => null,
            _ when WireNames<CaseStatus>.All.Contains(resolvedStatus) => [WireNames<CaseStatus>.Parse(resolvedStatus)],
            _ => [],
        };
        if (statuses is { Count: 0 })
        {
            var message = $"status 必須是 {string.Join("、", StatusGroups.Concat(WireNames<CaseStatus>.All))} 其中之一。";
            return ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["status"] = [message] });
        }

        var callerId = caller.Id;
        var query = Visible(dbContext, caller);
        query = resolvedScope switch
        {
            "created" => query.Where(item => item.CreatedByAccountId == callerId),
            "owned" => query.Where(CaseAttention.OwnedBy(callerId)),
            "my-groups" => query.Where(CaseAttention.InGroupsOf(callerId, dbContext.CaseGroupMembers)),
            _ => query,
        };

        if (overdue == true)
        {
            query = query.Where(CaseAttention.Overdue(clock.GetUtcNow()));
        }

        if (statuses is not null)
        {
            var wanted = statuses.ToList();
            query = query.Where(item => wanted.Contains(item.Status));
        }

        if (typeId is { } onlyType)
        {
            query = query.Where(item => item.TypeId == onlyType);
        }

        if (groupId is { } onlyGroup)
        {
            query = query.Where(item => item.GroupId == onlyGroup);
        }

        var rows = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(dbContext, rows, [], cancellationToken);
        return Results.Ok(rows.ConvertAll(item => new CaseSummaryView(
            item.Id,
            item.Title,
            item.Status,
            item.Origin,
            names.Type(item.TypeId),
            names.Group(item.GroupId),
            names.Account(item.CreatedByAccountId),
            names.Account(item.OwnerAccountId),
            item.DueAt,
            item.CreatedAt,
            item.UpdatedAt)));
    }

    /// <summary>
    /// 逾期提示 (M7 plan §3 E, decision D; issue #250): the caller's overdue counts and 待我受理, decided
    /// now (<see cref="CaseAttention"/>). Internal accounts only — the admin never asks for an external
    /// customer, who gets the one <c>403 case</c> like everywhere else on cases. The manager gets no extra
    /// count for being the manager.
    /// </summary>
    internal static async Task<IResult> AttentionAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountRole roles,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(httpContext, roles, cancellationToken) is not { } caller)
        {
            return ApiErrors.Unauthorized();
        }

        if (!caller.IsInternal)
        {
            return Denied();
        }

        var sets = CaseAttention.For(Visible(dbContext, caller), dbContext.CaseGroupMembers, caller.Id, clock.GetUtcNow());
        var owned = await sets.OwnedOverdue.CountAsync(cancellationToken);
        var groupPending = await sets.GroupPendingOverdue.CountAsync(cancellationToken);
        var pendingForMe = await sets.PendingForMe.CountAsync(cancellationToken);
        return Results.Ok(new CaseAttentionView(owned + groupPending, owned, groupPending, pendingForMe));
    }

    /// <summary>The case, its history, names and links (<c>403 case</c> unless visible).</summary>
    internal static async Task<IResult> GetAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountRole roles,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(httpContext, roles, cancellationToken) is not { } caller)
        {
            return ApiErrors.Unauthorized();
        }

        if (!caller.IsInternal)
        {
            return Denied();
        }

        var item = await Visible(dbContext, caller).SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null)
        {
            return Denied();
        }

        return Results.Ok(await DetailAsync(httpContext, dbContext, permissions, caller, item, cancellationToken));
    }

    /// <summary>The number of open cases in <paramref name="groupId"/>: what keeps a group from being
    /// archived (<see cref="CaseGroupEndpoints"/>, <c>422 case-group-in-use</c>).</summary>
    internal static Task<int> OpenCaseCountInGroupAsync(AppDbContext dbContext, Guid groupId, CancellationToken cancellationToken)
    {
        var open = CaseStatuses.Open.ToList();
        return dbContext.Cases.AsNoTracking().CountAsync(item => item.GroupId == groupId && open.Contains(item.Status), cancellationToken);
    }

    /// <summary>The cases <paramref name="caller"/> may see (<see cref="CaseVisibility"/>), untracked.</summary>
    internal static IQueryable<Case> Visible(AppDbContext dbContext, CaseCaller caller) =>
        dbContext.Cases.AsNoTracking()
            .Where(CaseVisibility.VisibleTo(caller.Id, caller.IsManager, dbContext.CaseGroupMembers, dbContext.CaseEvents));

    /// <summary>The caller and their role now (read from the database on every request), or
    /// <see langword="null"/> when there is no account of this organization behind the token.</summary>
    internal static async Task<CaseCaller?> CallerAsync(HttpContext httpContext, RequestAccountRole roles, CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId
            || await roles.GetAsync(callerId, cancellationToken) is not { } role)
        {
            return null;
        }

        return new CaseCaller(callerId, CaseGroupRules.IsEligibleMember(role), role == AccountRole.SmbAdmin);
    }

    /// <summary>The one <c>403 case</c>: external customer, unknown id, another organization's, not visible.</summary>
    internal static IResult Denied() => ApiErrors.Forbidden(ForbiddenReason.CaseFeature);

    internal static IResult Refuse(string reason, string message, string field) =>
        ApiErrors.WithReason(StatusCodes.Status422UnprocessableEntity, reason, message, field: field);

    private static async Task<IResult?> LinkRefusalAsync(
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CaseCaller caller,
        CaseLinks links,
        CancellationToken cancellationToken)
    {
        if (links.DatabaseId is { } databaseId && links.SubmissionId is { } submissionId)
        {
            var readable = await DatabaseRecordReaders.CanReadAsync(dbContext, permissions, caller.Id, databaseId, cancellationToken)
                && await dbContext.DatabaseSubmissions.AsNoTracking().AnyAsync(
                    submission => submission.Id == submissionId && submission.DatabaseId == databaseId && submission.WithdrawnAt == null,
                    cancellationToken);
            if (!readable)
            {
                return Refuse(LinkNotAvailableReason, RecordNotAvailableMessage, CaseRules.SubmissionField);
            }
        }

        if (links.ThreadAssistantId is { } assistantId && links.ThreadId is { } threadId
            && !await dbContext.ChatThreads.AsNoTracking()
                .Where(ChatThreadAccess.OwnedBy(caller.Id, assistantId))
                .AnyAsync(thread => thread.Id == threadId, cancellationToken))
        {
            return Refuse(LinkNotAvailableReason, ThreadNotAvailableMessage, CaseRules.ThreadField);
        }

        if (links.PreviousCaseId is { } previousId)
        {
            var closed = CaseStatuses.Closed.ToList();
            if (!await Visible(dbContext, caller).AnyAsync(item => item.Id == previousId && closed.Contains(item.Status), cancellationToken))
            {
                return Refuse(LinkNotAvailableReason, PreviousCaseNotAvailableMessage, CaseRules.PreviousCaseField);
            }
        }

        return null;
    }

    internal static async Task<CaseDetailView> DetailAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CaseCaller caller,
        Case item,
        CancellationToken cancellationToken)
    {
        var events = await dbContext.CaseEvents.AsNoTracking()
            .Where(caseEvent => caseEvent.CaseId == item.Id)
            .OrderBy(caseEvent => caseEvent.Ordinal)
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(dbContext, [item], events, cancellationToken);

        var view = new CaseView(
            item.Id,
            item.Title,
            item.Description,
            item.Status,
            item.Origin,
            names.Type(item.TypeId),
            names.Group(item.GroupId),
            names.Account(item.CreatedByAccountId),
            names.Account(item.OwnerAccountId),
            item.DueAt,
            item.Resolution,
            item.CancelReason,
            item.CreatedAt,
            item.UpdatedAt,
            item.AcceptedAt,
            item.CompletedAt,
            item.CancelledAt,
            item.EventCount);

        var eventViews = events.ConvertAll(caseEvent => new CaseEventView(
            caseEvent.Id,
            caseEvent.Ordinal,
            caseEvent.Action,
            names.Account(caseEvent.ActorAccountId),
            caseEvent.At,
            caseEvent.Note,
            caseEvent.Status,
            names.Account(caseEvent.OwnerAccountId),
            caseEvent.FromGroupId is { } from ? names.Group(from) : null,
            caseEvent.ToGroupId is { } to ? names.Group(to) : null,
            caseEvent.DueAt));

        var actor = await ActorAsync(dbContext, caller, item, cancellationToken);
        return new CaseDetailView(
            view,
            eventViews,
            await LinksAsync(httpContext, dbContext, permissions, caller, item, cancellationToken),
            CaseActionRules.Allowed(item.Status, actor),
            CaseActionRules.CancelReasonRequired(item.Status, actor));
    }

    /// <summary>How <paramref name="caller"/> stands to <paramref name="item"/> now (its current group's
    /// membership read from the database).</summary>
    internal static async Task<CaseActor> ActorAsync(AppDbContext dbContext, CaseCaller caller, Case item, CancellationToken cancellationToken)
    {
        var groupId = item.GroupId;
        var callerId = caller.Id;
        var isMember = await dbContext.CaseGroupMembers.AsNoTracking()
            .AnyAsync(member => member.GroupId == groupId && member.AccountId == callerId, cancellationToken);
        return new CaseActor(
            IsCreator: item.CreatedByAccountId == callerId,
            IsOwner: item.OwnerAccountId == callerId,
            IsGroupMember: isMember,
            IsManager: caller.IsManager);
    }

    /// <summary>Each link's state as of this request; never anything the linked row holds.</summary>
    private static async Task<CaseLinksView> LinksAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CaseCaller caller,
        Case item,
        CancellationToken cancellationToken)
    {
        CaseRecordLinkView? record = null;
        if (item.DatabaseId is { } databaseId && item.SubmissionId is { } submissionId)
        {
            var found = await dbContext.DatabaseSubmissions.AsNoTracking()
                .Where(submission => submission.Id == submissionId && submission.DatabaseId == databaseId)
                .Select(submission => new { submission.WithdrawnAt })
                .SingleOrDefaultAsync(cancellationToken);
            var state = found is null
                ? CaseRecordLinkState.Unavailable
                : found.WithdrawnAt is null ? CaseRecordLinkState.Available : CaseRecordLinkState.Withdrawn;
            var canRead = state == CaseRecordLinkState.Available
                && await DatabaseRecordReaders.CanReadAsync(dbContext, permissions, caller.Id, databaseId, cancellationToken);
            record = new CaseRecordLinkView(databaseId, submissionId, state, canRead);
        }

        CaseThreadLinkView? thread = null;
        if (item.ThreadAssistantId is { } assistantId && item.ThreadId is { } threadId)
        {
            var canOpen = await dbContext.ChatThreads.AsNoTracking()
                .Where(ChatThreadAccess.OwnedBy(caller.Id, assistantId))
                .AnyAsync(candidate => candidate.Id == threadId, cancellationToken);
            thread = new CaseThreadLinkView(assistantId, threadId, canOpen);
        }

        CaseIssueLinkView? issue = null;
        if (item.AssistantIssueId is { } issueId)
        {
            issue = new CaseIssueLinkView(
                issueId, await AssistantIssueEndpoints.CanOpenAsync(httpContext, dbContext, caller.Id, issueId, cancellationToken));
        }

        CasePreviousLinkView? previous = null;
        if (item.PreviousCaseId is { } previousId)
        {
            previous = new CasePreviousLinkView(
                previousId, await Visible(dbContext, caller).AnyAsync(candidate => candidate.Id == previousId, cancellationToken));
        }

        return new CaseLinksView(record, thread, issue, previous);
    }

    private static async Task<CaseNames> NamesAsync(
        AppDbContext dbContext, IReadOnlyList<Case> cases, IReadOnlyList<CaseEvent> events, CancellationToken cancellationToken)
    {
        var typeIds = cases.Select(item => item.TypeId).Distinct().ToList();
        var groupIds = cases.Select(item => item.GroupId)
            .Concat(events.SelectMany(caseEvent => new[] { caseEvent.FromGroupId, caseEvent.ToGroupId }).OfType<Guid>())
            .Distinct()
            .ToList();
        var types = await dbContext.CaseTypes.AsNoTracking()
            .Where(type => typeIds.Contains(type.Id))
            .ToDictionaryAsync(type => type.Id, type => type.Name, cancellationToken);
        var groups = await dbContext.CaseGroups.AsNoTracking()
            .Where(group => groupIds.Contains(group.Id))
            .ToDictionaryAsync(group => group.Id, cancellationToken);
        var accounts = await AccountNames.LoadAsync(
            dbContext,
            cases.SelectMany(item => new[] { item.CreatedByAccountId, item.OwnerAccountId })
                .Concat(events.SelectMany(caseEvent => new[] { caseEvent.ActorAccountId, caseEvent.OwnerAccountId })),
            cancellationToken);
        return new CaseNames(types, groups.ToDictionary(pair => pair.Key, pair => (pair.Value.Name, pair.Value.IsArchived)), accounts);
    }

    /// <summary>Names for the views; types and groups always exist (<c>Restrict</c> foreign keys).</summary>
    private sealed class CaseNames(
        IReadOnlyDictionary<Guid, string> types,
        IReadOnlyDictionary<Guid, (string Name, bool Archived)> groups,
        AccountNameLookup accounts)
    {
        public CaseTypeRefView Type(Guid id) => new(id, types[id]);

        public CaseGroupRefView Group(Guid id) => new(id, groups[id].Name, groups[id].Archived);

        public CaseAccountView? Account(Guid? id) => id is { } accountId ? new CaseAccountView(accountId, accounts.NameOf(accountId)) : null;
    }
}

/// <summary>Who is asking: their id, whether they are an internal account (may use cases at all) and
/// whether they are the organization's manager (sees every case).</summary>
internal sealed record CaseCaller(Guid Id, bool IsInternal, bool IsManager);
