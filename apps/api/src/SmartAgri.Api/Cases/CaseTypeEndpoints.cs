using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Cases;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Cases;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Api.Cases;

/// <summary>A case type's default group as the screens show it.</summary>
/// <param name="Archived">Only ever <see langword="true"/> on an inactive type (an active type's
/// default group cannot be archived).</param>
public sealed record CaseTypeGroupView(Guid Id, string Name, bool Archived);

/// <summary>One case type (案件類型).</summary>
/// <param name="Description">What the assistant reads to choose a type (M7-9); empty when there is none.</param>
/// <param name="DefaultDueHours">The default handling time in hours, 1–2,160 (calendar time, decision H).</param>
/// <param name="IsActive">Whether new cases may use it; an inactive type is only in the manager's
/// <c>includeInactive=true</c> list.</param>
public sealed record CaseTypeView(
    Guid Id,
    string Name,
    string Description,
    CaseTypeGroupView DefaultGroup,
    int DefaultDueHours,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary><c>GET /api/v1/case-types</c>'s response.</summary>
/// <param name="Types">Types in the order they were created. Without <c>includeInactive=true</c>
/// (a manager only) the inactive ones are left out.</param>
/// <param name="CanManage">Whether the caller is the organization's manager (may create and change types).</param>
public sealed record CaseTypeListView(IReadOnlyList<CaseTypeView> Types, bool CanManage);

/// <summary><c>POST /api/v1/case-types</c> and <c>PUT /api/v1/case-types/{id}</c>: every field.</summary>
/// <param name="IsActive">Omitted: active for a new type, unchanged for an update.</param>
public sealed record CaseTypeRequest(
    string? Name, string? Description, Guid? DefaultGroupId, int? DefaultDueHours, bool? IsActive);

/// <summary>
/// 案件類型 (M7 plan §3 B, §5 Slice M7-2; issue #247). Every internal account may list the active
/// types — creating a case starts from one — and an external customer gets <c>403 case</c>.
/// Creating and changing are the manager's
/// (<see cref="OrganizationAdminPolicy.RequireOrganizationAdmin{TBuilder}"/>, <c>403
/// organization-settings</c>, the same bytes for an id that does not exist or belongs to another
/// organization). Types are never deleted, only deactivated (decision O).
/// </summary>
/// <remarks>
/// <para>
/// The default group must be one of the organization's groups that is not archived (<c>422
/// case-group-archived</c>; an unknown or foreign id is <c>422 case-group-not-found</c>). An inactive
/// type whose group was archived later may keep it while it stays inactive; reactivating it needs a
/// group in use. The other side is in <see cref="CaseGroupEndpoints"/>: a group that is an active
/// type's default cannot be archived (<c>422 case-group-in-use</c>). A type some database opens cases
/// of cannot be deactivated (<c>422 case-type-in-use</c>, M7-10), naming the databases.
/// </para>
/// <para>
/// Creating and changing each write one <see cref="OrganizationActivity"/> in the same save
/// (decision P); an update that changes nothing writes nothing. Seam for M7-3: a new case reads its
/// type's <see cref="CaseType.DefaultGroupId"/> and <see cref="CaseType.DefaultDueHours"/> through
/// <see cref="FindActiveAsync"/>.
/// </para>
/// </remarks>
public static class CaseTypeEndpoints
{
    public const string Path = "/api/v1/case-types";

    public const string NameTakenReason = "case-type-name-taken";

    public const string NameTakenMessage = "已經有同名的案件類型，請換一個名稱。";

    public const string GroupArchivedReason = "case-group-archived";

    public const string GroupArchivedMessage = "這個承辦組已封存，請選擇其他承辦組。";

    public const string GroupNotFoundReason = "case-group-not-found";

    public const string GroupNotFoundMessage = "找不到這個承辦組，請重新選擇。";

    /// <summary>Deactivating a type that a database opens cases of (M7-10, decision O).</summary>
    public const string InUseReason = "case-type-in-use";

    /// <summary>The field of a <c>case-type-in-use</c> refusal: the databases that use the type.</summary>
    public const string InUseField = "databases";

    public static IEndpointRouteBuilder MapCaseTypeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var types = endpoints.MapGroup(Path);

        types.MapGet(string.Empty, ListAsync)
            .RequireAuthorization()
            .Produces<CaseTypeListView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        types.MapPost(string.Empty, CreateAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<CaseTypeView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        types.MapPut("/{id:guid}", UpdateAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<CaseTypeView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>
    /// Internal accounts only (<c>403 case</c> for an external customer). <paramref name="includeInactive"/>
    /// is honoured for the manager (the settings screen); everyone else always gets only the active
    /// types — what a new case may use.
    /// </summary>
    internal static async Task<IResult> ListAsync(
        bool? includeInactive,
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
        var query = dbContext.CaseTypes.AsNoTracking();
        if (!(isManager && includeInactive == true))
        {
            query = query.Where(type => type.IsActive);
        }

        var types = await query.OrderBy(type => type.CreatedAt).ThenBy(type => type.Id).ToListAsync(cancellationToken);
        return Results.Ok(new CaseTypeListView(await ToViewsAsync(dbContext, types, cancellationToken), isManager));
    }

    /// <summary>
    /// Field rules: <c>422</c> with every failure under its field; a taken name: <c>422
    /// case-type-name-taken</c>; the group: <c>422 case-group-not-found</c> or <c>422
    /// case-group-archived</c>. Writes the type and one <c>case-type-created</c>.
    /// </summary>
    internal static async Task<IResult> CreateAsync(
        CaseTypeRequest request,
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

        var fields = CaseTypeRules.Validate(
            request.Name, request.Description, request.DefaultGroupId, request.DefaultDueHours, request.IsActive);
        if (!fields.IsValid)
        {
            return ApiErrors.ValidationFailed(fields.Failures);
        }

        var value = fields.Value;
        if (await NameTakenAsync(dbContext, value.Name, exceptId: null, cancellationToken))
        {
            return NameTaken();
        }

        if (await FindGroupAsync(dbContext, value.DefaultGroupId, cancellationToken) is not { } group)
        {
            return GroupNotFound();
        }

        if (group.IsArchived)
        {
            return GroupArchived();
        }

        var now = clock.GetUtcNow();
        var type = CaseType.Create(organizationId, value.Name, value.Description, group, value.DefaultDueHours, value.IsActive, now);
        dbContext.CaseTypes.Add(type);
        dbContext.OrganizationActivities.Add(OrganizationActivity.CaseTypeCreated(organizationId, callerId, now, type.Id, type.Name));
        if (await SaveOrNameTakenAsync(dbContext, cancellationToken) is { } refused)
        {
            return refused;
        }

        return Results.Created($"{Path}/{type.Id}", (await ToViewsAsync(dbContext, [type], cancellationToken))[0]);
    }

    /// <summary>
    /// Replaces every field, with the same checks as <see cref="CreateAsync"/>; keeping an archived
    /// group is allowed only on a type that stays (or becomes) inactive. Nothing changed answers
    /// <c>200</c> without writing; otherwise one <c>case-type-updated</c>.
    /// </summary>
    internal static async Task<IResult> UpdateAsync(
        Guid id,
        CaseTypeRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (await dbContext.CaseTypes.SingleOrDefaultAsync(type => type.Id == id, cancellationToken) is not { } type)
        {
            return NotFound();
        }

        var fields = CaseTypeRules.Validate(
            request.Name, request.Description, request.DefaultGroupId, request.DefaultDueHours, request.IsActive ?? type.IsActive);
        if (!fields.IsValid)
        {
            return ApiErrors.ValidationFailed(fields.Failures);
        }

        var value = fields.Value;
        if (!string.Equals(value.Name, type.Name, StringComparison.Ordinal)
            && await NameTakenAsync(dbContext, value.Name, exceptId: type.Id, cancellationToken))
        {
            return NameTaken();
        }

        if (await FindGroupAsync(dbContext, value.DefaultGroupId, cancellationToken) is not { } group)
        {
            return GroupNotFound();
        }

        if (group.IsArchived && (value.IsActive || group.Id != type.DefaultGroupId))
        {
            return GroupArchived();
        }

        if (type.IsActive && !value.IsActive
            && await DatabaseNamesUsingAsync(dbContext, type.Id, cancellationToken) is { Count: > 0 } databaseNames)
        {
            return InUse(type.Name, databaseNames);
        }

        var now = clock.GetUtcNow();
        var changed = type.Update(value.Name, value.Description, group, value.DefaultDueHours, value.IsActive, now);
        if (changed.Count > 0)
        {
            dbContext.OrganizationActivities.Add(OrganizationActivity.CaseTypeUpdated(
                type.OrganizationId, callerId, now, type.Id, type.Name, changed, type.IsActive));
            if (await SaveOrNameTakenAsync(dbContext, cancellationToken) is { } refused)
            {
                return refused;
            }
        }

        return Results.Ok((await ToViewsAsync(dbContext, [type], cancellationToken))[0]);
    }

    /// <summary>
    /// M7-3's seam: the active type a new case is created from, under the organization filter
    /// (<see langword="null"/> for an inactive, unknown or foreign id). Its
    /// <see cref="CaseType.DefaultGroupId"/> and <see cref="CaseType.DefaultDueHours"/> fill in the
    /// case's group and due time.
    /// </summary>
    public static Task<CaseType?> FindActiveAsync(AppDbContext dbContext, Guid id, CancellationToken cancellationToken) =>
        dbContext.CaseTypes.AsNoTracking().SingleOrDefaultAsync(type => type.Id == id && type.IsActive, cancellationToken);

    /// <summary>The names of the active types whose default group is <paramref name="groupId"/>, in
    /// the order they were created: what keeps that group from being archived.</summary>
    internal static Task<List<string>> ActiveTypeNamesUsingGroupAsync(AppDbContext dbContext, Guid groupId, CancellationToken cancellationToken) =>
        dbContext.CaseTypes.AsNoTracking()
            .Where(type => type.DefaultGroupId == groupId && type.IsActive)
            .OrderBy(type => type.CreatedAt).ThenBy(type => type.Id)
            .Select(type => type.Name)
            .ToListAsync(cancellationToken);

    /// <summary>The names of the databases (in use or archived) whose submissions open cases of
    /// <paramref name="typeId"/>, in the order they were created: what keeps the type from being
    /// deactivated (M7-10).</summary>
    internal static Task<List<string>> DatabaseNamesUsingAsync(AppDbContext dbContext, Guid typeId, CancellationToken cancellationToken) =>
        dbContext.Databases.AsNoTracking()
            .Where(database => database.AutoCaseTypeId == typeId)
            .OrderBy(database => database.CreatedAt).ThenBy(database => database.Id)
            .Select(database => database.Name)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// <c>422 case-type-in-use</c> under <see cref="InUseField"/>, naming each database, e.g.
    /// 「設備故障報修」是數據庫「客戶資料庫」送出後自動開案的類型……
    /// </summary>
    private static IResult InUse(string typeName, IReadOnlyList<string> databaseNames)
    {
        var message = $"「{typeName}」是數據庫{string.Join("、", databaseNames.Select(name => $"「{name}」"))}送出後自動開案的類型。"
            + "請先在這些數據庫改選其他類型或關閉自動開案，再停用。";
        return ApiErrors.Refused(InUseReason, message, [new ValidationFailure(InUseField, message)]);
    }

    /// <summary>The same bytes as a non-manager's <c>403</c>.</summary>
    private static IResult NotFound() => ApiErrors.NotFound(ForbiddenReason.OrganizationSettings);

    private static IResult NameTaken() =>
        ApiErrors.WithReason(StatusCodes.Status422UnprocessableEntity, NameTakenReason, NameTakenMessage, field: CaseTypeRules.NameField);

    private static IResult GroupArchived() =>
        ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity, GroupArchivedReason, GroupArchivedMessage, field: CaseTypeRules.DefaultGroupField);

    private static IResult GroupNotFound() =>
        ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity, GroupNotFoundReason, GroupNotFoundMessage, field: CaseTypeRules.DefaultGroupField);

    /// <summary>Under the organization filter: another organization's group is not found.</summary>
    private static Task<CaseGroup?> FindGroupAsync(AppDbContext dbContext, Guid groupId, CancellationToken cancellationToken) =>
        dbContext.CaseGroups.AsNoTracking().SingleOrDefaultAsync(group => group.Id == groupId, cancellationToken);

    private static Task<bool> NameTakenAsync(AppDbContext dbContext, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var sameName = dbContext.CaseTypes.Where(type => type.Name == name);
        if (exceptId is { } except)
        {
            sameName = sameName.Where(type => type.Id != except);
        }

        return sameName.AnyAsync(cancellationToken);
    }

    /// <summary>Saves; a concurrent request that took the same name first loses on the unique index.</summary>
    private static async Task<IResult?> SaveOrNameTakenAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, CaseTypeIndexes.Name))
        {
            return NameTaken();
        }
    }

    private static async Task<List<CaseTypeView>> ToViewsAsync(
        AppDbContext dbContext, IReadOnlyList<CaseType> types, CancellationToken cancellationToken)
    {
        var groupIds = types.Select(type => type.DefaultGroupId).Distinct().ToList();
        var groups = await dbContext.CaseGroups.AsNoTracking()
            .Where(group => groupIds.Contains(group.Id))
            .ToDictionaryAsync(group => group.Id, cancellationToken);
        return [.. types.Select(type =>
        {
            var group = groups[type.DefaultGroupId];
            return new CaseTypeView(
                type.Id,
                type.Name,
                type.Description,
                new CaseTypeGroupView(group.Id, group.Name, group.IsArchived),
                type.DefaultDueHours,
                type.IsActive,
                type.CreatedAt,
                type.UpdatedAt);
        })];
    }
}
