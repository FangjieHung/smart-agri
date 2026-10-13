using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenIddict.Abstractions;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Accounts;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Team;

/// <summary>One account the deactivation dialog offers as the successor.</summary>
/// <param name="Eligible">May be chosen (<see cref="MemberOffboardingRules.CheckSuccessor"/>).</param>
/// <param name="IneligibleReason"><see langword="null"/> when eligible.</param>
/// <param name="MissingPermissions">For <c>missing-permissions</c>: which ones (frontend order).</param>
/// <param name="CanHandleIssues">Holds <c>handle-assistant-issues</c>: the member's unresolved 處理事項
/// are assigned to this account; otherwise they become unassigned.</param>
public sealed record SuccessorCandidateView(
    Guid Id,
    string DisplayName,
    AccountRole Role,
    bool Eligible,
    SuccessorIneligibleReason? IneligibleReason,
    IReadOnlyList<AccountPermission> MissingPermissions,
    bool CanHandleIssues);

/// <summary>
/// <c>GET /api/v1/team/members/{id}/offboarding</c>: what deactivating this member would hand over and
/// who may take it (issue #356).
/// </summary>
/// <param name="Block">Why the member cannot be deactivated now, or <see langword="null"/>.</param>
/// <param name="BlockMessage">The message for <paramref name="Block"/>, shown as is.</param>
/// <param name="RequiredPermissions">What the successor must hold (frontend order).</param>
/// <param name="SuccessorRequired">Whether a successor must be chosen: only when the member holds something
/// to hand over (<see cref="MemberOffboardingRules.SuccessorRequired"/>, issue #361). Otherwise the dialog
/// does not offer one and <c>:deactivate</c> accepts <c>successorAccountId: null</c>.</param>
/// <param name="Candidates">Every other account of the organization still in use, in the team's order
/// (role, then name), eligible or not.</param>
public sealed record MemberOffboardingView(
    Guid MemberId,
    string DisplayName,
    MemberDeactivationBlock? Block,
    string? BlockMessage,
    MemberHoldings Holdings,
    IReadOnlyList<AccountPermission> RequiredPermissions,
    bool SuccessorRequired,
    IReadOnlyList<SuccessorCandidateView> Candidates);

/// <summary><c>POST /api/v1/team/members/{id}:deactivate</c> request.</summary>
/// <param name="SuccessorAccountId">Who takes over; may be <see langword="null"/> only when the member holds
/// nothing to hand over (<see cref="MemberOffboardingView.SuccessorRequired"/>). A successor given anyway
/// is still checked.</param>
public sealed record DeactivateMemberRequest(Guid? SuccessorAccountId);

/// <summary><c>POST /api/v1/team/members/{id}:deactivate</c> response: the whole team afterwards (like
/// every other team write) and what went to the successor.</summary>
public sealed record DeactivateMemberResponse(TeamResponse Team, MemberTransferCounts Transferred);

/// <summary>
/// 停用成員並指定接手人 and 重新啟用 (issue #356; owner decision 2026-10-13). Same route group, same
/// <c>manage-assistants</c> policy and the same <c>403 team</c> for an unknown or other-organization id as
/// the rest of <see cref="TeamEndpoints"/>. On top of that, only a manager (<c>smb-admin</c>) deactivates
/// or reactivates another manager (<c>422 admin-required</c>, issue #361), and the successor may be left
/// out when the member holds nothing to hand over (issue #361).
/// </summary>
/// <remarks>
/// <para>
/// Deactivating, in <b>one</b> transaction (a failure anywhere leaves nothing half done): the member's
/// assistants, drafts, knowledge bases and databases become the successor's; on those, the successor's
/// own share row (an owner is never a share target) is dropped and the member's data-manager
/// designation on the transferred databases passes to the successor (with history rows); unresolved
/// 處理事項 assigned to the member go to the successor when they hold <c>handle-assistant-issues</c>, else
/// they become unassigned (both recorded as <c>assigned</c> events); open, accepted cases they own are
/// handed over (<see cref="Case.HandOver"/>). Then the account is marked deactivated, its security stamp
/// rotated (the sign-in cookie stops working), every token entry of it revoked (codes and refresh tokens
/// stop working) and one <c>member-deactivated</c> activity written. Access tokens already issued are
/// refused on their next call by the per-request account check (<c>PasswordChangeGate</c>).
/// </para>
/// <para>
/// Deliberately left alone (the inventory in the PR of #356): shares and designations <b>other</b> owners
/// gave the member, case-group memberships, the member's private conversations (retention applies),
/// their submitted records and every history or audit column.
/// </para>
/// <para>
/// The transaction is serializable, so two managers deactivating each other at the same moment cannot
/// leave the organization without a manager in use: one of them gets <c>409 team-changed</c>.
/// </para>
/// </remarks>
public static class MemberOffboardingEndpoints
{
    public const string BlockedReasonSelf = "cannot-deactivate-self";
    public const string BlockedReasonLastAdmin = "last-admin";
    public const string BlockedReasonAlreadyDeactivated = "member-already-deactivated";
    public const string BlockedReasonAdminRequired = "admin-required";
    public const string SuccessorNotEligibleReason = "successor-not-eligible";
    public const string NotDeactivatedReason = "member-not-deactivated";
    public const string ConflictReason = "team-changed";
    private const string ConflictMessage = "團隊剛被其他人更新，請重新整理後再試一次。";
    private const string MemberField = "memberId";

    private static readonly IReadOnlySet<AccountPermission> NoPermissions = new HashSet<AccountPermission>();

    internal static async Task<IResult> GetOffboardingAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await RoleOfAsync(dbContext, callerId, cancellationToken) is not { } callerRole)
        {
            return ApiErrors.Unauthorized();
        }

        var member = await dbContext.Accounts.AsNoTracking().SingleOrDefaultAsync(account => account.Id == id, cancellationToken);
        if (member is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Team);
        }

        var holdings = await HoldingsAsync(dbContext, member.Id, cancellationToken);
        var required = MemberOffboardingRules.RequiredPermissions(holdings);
        var block = MemberOffboardingRules.CheckDeactivation(
            callerId, callerRole, member.Id, member.Role, member.IsDeactivated, await OtherActiveAdminsAsync(dbContext, member.Id, cancellationToken));

        var others = await dbContext.Accounts.AsNoTracking()
            .Where(account => account.Id != member.Id && account.DeactivatedAt == null)
            .Select(account => new { account.Id, account.DisplayName, account.Role })
            .ToListAsync(cancellationToken);
        var grants = await GrantsAsync(dbContext, others.Select(account => account.Id), cancellationToken);
        var candidates = others
            .OrderBy(account => (int)account.Role)
            .ThenBy(account => account.DisplayName, StringComparer.Ordinal)
            .ThenBy(account => account.Id)
            .Select(account =>
            {
                var granted = grants.GetValueOrDefault(account.Id) ?? NoPermissions;
                var check = MemberOffboardingRules.CheckSuccessor(member.Id, account.Id, account.Role, candidateDeactivated: false, granted, required);
                return new SuccessorCandidateView(
                    account.Id,
                    account.DisplayName,
                    account.Role,
                    check.IsEligible,
                    check.Reason,
                    check.MissingPermissions,
                    granted.Contains(AccountPermission.HandleAssistantIssues));
            })
            .ToList();

        return Results.Ok(new MemberOffboardingView(
            member.Id,
            member.DisplayName,
            block,
            block is { } blocked ? MemberOffboardingRules.MessageOf(blocked) : null,
            holdings,
            required,
            MemberOffboardingRules.SuccessorRequired(holdings),
            candidates));
    }

    internal static async Task<IResult> DeactivateAsync(
        Guid id,
        DeactivateMemberRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        UserManager<Account> userManager,
        IOpenIddictTokenManager tokenManager,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var organizationId = dbContext.OrganizationContext.OrganizationId
            ?? throw new InvalidOperationException("An authenticated request must have a current organization.");

        // Serializable: the last-manager check reads the other managers and then writes, so two
        // managers deactivating each other at the same moment would otherwise both pass it. PostgreSQL
        // aborts one of them instead (serialization failure → 409 below).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        if (await RoleOfAsync(dbContext, callerId, cancellationToken) is not { } callerRole)
        {
            return ApiErrors.Unauthorized();
        }

        var member = await dbContext.Accounts.SingleOrDefaultAsync(account => account.Id == id, cancellationToken);
        if (member is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Team);
        }

        var block = MemberOffboardingRules.CheckDeactivation(
            callerId, callerRole, member.Id, member.Role, member.IsDeactivated, await OtherActiveAdminsAsync(dbContext, member.Id, cancellationToken));
        if (block is { } blocked)
        {
            return Blocked(blocked);
        }

        // Whether a successor is needed is decided on what the member holds now, in this transaction.
        var holdings = await HoldingsAsync(dbContext, member.Id, cancellationToken);
        if (request.SuccessorAccountId is not { } successorId || successorId == Guid.Empty)
        {
            if (MemberOffboardingRules.SuccessorRequired(holdings))
            {
                return ApiErrors.WithReason(
                    StatusCodes.Status422UnprocessableEntity,
                    SuccessorNotEligibleReason,
                    MemberOffboardingRules.SuccessorRequiredMessage,
                    field: MemberOffboardingRules.SuccessorField);
            }

            return await CommitAsync(
                transaction, dbContext, userManager, tokenManager, member, null, NoPermissions, callerId, organizationId, clock, cancellationToken);
        }

        var successor = successorId == member.Id
            ? null
            : await dbContext.Accounts.AsNoTracking().SingleOrDefaultAsync(account => account.Id == successorId, cancellationToken);
        var successorGrants = successor is null
            ? NoPermissions
            : (await GrantsAsync(dbContext, [successor.Id], cancellationToken)).GetValueOrDefault(successor.Id) ?? NoPermissions;
        if (successor is null
            || !MemberOffboardingRules.CheckSuccessor(
                member.Id,
                successor.Id,
                successor.Role,
                successor.IsDeactivated,
                successorGrants,
                MemberOffboardingRules.RequiredPermissions(holdings)).IsEligible)
        {
            // Another organization's account and one that does not exist answer the same: they are
            // simply not a successor the dialog could have offered.
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity,
                SuccessorNotEligibleReason,
                MemberOffboardingRules.SuccessorNotEligibleMessage,
                field: MemberOffboardingRules.SuccessorField);
        }

        return await CommitAsync(
            transaction, dbContext, userManager, tokenManager, member, successor, successorGrants, callerId, organizationId, clock, cancellationToken);
    }

    private static async Task<IResult> CommitAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        AppDbContext dbContext,
        UserManager<Account> userManager,
        IOpenIddictTokenManager tokenManager,
        Account member,
        Account? successor,
        IReadOnlySet<AccountPermission> successorGrants,
        Guid callerId,
        Guid organizationId,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        try
        {
            var transferred = await DeactivateAndTransferAsync(
                dbContext, userManager, tokenManager, member, successor, successorGrants, callerId, organizationId, clock.GetUtcNow(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new DeactivateMemberResponse(
                await TeamEndpoints.BuildTeamResponseAsync(dbContext, callerId, cancellationToken), transferred));
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            return ApiErrors.WithReason(StatusCodes.Status409Conflict, ConflictReason, ConflictMessage);
        }
    }

    private static async Task<MemberTransferCounts> DeactivateAndTransferAsync(
        AppDbContext dbContext,
        UserManager<Account> userManager,
        IOpenIddictTokenManager tokenManager,
        Account member,
        Account? successor,
        IReadOnlySet<AccountPermission> successorGrants,
        Guid callerId,
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // No successor only when the member holds nothing to hand over (checked by the caller in the
        // same transaction), so there is nothing to transfer.
        var transferred = successor is null
            ? new MemberTransferCounts(0, 0, 0, 0, IssuesReassigned: 0, IssuesUnassigned: 0, Cases: 0)
            : await TransferAsync(
                dbContext, member, successor, successorGrants.Contains(AccountPermission.HandleAssistantIssues), callerId, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Rotating the stamp saves the account (DeactivatedAt included) in the same UPDATE, through
        // UserManager like reset-password does, so Identity's concurrency stamp applies.
        member.Deactivate(now);
        var stamped = await userManager.UpdateSecurityStampAsync(member);
        if (!stamped.Succeeded)
        {
            throw new InvalidOperationException("The deactivated account could not be saved: " + string.Join("; ", stamped.Errors.Select(error => error.Code)));
        }

        // Authorization codes, refresh tokens (#354) and access/identity token entries issued so far.
        await tokenManager.RevokeBySubjectAsync(member.Id.ToString("D"), cancellationToken);

        dbContext.OrganizationActivities.Add(OrganizationActivity.MemberDeactivated(
            organizationId,
            callerId,
            now,
            new MemberRef(member.Id, member.DisplayName),
            successor is null ? null : new MemberRef(successor.Id, successor.DisplayName),
            transferred));
        await dbContext.SaveChangesAsync(cancellationToken);
        return transferred;
    }

    internal static async Task<IResult> ReactivateAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await RoleOfAsync(dbContext, callerId, cancellationToken) is not { } callerRole)
        {
            return ApiErrors.Unauthorized();
        }

        var member = await dbContext.Accounts.SingleOrDefaultAsync(account => account.Id == id, cancellationToken);
        if (member is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Team);
        }

        if (!member.IsDeactivated)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity, NotDeactivatedReason, MemberOffboardingRules.NotDeactivatedMessage, field: MemberField);
        }

        if (!MemberOffboardingRules.MayManageRole(callerRole, member.Role))
        {
            return Blocked(MemberDeactivationBlock.AdminRequired);
        }

        member.Reactivate();
        dbContext.OrganizationActivities.Add(OrganizationActivity.MemberReactivated(
            member.OrganizationId, callerId, clock.GetUtcNow(), new MemberRef(member.Id, member.DisplayName)));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(await TeamEndpoints.BuildTeamResponseAsync(dbContext, callerId, cancellationToken));
    }

    /// <summary>What <paramref name="memberId"/> holds now (see <see cref="MemberHoldings"/>).</summary>
    internal static async Task<MemberHoldings> HoldingsAsync(AppDbContext dbContext, Guid memberId, CancellationToken cancellationToken)
    {
        var ownedAssistantIds = dbContext.Assistants.Where(assistant => assistant.OwnerAccountId == memberId).Select(assistant => assistant.Id);

        var borrowedKnowledgeBases = await (
                from link in dbContext.AssistantKnowledgeBases
                join knowledgeBase in dbContext.KnowledgeBases on link.KnowledgeBaseId equals knowledgeBase.Id
                where ownedAssistantIds.Contains(link.AssistantId)
                    && knowledgeBase.OwnerAccountId != memberId
                    && knowledgeBase.SharingScope != KnowledgeSharingScope.Public
                select knowledgeBase.Id)
            .Distinct()
            .CountAsync(cancellationToken);
        var borrowedDatabases = await (
                from link in dbContext.AssistantDatabases
                join database in dbContext.Databases on link.DatabaseId equals database.Id
                where ownedAssistantIds.Contains(link.AssistantId) && database.OwnerAccountId != memberId
                select database.Id)
            .Distinct()
            .CountAsync(cancellationToken);

        return new MemberHoldings(
            Assistants: await ownedAssistantIds.CountAsync(cancellationToken),
            Drafts: await dbContext.AssistantDrafts.CountAsync(draft => draft.OwnerAccountId == memberId, cancellationToken),
            KnowledgeBases: await dbContext.KnowledgeBases.CountAsync(knowledgeBase => knowledgeBase.OwnerAccountId == memberId, cancellationToken),
            Databases: await dbContext.Databases.CountAsync(database => database.OwnerAccountId == memberId, cancellationToken),
            OpenIssues: await dbContext.AssistantIssues.CountAsync(
                issue => issue.AssigneeAccountId == memberId && issue.Status != AssistantIssueStatus.Resolved, cancellationToken),
            OpenCases: await dbContext.Cases.CountAsync(
                item => item.OwnerAccountId == memberId && (item.Status == CaseStatus.InProgress || item.Status == CaseStatus.AwaitingInfo),
                cancellationToken),
            BorrowedSources: borrowedKnowledgeBases + borrowedDatabases);
    }

    private static async Task<MemberTransferCounts> TransferAsync(
        AppDbContext dbContext,
        Account member,
        Account successor,
        bool successorCanHandleIssues,
        Guid actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Tracked throughout, so every change below is a real UPDATE/DELETE carrying the row's own
        // OrganizationId (the write guard's concurrency token), as in TeamEndpoints.
        var assistants = await dbContext.Assistants.Where(assistant => assistant.OwnerAccountId == member.Id).ToListAsync(cancellationToken);
        var assistantIds = assistants.ConvertAll(assistant => assistant.Id);
        assistants.ForEach(assistant => assistant.TransferOwnership(successor.Id));
        dbContext.AssistantShares.RemoveRange(await dbContext.AssistantShares
            .Where(share => assistantIds.Contains(share.AssistantId) && share.AccountId == successor.Id)
            .ToListAsync(cancellationToken));

        var drafts = await dbContext.AssistantDrafts.Where(draft => draft.OwnerAccountId == member.Id).ToListAsync(cancellationToken);
        drafts.ForEach(draft => draft.TransferOwnership(successor.Id));

        var knowledgeBases = await dbContext.KnowledgeBases.Where(knowledgeBase => knowledgeBase.OwnerAccountId == member.Id).ToListAsync(cancellationToken);
        var knowledgeBaseIds = knowledgeBases.ConvertAll(knowledgeBase => knowledgeBase.Id);
        knowledgeBases.ForEach(knowledgeBase => knowledgeBase.TransferOwnership(successor.Id));
        dbContext.KnowledgeBaseShares.RemoveRange(await dbContext.KnowledgeBaseShares
            .Where(share => knowledgeBaseIds.Contains(share.KnowledgeBaseId) && share.AccountId == successor.Id)
            .ToListAsync(cancellationToken));

        var databases = await dbContext.Databases.Where(database => database.OwnerAccountId == member.Id).ToListAsync(cancellationToken);
        var databaseIds = databases.ConvertAll(database => database.Id);
        databases.ForEach(database => database.TransferOwnership(successor.Id));
        var designations = await dbContext.DatabaseDataManagers
            .Where(designation => databaseIds.Contains(designation.DatabaseId)
                && (designation.AccountId == member.Id || designation.AccountId == successor.Id))
            .ToListAsync(cancellationToken);
        foreach (var database in databases)
        {
            // The successor takes the member's place among the data managers (an owner is designated
            // when the database is created); reading records still needs read-consented-submissions.
            if (designations.Find(designation => designation.DatabaseId == database.Id && designation.AccountId == member.Id) is not { } memberDesignation)
            {
                continue;
            }

            dbContext.DatabaseDataManagers.Remove(memberDesignation);
            dbContext.DatabaseDataManagerChanges.Add(DatabaseDataManagerChange.Create(database, member.Id, assigned: false, actorId, now));
            if (!designations.Exists(designation => designation.DatabaseId == database.Id && designation.AccountId == successor.Id))
            {
                dbContext.DatabaseDataManagers.Add(DatabaseDataManager.Create(database, successor.Id, actorId, now));
                dbContext.DatabaseDataManagerChanges.Add(DatabaseDataManagerChange.Create(database, successor.Id, assigned: true, actorId, now));
            }
        }

        var issues = await dbContext.AssistantIssues
            .Where(issue => issue.AssigneeAccountId == member.Id && issue.Status != AssistantIssueStatus.Resolved)
            .ToListAsync(cancellationToken);
        foreach (var issue in issues)
        {
            if (issue.Assign(successorCanHandleIssues ? successor.Id : null, actorId, now) is { } assigned)
            {
                dbContext.AssistantIssueEvents.Add(assigned);
            }
        }

        var cases = await dbContext.Cases
            .Where(item => item.OwnerAccountId == member.Id && (item.Status == CaseStatus.InProgress || item.Status == CaseStatus.AwaitingInfo))
            .ToListAsync(cancellationToken);
        var note = MemberOffboardingRules.CaseHandOverNote(member.DisplayName, successor.DisplayName);
        foreach (var item in cases)
        {
            dbContext.CaseEvents.Add(item.HandOver(successor.Id, note, now));
        }

        return new MemberTransferCounts(
            assistants.Count,
            drafts.Count,
            knowledgeBases.Count,
            databases.Count,
            IssuesReassigned: successorCanHandleIssues ? issues.Count : 0,
            IssuesUnassigned: successorCanHandleIssues ? 0 : issues.Count,
            Cases: cases.Count);
    }

    /// <summary>The caller's role, read from the database like their permissions (the token's <c>role</c>
    /// claim is only as fresh as the token); <see langword="null"/> when the account is gone.</summary>
    private static Task<AccountRole?> RoleOfAsync(AppDbContext dbContext, Guid accountId, CancellationToken cancellationToken) =>
        dbContext.Accounts.AsNoTracking()
            .Where(account => account.Id == accountId)
            .Select(account => (AccountRole?)account.Role)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Managers (<c>smb-admin</c>) in use other than <paramref name="memberId"/>.</summary>
    private static Task<int> OtherActiveAdminsAsync(AppDbContext dbContext, Guid memberId, CancellationToken cancellationToken) =>
        dbContext.Accounts.CountAsync(
            account => account.Id != memberId && account.Role == AccountRole.SmbAdmin && account.DeactivatedAt == null,
            cancellationToken);

    private static async Task<Dictionary<Guid, IReadOnlySet<AccountPermission>>> GrantsAsync(
        AppDbContext dbContext, IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        var ids = accountIds.ToList();
        return (await dbContext.AccountPermissions.AsNoTracking()
                .Where(grant => ids.Contains(grant.AccountId))
                .Select(grant => new { grant.AccountId, grant.Permission })
                .ToListAsync(cancellationToken))
            .GroupBy(grant => grant.AccountId)
            .ToDictionary(group => group.Key, group => (IReadOnlySet<AccountPermission>)group.Select(grant => grant.Permission).ToHashSet());
    }

    /// <summary>PostgreSQL's <c>40001</c>, however EF Core or Identity wrapped it.</summary>
    private static bool IsSerializationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure })
            {
                return true;
            }
        }

        return false;
    }

    private static IResult Blocked(MemberDeactivationBlock block)
    {
        var reason = block switch
        {
            MemberDeactivationBlock.Self => BlockedReasonSelf,
            MemberDeactivationBlock.LastAdmin => BlockedReasonLastAdmin,
            MemberDeactivationBlock.AdminRequired => BlockedReasonAdminRequired,
            _ => BlockedReasonAlreadyDeactivated,
        };
        return ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity, reason, MemberOffboardingRules.MessageOf(block), field: MemberField);
    }
}
