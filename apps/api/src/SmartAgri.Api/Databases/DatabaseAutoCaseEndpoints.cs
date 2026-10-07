using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Cases;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Cases;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Databases;

/// <summary>One active case type a database may open cases of, with what the setting screen warns
/// about: how many members of its default group cannot read this database's records.</summary>
/// <param name="Group">The type's default group: where the cases go.</param>
/// <param name="MemberCount">The group's members now.</param>
/// <param name="UnreadableMemberCount">Of those, how many cannot read this database's records right now
/// (not a designated data manager holding <c>read-consented-submissions</c>): 「承辦組中有 N 人無法讀取這個
/// 數據庫的紀錄」. Seeing a case never lets anyone read its record (case ADR).</param>
public sealed record DatabaseAutoCaseOptionView(
    Guid Id, string Name, CaseTypeGroupView Group, int DefaultDueHours, int MemberCount, int UnreadableMemberCount);

/// <summary><c>GET</c>/<c>PUT /api/v1/databases/{id}/auto-case</c>: the setting and the choices.</summary>
/// <param name="CaseTypeId">The type a submission opens a case of; <see langword="null"/> (sent as
/// <c>null</c>) when submissions open none.</param>
/// <param name="Options">The organization's active case types, in the order they were created (the
/// current one is always among them: a type in use cannot be deactivated).</param>
public sealed record DatabaseAutoCaseView(Guid DatabaseId, Guid? CaseTypeId, IReadOnlyList<DatabaseAutoCaseOptionView> Options);

/// <summary><c>PUT /api/v1/databases/{id}/auto-case</c>: an active case type, or <see langword="null"/>
/// (or omitted) to open no case.</summary>
public sealed record UpdateDatabaseAutoCaseRequest(Guid? CaseTypeId);

/// <summary>
/// 送出後自動開案 (M7 plan §3 I, §5 Slice M7-10; issue #255): the manager sets a database to open a case
/// of one type for every new submission (<see cref="DatabaseSubmissionService"/> opens it in the same
/// save as the record).
/// </summary>
/// <remarks>
/// <para>
/// The manager's only (<see cref="OrganizationAdminPolicy.RequireOrganizationAdmin{TBuilder}"/>, <c>403
/// organization-settings</c>), whether or not they own the database; a database that does not exist or
/// belongs to another organization gets the same bytes. Only an active type may be chosen (<c>422
/// case-type-inactive</c>, also for an unknown or foreign id); the other side is
/// <see cref="CaseTypeEndpoints"/>: a type some database uses cannot be deactivated (<c>422
/// case-type-in-use</c>).
/// </para>
/// <para>
/// A change writes one <c>database-auto-case-changed</c> <see cref="OrganizationActivity"/> in the same
/// save (decision P); choosing what is already set answers <c>200</c> without writing.
/// </para>
/// </remarks>
public static class DatabaseAutoCaseEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseAutoCaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var autoCase = endpoints.MapGroup(DatabaseEndpoints.DatabasesPath + "/{id:guid}/auto-case");

        autoCase.MapGet(string.Empty, GetAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<DatabaseAutoCaseView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        autoCase.MapPut(string.Empty, UpdateAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<DatabaseAutoCaseView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>The setting and every active type with its group's unreadable-member count.</summary>
    internal static async Task<IResult> GetAsync(Guid id, AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var database = await dbContext.Databases.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return database is null
            ? NotFound()
            : Results.Ok(await ViewAsync(dbContext, database.Id, database.AutoCaseTypeId, cancellationToken));
    }

    /// <summary>Sets (or with <c>null</c> clears) the type; one <c>database-auto-case-changed</c> when it changed.</summary>
    internal static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateDatabaseAutoCaseRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var database = await dbContext.Databases.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (database is null)
        {
            return NotFound();
        }

        AutoCaseTypeRef? to = null;
        if (request.CaseTypeId is { } typeId)
        {
            if (await CaseTypeEndpoints.FindActiveAsync(dbContext, typeId, cancellationToken) is not { } type)
            {
                return CaseEndpoints.Refuse(
                    CaseEndpoints.TypeInactiveReason, CaseEndpoints.TypeInactiveMessage, DatabaseAutoCaseRules.CaseTypeField);
            }

            to = new AutoCaseTypeRef(type.Id, type.Name);
        }

        AutoCaseTypeRef? from = null;
        if (database.AutoCaseTypeId is { } previousId)
        {
            var previousName = await dbContext.CaseTypes.AsNoTracking()
                .Where(type => type.Id == previousId)
                .Select(type => type.Name)
                .SingleAsync(cancellationToken);
            from = new AutoCaseTypeRef(previousId, previousName);
        }

        if (database.SetAutoCaseType(to?.Id))
        {
            dbContext.OrganizationActivities.Add(OrganizationActivity.DatabaseAutoCaseChanged(
                database.OrganizationId, callerId, clock.GetUtcNow(), database.Id, database.Name, from, to));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await ViewAsync(dbContext, database.Id, database.AutoCaseTypeId, cancellationToken));
    }

    /// <summary>The same bytes as a non-manager's <c>403</c>.</summary>
    private static IResult NotFound() => ApiErrors.NotFound(ForbiddenReason.OrganizationSettings);

    private static async Task<DatabaseAutoCaseView> ViewAsync(
        AppDbContext dbContext, Guid databaseId, Guid? caseTypeId, CancellationToken cancellationToken)
    {
        var types = await dbContext.CaseTypes.AsNoTracking()
            .Where(type => type.IsActive)
            .OrderBy(type => type.CreatedAt).ThenBy(type => type.Id)
            .ToListAsync(cancellationToken);
        var groupIds = types.Select(type => type.DefaultGroupId).Distinct().ToList();
        var groups = await dbContext.CaseGroups.AsNoTracking()
            .Where(group => groupIds.Contains(group.Id))
            .ToDictionaryAsync(group => group.Id, cancellationToken);
        var members = (await dbContext.CaseGroupMembers.AsNoTracking()
                .Where(member => groupIds.Contains(member.GroupId))
                .Select(member => new { member.GroupId, member.AccountId })
                .ToListAsync(cancellationToken))
            .ToLookup(member => member.GroupId, member => member.AccountId);
        var readers = await DatabaseRecordReaders.EffectiveReaderIdsAsync(dbContext, databaseId, cancellationToken);

        return new DatabaseAutoCaseView(
            databaseId,
            caseTypeId,
            [.. types.Select(type =>
            {
                var group = groups[type.DefaultGroupId];
                var groupMembers = members[type.DefaultGroupId].ToList();
                return new DatabaseAutoCaseOptionView(
                    type.Id,
                    type.Name,
                    new CaseTypeGroupView(group.Id, group.Name, group.IsArchived),
                    type.DefaultDueHours,
                    groupMembers.Count,
                    groupMembers.Count(accountId => !readers.Contains(accountId)));
            })]);
    }
}
