using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Setup;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Api.Team;

/// <summary>
/// One row of <c>GET /api/v1/team</c> / <c>PUT .../members/{id}/permissions</c>. Labels,
/// descriptions and <c>enforcedNote</c> for each permission stay a frontend constant
/// (<c>team.model.ts</c>'s <c>ACCOUNT_PERMISSIONS</c>); the API only sends ids, never a
/// duplicate copy of that catalogue.
/// </summary>
public sealed record TeamMemberResponse(
    Guid Id,
    string DisplayName,
    AccountRole Role,
    IReadOnlyList<AccountPermission> Permissions,
    IReadOnlyList<AccountPermission> LockedPermissions);

/// <summary>
/// <c>GET /api/v1/team</c> response, and also what a successful <c>PUT
/// /api/v1/team/members/{id}/permissions</c> returns (<c>docs/handoff/mock-to-api-mapping.md</c>
/// §2.6: <c>updateMemberPermissions</c> resolves to <c>RepositoryView&lt;TeamView&gt;</c>, same
/// as <c>getTeam</c>) — the whole team, so the caller sees the effect of its own change
/// (including its own <see cref="TeamMemberResponse.LockedPermissions"/>) without a second
/// round trip.
/// </summary>
/// <param name="SavedAt"><see langword="null"/> until the first successful <c>PUT</c> ever
/// made in this organization, then the moment of the most recent one
/// (<c>Organization.TeamPermissionsSavedAt</c>) — one timestamp for the whole team, not one
/// per member, mirroring the mock's <c>StoredTeamPermissions.savedAt</c>.</param>
public sealed record TeamResponse(IReadOnlyList<TeamMemberResponse> Members, DateTimeOffset? SavedAt);

/// <summary>
/// <c>PUT /api/v1/team/members/{id}/permissions</c> request. <see cref="Permissions"/> is
/// plain strings rather than <see cref="AccountPermission"/> on purpose: an unrecognized
/// value must become this endpoint's own <c>422</c> (<c>team.model.ts</c>'s
/// <c>validateMemberPermissions</c>), not a model-binding failure with a different, undocumented
/// shape.
/// </summary>
public sealed record UpdateMemberPermissionsRequest(IReadOnlyList<string>? Permissions);

/// <summary>
/// <c>POST /api/v1/team/members</c> request. <see cref="Role"/> and <see cref="Permissions"/>
/// are plain strings, like <see cref="UpdateMemberPermissionsRequest.Permissions"/>: an
/// unrecognized value becomes this endpoint's own <c>422</c>, not a model-binding failure
/// with a different, undocumented shape.
/// </summary>
public sealed record CreateMemberRequest(
    string? LoginName,
    string? DisplayName,
    string? Role,
    IReadOnlyList<string>? Permissions);

/// <summary>
/// <c>201</c> response of <c>POST /api/v1/team/members</c> (issue #52, M2 plan Slice 18).
/// <see cref="OneTimePassword"/> appears here and only here: it is never logged, never
/// recorded in OpenTelemetry, and never retrievable again afterward — the same guarantee
/// <c>setup</c> gives its administrator's password (<see cref="OneTimePasswordGenerator"/>).
/// </summary>
public sealed record CreateMemberResponse(TeamMemberResponse Member, string OneTimePassword);

/// <summary>
/// <c>GET /api/v1/team</c>, <c>PUT /api/v1/team/members/{id}/permissions</c> and
/// <c>POST /api/v1/team/members</c> (M1 plan, Slice 8; M2 plan, Slice 18 / issue #52):
/// replaces the frontend mock's <c>getTeam</c>/<c>updateMemberPermissions</c>, same rules.
/// All three require <see cref="AccountPermission.ManageAssistants"/>
/// (<see cref="ForbiddenReason.Team"/>); a member id that does not exist, or belongs to
/// another organization, gets the exact same <c>403</c> as "no permission" — never a
/// <c>404</c>, never a message naming the member (<c>tasks-6-10-backend-handoff.md</c> §1.6).
/// </summary>
public static class TeamEndpoints
{
    private const string UnknownPermissionMessage = "有不認得的權限值，這次變更沒有儲存。";

    private const string SelfLockMessage =
        "不能移除自己的「管理助理與團隊」權限：移除後就打不開團隊設定，也沒有別的入口可以加回來。";

    private const string UnknownRoleMessage = "有不認得的角色值，這次沒有新增成員。";

    private const string DuplicateLoginNameMessage = "這個登入名稱在目前組織已經有人使用，請改用其他名稱。";

    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var team = endpoints.MapGroup("/api/v1/team")
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.Team);

        team.MapGet("", GetTeamAsync)
            .Produces<TeamResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        team.MapPut("/members/{id:guid}/permissions", UpdateMemberPermissionsAsync)
            .Produces<TeamResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        team.MapPost("/members", CreateMemberAsync)
            .Produces<CreateMemberResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    internal static async Task<IResult> GetTeamAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        return Results.Ok(await BuildTeamResponseAsync(dbContext, viewerId, cancellationToken));
    }

    /// <summary>
    /// Order of checks mirrors the mock exactly (<c>mock-demo-repository.ts</c>'s
    /// <c>updateMemberPermissions</c>): the caller's own permission was already checked by the
    /// route's policy; then the member must exist (in the caller's organization, enforced by
    /// the query filter); then the requested permissions are validated
    /// (<c>team.model.ts</c>'s <c>validateMemberPermissions</c> — unknown values first, then
    /// the self-lock rule) with nothing written on failure; only then is anything saved.
    /// </summary>
    internal static async Task<IResult> UpdateMemberPermissionsAsync(
        Guid id,
        UpdateMemberPermissionsRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        // Scoped to the caller's organization by AppDbContext's query filter: an id that
        // belongs to another organization is indistinguishable from one that does not exist
        // at all, and both answer with the exact same bytes as "no permission".
        var member = await dbContext.Accounts.SingleOrDefaultAsync(account => account.Id == id, cancellationToken);
        if (member is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Team);
        }

        var requested = new List<AccountPermission>();
        foreach (var raw in request.Permissions ?? [])
        {
            if (!TryParsePermission(raw, out var permission))
            {
                return ValidationFailed(UnknownPermissionMessage);
            }

            requested.Add(permission);
        }

        // Tracked (not AsNoTracking): removed below with RemoveRange, so EF issues real
        // DELETEs carrying the rows' own OrganizationId as the write guard's concurrency
        // token, rather than a hand-built "deleted" row that never touched the database.
        var existingGrants = await dbContext.AccountPermissions
            .Where(grant => grant.AccountId == id)
            .ToListAsync(cancellationToken);
        var currentPermissions = existingGrants.Select(grant => grant.Permission).ToHashSet();

        // Only the caller's own row can lock a permission today (team.model.ts's
        // lockedPermissionsFor): removing your own manage-assistants would close the door
        // behind you, with no other way back into team settings.
        if (id == callerId
            && currentPermissions.Contains(AccountPermission.ManageAssistants)
            && !requested.Contains(AccountPermission.ManageAssistants))
        {
            return ValidationFailed(SelfLockMessage);
        }

        var normalized = Normalize(requested);
        dbContext.AccountPermissions.RemoveRange(existingGrants.Where(grant => !normalized.Contains(grant.Permission)));
        foreach (var permission in normalized)
        {
            if (!currentPermissions.Contains(permission))
            {
                dbContext.AccountPermissions.Add(new AccountPermissionGrant(member, permission));
            }
        }

        var organization = await dbContext.Organizations
            .SingleAsync(candidate => candidate.Id == member.OrganizationId, cancellationToken);
        organization.RecordTeamPermissionsSaved(clock.GetUtcNow());

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(await BuildTeamResponseAsync(dbContext, callerId, cancellationToken));
    }

    /// <summary>
    /// A new member account in the caller's organization (issue #52, M2 plan Slice 18).
    /// Mirrors <c>EfInitialSetupStore.CreateAsync</c>'s account-creation shape (<c>Account.Create</c>
    /// → <c>RequirePasswordChange</c> → <c>UserManager.CreateAsync</c> with a generated one-time
    /// password → grant the requested permissions), inside one transaction so a member is never
    /// left without the permissions it was created with. The one-time password is returned once,
    /// in <see cref="CreateMemberResponse.OneTimePassword"/>, and nowhere else — never logged,
    /// never traced, never stored anywhere but the account's own password hash.
    /// </summary>
    internal static async Task<IResult> CreateMemberAsync(
        CreateMemberRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        UserManager<Account> userManager,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var loginName = request.LoginName?.Trim() ?? string.Empty;
        if (loginName.Length is 0 or > Account.LoginNameMaxLength)
        {
            return ValidationFailed(
                "loginName", $"登入名稱必須是 1 到 {Account.LoginNameMaxLength} 個字元。");
        }

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length is 0 or > Account.DisplayNameMaxLength)
        {
            return ValidationFailed(
                "displayName", $"顯示名稱必須是 1 到 {Account.DisplayNameMaxLength} 個字元。");
        }

        if (!TryParseRole(request.Role, out var role))
        {
            return ValidationFailed("role", UnknownRoleMessage);
        }

        var requested = new List<AccountPermission>();
        foreach (var raw in request.Permissions ?? [])
        {
            if (!TryParsePermission(raw, out var permission))
            {
                return ValidationFailed("permissions", UnknownPermissionMessage);
            }

            requested.Add(permission);
        }

        // Pre-check for the common case (a fast, friendly 422); the unique index on
        // (OrganizationId, NormalizedLoginName) is the real guard against a concurrent
        // request choosing the same name between this check and the insert below.
        var normalizedLogin = Account.NormalizeLoginName(loginName);
        if (await dbContext.Accounts.AnyAsync(account => account.NormalizedLoginName == normalizedLogin, cancellationToken))
        {
            return ValidationFailed("loginName", DuplicateLoginNameMessage);
        }

        var organizationId = dbContext.OrganizationContext.OrganizationId
            ?? throw new InvalidOperationException("An authenticated request must have a current organization.");
        var organization = await dbContext.Organizations
            .SingleAsync(candidate => candidate.Id == organizationId, cancellationToken);

        var account = Account.Create(organization, loginName, displayName, role);
        account.RequirePasswordChange();
        var oneTimePassword = OneTimePasswordGenerator.Generate();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Through UserManager, not a hand-made hash: runs Identity's user-name and password
        // validators with the host's options, exactly as EfInitialSetupStore does.
        var created = await userManager.CreateAsync(account, oneTimePassword);
        if (!created.Succeeded)
        {
            if (created.Errors.Any(error => error.Code is "DuplicateUserName" or "DuplicateEmail"))
            {
                return ValidationFailed("loginName", DuplicateLoginNameMessage);
            }

            var message = created.Errors.FirstOrDefault()?.Description ?? UnknownRoleMessage;
            return ValidationFailed("loginName", message);
        }

        var normalized = Normalize(requested);
        dbContext.AccountPermissions.AddRange(normalized.Select(permission => new AccountPermissionGrant(account, permission)));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception))
        {
            return ValidationFailed("loginName", DuplicateLoginNameMessage);
        }

        await transaction.CommitAsync(cancellationToken);

        var response = new CreateMemberResponse(
            new TeamMemberResponse(account.Id, account.DisplayName, account.Role, normalized, []),
            oneTimePassword);

        return Results.Created($"/api/v1/team/members/{account.Id}", response);
    }

    private static async Task<TeamResponse> BuildTeamResponseAsync(
        AppDbContext dbContext,
        Guid viewerId,
        CancellationToken cancellationToken)
    {
        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Id)
            .Select(account => new { account.Id, account.DisplayName, account.Role })
            .ToListAsync(cancellationToken);

        var grantsByAccount = (await dbContext.AccountPermissions.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(grant => grant.AccountId)
            .ToDictionary(group => group.Key, group => group.Select(grant => grant.Permission).ToHashSet());

        var members = accounts.ConvertAll(account =>
        {
            var granted = grantsByAccount.TryGetValue(account.Id, out var set)
                ? (IReadOnlySet<AccountPermission>)set
                : new HashSet<AccountPermission>();
            var ordered = RequestAccountPermissions.Ordered(granted);
            var locked = account.Id == viewerId && granted.Contains(AccountPermission.ManageAssistants)
                ? new[] { AccountPermission.ManageAssistants }
                : [];

            return new TeamMemberResponse(account.Id, account.DisplayName, account.Role, ordered, locked);
        });

        // The current organization, not the viewer's own row's organization: both are the
        // same value (the query filter already limits everything above to it), but reading
        // it from the context needs no extra round trip when the accounts list is used only
        // for its ids and permissions.
        var organizationId = dbContext.OrganizationContext.OrganizationId
            ?? throw new InvalidOperationException("An authenticated request must have a current organization.");
        var savedAt = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.TeamPermissionsSavedAt)
            .SingleAsync(cancellationToken);

        return new TeamResponse(members, savedAt);
    }

    /// <summary>Distinct, in the frontend's <c>ACCOUNT_PERMISSIONS</c> order
    /// (<c>team.model.ts</c>'s <c>normalizeMemberPermissions</c>).</summary>
    internal static IReadOnlyList<AccountPermission> Normalize(IReadOnlyCollection<AccountPermission> permissions) =>
        AccountPermissionOrder.Sort(permissions);

    internal static bool TryParsePermission(string? raw, out AccountPermission permission)
    {
        if (raw is not null && WireNames<AccountPermission>.All.Contains(raw))
        {
            permission = WireNames<AccountPermission>.Parse(raw);
            return true;
        }

        permission = default;
        return false;
    }

    internal static bool TryParseRole(string? raw, out AccountRole role)
    {
        if (raw is not null && WireNames<AccountRole>.All.Contains(raw))
        {
            role = WireNames<AccountRole>.Parse(raw);
            return true;
        }

        role = default;
        return false;
    }

    private static IResult ValidationFailed(string message) => ValidationFailed("permissions", message);

    private static IResult ValidationFailed(string field, string message) =>
        ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { [field] = [message] });
}
