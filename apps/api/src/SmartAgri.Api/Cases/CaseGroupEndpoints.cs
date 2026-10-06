using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Accounts;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Cases;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Cases;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Api.Cases;

/// <summary>An account as the case group screens show it: its display name now, or
/// 「已停用的帳號」 when it can no longer be found.</summary>
public sealed record CaseGroupAccountView(Guid Id, string DisplayName);

/// <summary>One case group (承辦組).</summary>
/// <param name="Archived">Whether a manager archived it; an archived group is not offered where a
/// group is chosen.</param>
/// <param name="Members">The current members, in the order they were added.</param>
public sealed record CaseGroupView(
    Guid Id,
    string Name,
    bool Archived,
    DateTimeOffset? ArchivedAt,
    IReadOnlyList<CaseGroupAccountView> Members,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>An account the manager may make a member: the organization's own people only
/// (<c>smb-admin</c>, <c>internal-employee</c>).</summary>
public sealed record CaseGroupCandidateView(Guid Id, string DisplayName, AccountRole Role);

/// <summary><c>GET /api/v1/case-groups</c>'s response.</summary>
/// <param name="Groups">Groups in the order they were created. Without
/// <c>includeArchived=true</c> (a manager only) the archived ones are left out.</param>
/// <param name="CanManage">Whether the caller is the organization's manager (may create, rename,
/// archive and change members).</param>
/// <param name="Candidates">Who may be a member (manager only; empty for everyone else).</param>
public sealed record CaseGroupListView(
    IReadOnlyList<CaseGroupView> Groups,
    bool CanManage,
    IReadOnlyList<CaseGroupCandidateView> Candidates);

/// <summary>One addition or removal, newest first in <c>GET …/member-changes</c>.</summary>
public sealed record CaseGroupMemberChangeView(
    Guid Id,
    CaseGroupAccountView Account,
    bool Added,
    CaseGroupAccountView ChangedBy,
    DateTimeOffset ChangedAt);

/// <summary><c>POST /api/v1/case-groups</c> and <c>PUT /api/v1/case-groups/{id}</c>.</summary>
public sealed record CaseGroupNameRequest(string? Name);

/// <summary><c>PUT /api/v1/case-groups/{id}/members</c>: the whole member list.</summary>
public sealed record UpdateCaseGroupMembersRequest(IReadOnlyList<Guid>? AccountIds);

/// <summary>
/// 承辦組 (M7 plan §3 A, §5 Slice M7-1; issue #246). Every internal account may list the groups —
/// creating a case and transferring one need to choose a group — and an external customer gets
/// <c>403 case</c>. Every change is the manager's
/// (<see cref="OrganizationAdminPolicy.RequireOrganizationAdmin{TBuilder}"/>, <c>403
/// organization-settings</c>): for anyone else, and for an id that does not exist or belongs to
/// another organization, the same bytes.
/// </summary>
/// <remarks>
/// Creating, renaming, archiving and unarchiving each write one <see cref="OrganizationActivity"/>
/// in the same save (decision P); member changes have their own history
/// (<see cref="CaseGroupMemberChange"/>, like the data managers'). Groups are never deleted
/// (decision G). A group still in use cannot be archived: the default of an active case type (M7-2)
/// and, from M7-3, open cases (<see cref="ArchiveAsync"/>).
/// </remarks>
public static class CaseGroupEndpoints
{
    public const string Path = "/api/v1/case-groups";

    public const string NameTakenReason = "case-group-name-taken";

    public const string NameTakenMessage = "已經有同名的承辦組，請換一個名稱。";

    public const string MemberNotEligibleReason = "member-not-eligible";

    public const string MemberNotEligibleMessage = "成員只能是組織內部的帳號（管理者或內部同仁），這次的名單沒有儲存。";

    public const string MembersRequiredMessage = "請提供成員名單。";

    public const string MembersConflictReason = "case-group-members-conflict";

    public const string MembersConflictMessage = "承辦組成員剛被其他人更新，請重新整理後再試一次。";

    public const string AccountIdsField = "accountIds";

    public const string InUseReason = "case-group-in-use";

    /// <summary>The field of a <c>case-group-in-use</c> refusal: what still uses the group (M7-2: the
    /// active case types it is the default of; M7-3 adds open cases).</summary>
    public const string InUseField = "caseTypes";

    public static IEndpointRouteBuilder MapCaseGroupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var groups = endpoints.MapGroup(Path);

        groups.MapGet(string.Empty, ListAsync)
            .RequireAuthorization()
            .Produces<CaseGroupListView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        groups.MapPost(string.Empty, CreateAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<CaseGroupView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        groups.MapPut("/{id:guid}", RenameAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<CaseGroupView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        groups.MapPost("/{id:guid}:archive", ArchiveAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<CaseGroupView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        groups.MapPost("/{id:guid}:unarchive", UnarchiveAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<CaseGroupView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        groups.MapPut("/{id:guid}/members", UpdateMembersAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<CaseGroupView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        groups.MapGet("/{id:guid}/member-changes", MemberChangesAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<List<CaseGroupMemberChangeView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>
    /// Internal accounts only (<c>403 case</c> for an external customer). <paramref name="includeArchived"/>
    /// is honoured for the manager (the settings screen); everyone else always gets only the groups in
    /// use — what may be chosen.
    /// </summary>
    internal static async Task<IResult> ListAsync(
        bool? includeArchived,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountRole roles,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId
            || await roles.GetAsync(callerId, cancellationToken) is not { } role)
        {
            return ApiErrors.Unauthorized();
        }

        if (!CaseGroupRules.IsEligibleMember(role))
        {
            return ApiErrors.Forbidden(ForbiddenReason.CaseFeature);
        }

        var isManager = role == AccountRole.SmbAdmin;
        var query = dbContext.CaseGroups.AsNoTracking();
        if (!(isManager && includeArchived == true))
        {
            query = query.Where(group => group.ArchivedAt == null);
        }

        var groups = await query.OrderBy(group => group.CreatedAt).ThenBy(group => group.Id).ToListAsync(cancellationToken);
        var views = await ToViewsAsync(dbContext, groups, cancellationToken);

        var candidates = isManager
            ? (await dbContext.Accounts.AsNoTracking()
                    .Where(account => account.Role == AccountRole.SmbAdmin || account.Role == AccountRole.InternalEmployee)
                    .Select(account => new { account.Id, account.DisplayName, account.Role })
                    .ToListAsync(cancellationToken))
                .OrderBy(account => (int)account.Role)
                .ThenBy(account => account.DisplayName, StringComparer.Ordinal)
                .ThenBy(account => account.Id)
                .Select(account => new CaseGroupCandidateView(account.Id, account.DisplayName, account.Role))
                .ToList()
            : [];

        return Results.Ok(new CaseGroupListView(views, isManager, candidates));
    }

    /// <summary>Name blank or too long: <c>422</c> <c>errors.name</c>; taken in the organization:
    /// <c>422 case-group-name-taken</c>. Writes the group and one <c>case-group-created</c>.</summary>
    internal static async Task<IResult> CreateAsync(
        CaseGroupNameRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId
            || dbContext.OrganizationContext.OrganizationId is not { } organizationId)
        {
            return ApiErrors.Unauthorized();
        }

        var name = CaseGroupRules.ValidateName(request.Name);
        if (!name.IsValid)
        {
            return ApiErrors.ValidationFailed(name.Failures);
        }

        if (await NameTakenAsync(dbContext, name.Value, exceptId: null, cancellationToken))
        {
            return NameTaken();
        }

        var now = clock.GetUtcNow();
        var group = CaseGroup.Create(organizationId, name.Value, now);
        dbContext.CaseGroups.Add(group);
        dbContext.OrganizationActivities.Add(OrganizationActivity.CaseGroupCreated(organizationId, callerId, now, group.Id, group.Name));
        if (await SaveOrNameTakenAsync(dbContext, cancellationToken) is { } refused)
        {
            return refused;
        }

        return Results.Created($"{Path}/{group.Id}", (await ToViewsAsync(dbContext, [group], cancellationToken))[0]);
    }

    /// <summary>Same checks as <see cref="CreateAsync"/>; the same name again answers <c>200</c>
    /// without writing anything. A rename writes one <c>case-group-renamed</c>.</summary>
    internal static async Task<IResult> RenameAsync(
        Guid id,
        CaseGroupNameRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await FindAsync(dbContext, id, cancellationToken) is not { } group)
        {
            return NotFound();
        }

        var name = CaseGroupRules.ValidateName(request.Name);
        if (!name.IsValid)
        {
            return ApiErrors.ValidationFailed(name.Failures);
        }

        var previousName = group.Name;
        if (!string.Equals(name.Value, previousName, StringComparison.Ordinal))
        {
            if (await NameTakenAsync(dbContext, name.Value, exceptId: group.Id, cancellationToken))
            {
                return NameTaken();
            }

            var now = clock.GetUtcNow();
            group.Rename(name.Value, now);
            dbContext.OrganizationActivities.Add(OrganizationActivity.CaseGroupRenamed(
                group.OrganizationId, callerId, now, group.Id, group.Name, previousName));
            if (await SaveOrNameTakenAsync(dbContext, cancellationToken) is { } refused)
            {
                return refused;
            }
        }

        return Results.Ok((await ToViewsAsync(dbContext, [group], cancellationToken))[0]);
    }

    /// <summary>
    /// Archives the group (one <c>case-group-archived</c>); already archived answers <c>200</c>
    /// without writing. A group that is the default of an active case type is <c>422
    /// case-group-in-use</c>, the message naming those types (M7-2); M7-3 adds open cases here.
    /// </summary>
    internal static Task<IResult> ArchiveAsync(
        Guid id, HttpContext httpContext, AppDbContext dbContext, TimeProvider clock, CancellationToken cancellationToken) =>
        SetArchivedAsync(id, archived: true, httpContext, dbContext, clock, cancellationToken);

    /// <summary>Puts the group back in use (one <c>case-group-unarchived</c>); not archived answers
    /// <c>200</c> without writing.</summary>
    internal static Task<IResult> UnarchiveAsync(
        Guid id, HttpContext httpContext, AppDbContext dbContext, TimeProvider clock, CancellationToken cancellationToken) =>
        SetArchivedAsync(id, archived: false, httpContext, dbContext, clock, cancellationToken);

    /// <summary>
    /// Replaces the members with exactly <c>accountIds</c>. Every id must be an internal account of
    /// the organization; one external customer, unknown id or account of another organization (the
    /// last two indistinguishable) is <c>422 member-not-eligible</c> and nothing is written. Each
    /// addition and removal writes one <see cref="CaseGroupMemberChange"/>; a request that changes
    /// nothing writes nothing.
    /// </summary>
    internal static async Task<IResult> UpdateMembersAsync(
        Guid id,
        UpdateCaseGroupMembersRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await FindAsync(dbContext, id, cancellationToken) is not { } group)
        {
            return NotFound();
        }

        if (request.AccountIds is not { } requestedIds)
        {
            return ApiErrors.ValidationFailed(
                MembersRequiredMessage, new Dictionary<string, string[]> { [AccountIdsField] = [MembersRequiredMessage] });
        }

        var requested = requestedIds.Distinct().ToList();
        var eligible = (await dbContext.Accounts.AsNoTracking()
                .Where(account => requested.Contains(account.Id))
                .Select(account => new { account.Id, account.Role })
                .ToListAsync(cancellationToken))
            .Count(account => CaseGroupRules.IsEligibleMember(account.Role));
        if (eligible != requested.Count)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity, MemberNotEligibleReason, MemberNotEligibleMessage, field: AccountIdsField);
        }

        var current = await dbContext.CaseGroupMembers
            .Where(member => member.GroupId == group.Id)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var member in current.Where(member => !requested.Contains(member.AccountId)))
        {
            dbContext.CaseGroupMembers.Remove(member);
            dbContext.CaseGroupMemberChanges.Add(CaseGroupMemberChange.Create(group, member.AccountId, added: false, callerId, now));
        }

        foreach (var accountId in requested.Where(accountId => current.All(member => member.AccountId != accountId)))
        {
            dbContext.CaseGroupMembers.Add(CaseGroupMember.Create(group, accountId, callerId, now));
            dbContext.CaseGroupMemberChanges.Add(CaseGroupMemberChange.Create(group, accountId, added: true, callerId, now));
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two saves adding the same account at once: one loses on the primary key; or a
            // concurrent removal already deleted a row this one removes.
            return ApiErrors.WithReason(StatusCodes.Status409Conflict, MembersConflictReason, MembersConflictMessage);
        }

        return Results.Ok((await ToViewsAsync(dbContext, [group], cancellationToken))[0]);
    }

    /// <summary>The group's member additions and removals, newest first.</summary>
    internal static async Task<IResult> MemberChangesAsync(Guid id, AppDbContext dbContext, CancellationToken cancellationToken)
    {
        if (await FindAsync(dbContext, id, cancellationToken, tracked: false) is not { } group)
        {
            return NotFound();
        }

        var changes = await dbContext.CaseGroupMemberChanges.AsNoTracking()
            .Where(change => change.GroupId == group.Id)
            .OrderByDescending(change => change.ChangedAt)
            .ThenByDescending(change => change.Id)
            .ToListAsync(cancellationToken);
        var names = await AccountNames.LoadAsync(
            dbContext, changes.Select(change => change.AccountId).Concat(changes.Select(change => change.ChangedByAccountId)), cancellationToken);
        return Results.Ok(changes.ConvertAll(change => new CaseGroupMemberChangeView(
            change.Id,
            new CaseGroupAccountView(change.AccountId, names.NameOf(change.AccountId)),
            change.Added,
            new CaseGroupAccountView(change.ChangedByAccountId, names.NameOf(change.ChangedByAccountId)),
            change.ChangedAt)));
    }

    private static async Task<IResult> SetArchivedAsync(
        Guid id, bool archived, HttpContext httpContext, AppDbContext dbContext, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await FindAsync(dbContext, id, cancellationToken) is not { } group)
        {
            return NotFound();
        }

        if (archived && !group.IsArchived
            && await CaseTypeEndpoints.ActiveTypeNamesUsingGroupAsync(dbContext, group.Id, cancellationToken) is { Count: > 0 } typeNames)
        {
            return InUse(group.Name, typeNames);
        }

        var now = clock.GetUtcNow();
        if (archived ? group.Archive(now) : group.Unarchive(now))
        {
            dbContext.OrganizationActivities.Add(OrganizationActivity.CaseGroupArchiveChanged(
                group.OrganizationId, callerId, now, group.Id, group.Name, archived));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok((await ToViewsAsync(dbContext, [group], cancellationToken))[0]);
    }

    /// <summary>
    /// <c>422 case-group-in-use</c>, naming the active types that default to the group, e.g.
    /// 「設備組」是啟用中的案件類型「設備故障報修」、「冷藏庫異常」的預設承辦組……
    /// </summary>
    private static IResult InUse(string groupName, IReadOnlyList<string> typeNames) =>
        ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity,
            InUseReason,
            $"「{groupName}」是啟用中的案件類型{string.Join("、", typeNames.Select(name => $"「{name}」"))}的預設承辦組。"
            + "請先替這些類型換一個承辦組，或停用它們，再封存。",
            field: InUseField);

    /// <summary>The group under the organization filter: another organization's id is not found,
    /// exactly like an id that does not exist.</summary>
    private static Task<CaseGroup?> FindAsync(AppDbContext dbContext, Guid id, CancellationToken cancellationToken, bool tracked = true)
    {
        var groups = tracked ? dbContext.CaseGroups : dbContext.CaseGroups.AsNoTracking();
        return groups.SingleOrDefaultAsync(group => group.Id == id, cancellationToken);
    }

    /// <summary>The same bytes as a non-manager's <c>403</c> (the endpoint's reason metadata).</summary>
    private static IResult NotFound() => ApiErrors.NotFound(ForbiddenReason.OrganizationSettings);

    private static IResult NameTaken() =>
        ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity, NameTakenReason, NameTakenMessage, field: CaseGroupRules.NameField);

    private static Task<bool> NameTakenAsync(AppDbContext dbContext, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var sameName = dbContext.CaseGroups.Where(group => group.Name == name);
        if (exceptId is { } except)
        {
            sameName = sameName.Where(group => group.Id != except);
        }

        return sameName.AnyAsync(cancellationToken);
    }

    /// <summary>Saves; a concurrent request that took the same name first loses on the unique index
    /// and gets the same <c>422</c> as the application check.</summary>
    private static async Task<IResult?> SaveOrNameTakenAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, CaseGroupIndexes.Name))
        {
            return NameTaken();
        }
    }

    private static async Task<List<CaseGroupView>> ToViewsAsync(
        AppDbContext dbContext, IReadOnlyList<CaseGroup> groups, CancellationToken cancellationToken)
    {
        var groupIds = groups.Select(group => group.Id).ToList();
        var members = (await dbContext.CaseGroupMembers.AsNoTracking()
                .Where(member => groupIds.Contains(member.GroupId))
                .ToListAsync(cancellationToken))
            .OrderBy(member => member.AddedAt)
            .ThenBy(member => member.AccountId)
            .ToList();
        var names = await AccountNames.LoadAsync(dbContext, members.Select(member => member.AccountId), cancellationToken);
        return [.. groups.Select(group => new CaseGroupView(
            group.Id,
            group.Name,
            group.IsArchived,
            group.ArchivedAt,
            [.. members.Where(member => member.GroupId == group.Id)
                .Select(member => new CaseGroupAccountView(member.AccountId, names.NameOf(member.AccountId)))],
            group.CreatedAt,
            group.UpdatedAt))];
    }
}
