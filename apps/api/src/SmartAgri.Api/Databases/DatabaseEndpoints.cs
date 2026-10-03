using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Databases;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Databases;

// View records are named after, and shaped like, the frontend's views in
// apps/admin/src/app/core/domain/database.model.ts, so the generated types line up. Differences,
// all deliberate (docs/plans/2026-10-03-m4-142-database-templates.md):
// - ids are GUIDs (the frontend widens DatabaseId to string for API mode);
// - DatabaseSummaryView has no recordCount / subjectCount: reading records needs the data-manager
//   designation and the read-consented-submissions permission (#144/#146), so until then no
//   response says anything about records; the frontend adapter maps them to null;
// - DatabaseSummaryView adds owner, templateId, formVersion, createdAt and viewerCanManage;
// - connectedAssistantNames / connectedAssistants are absent: assistant connections are #148;
//   `access` (data managers, #144) is part of the detail, see DatabaseAccessView;
// - DatabaseDetailView carries the current form as `form` (version number, when it was saved,
//   fields) instead of a bare `fields` array, so #143 can add versions without a new shape.

/// <summary>A scale field's range.</summary>
public sealed record DatabaseScaleRangeView(int Min, int Max, string MinLabel, string MaxLabel);

/// <summary>One field of a form, in display order. <see cref="Options"/> is empty unless the
/// type is a choice, <see cref="Scale"/> is <see langword="null"/> unless it is a scale (sent as
/// <c>null</c>, never omitted), <see cref="Unit"/> is empty unless it is a number.</summary>
/// <param name="Id">The field's stable key across form versions (<c>field-…</c>).</param>
public sealed record DatabaseFieldView(
    string Id,
    string Label,
    DatabaseFieldType Type,
    bool Required,
    IReadOnlyList<string> Options,
    DatabaseScaleRangeView? Scale,
    string Unit)
{
    public static DatabaseFieldView From(DatabaseFormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new(
            field.Id,
            field.Label,
            field.Type,
            field.Required,
            [.. field.Options],
            field.Scale is { } scale ? new DatabaseScaleRangeView(scale.Min, scale.Max, scale.MinLabel, scale.MaxLabel) : null,
            field.Unit);
    }
}

/// <summary>One row of <c>GET /api/v1/database-templates</c>.</summary>
public sealed record DatabaseTemplateView(
    DatabaseTemplateId Id,
    string Name,
    string Description,
    IReadOnlyList<DatabaseFieldView> Fields);

/// <summary>An account shown by name: id and display name only.</summary>
public sealed record DatabaseAccountView(Guid Id, string DisplayName);

/// <summary>
/// One row of <c>GET /api/v1/databases</c>, the <c>summary</c> of the detail, and the response
/// of <c>POST</c>. Never carries a record or subject count (see the file note).
/// </summary>
/// <param name="TemplateName">The template's name (e.g. 滿意度調查), for display.</param>
/// <param name="FieldCount">Fields of the current form.</param>
/// <param name="FormVersion">The current form's version number (1 right after creation).</param>
/// <param name="UpdatedAt">The later of the last change to the database row and the current
/// form version's creation.</param>
/// <param name="ViewerCanManage">Whether the caller may open and change it
/// (<see cref="DatabaseAccess.CanManage"/>).</param>
public sealed record DatabaseSummaryView(
    Guid Id,
    string Name,
    string Purpose,
    DatabaseTemplateId TemplateId,
    string TemplateName,
    int FieldCount,
    int FormVersion,
    DatabaseAccountView Owner,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool ViewerCanManage);

/// <summary>A form version: its number, when and by whom it was saved, and its fields.</summary>
public sealed record DatabaseFormView(
    Guid Id,
    int VersionNumber,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DatabaseFieldView> Fields);

/// <summary>One designated data manager (M4 #144).</summary>
/// <param name="Account">Who is designated.</param>
/// <param name="HasReadPermission">Whether the account holds <c>read-consented-submissions</c>
/// right now; <see langword="false"/> means designated but still unable to read.</param>
/// <param name="AssignedAt">When this designation was made.</param>
/// <param name="AssignedBy">The owner who made it.</param>
public sealed record DatabaseDataManagerView(
    DatabaseAccountView Account,
    bool HasReadPermission,
    DateTimeOffset AssignedAt,
    DatabaseAccountView AssignedBy);

/// <summary>An account the owner may designate (any account of the organization).</summary>
public sealed record DatabaseAccessCandidateView(Guid Id, string DisplayName, AccountRole Role, bool HasReadPermission);

/// <summary>The latest change to the designations: who and when.</summary>
public sealed record DatabaseAccessChangeView(DateTimeOffset ChangedAt, DatabaseAccountView ChangedBy);

/// <summary>
/// Who may read this database's consented records (M4 #144). <see cref="DataManagers"/> is who is
/// <b>designated</b>; <see cref="EffectiveReaders"/> is who can <b>actually read right now</b>
/// (designated and holding the permission): the owner's preview.
/// </summary>
/// <param name="Owner">The owner (never implies reading).</param>
/// <param name="DataManagers">Designated accounts, in the order they were designated.</param>
/// <param name="EffectiveReaders">The designated accounts that currently hold the permission.</param>
/// <param name="ViewerIsDataManager">Whether the caller is designated.</param>
/// <param name="ViewerCanReadRecords">Whether the caller may read records
/// (<see cref="DatabaseRecordAccess.CanRead"/>).</param>
/// <param name="ViewerCanManageAccess">Whether the caller may change the designations (owner only).</param>
/// <param name="Candidates">Who may be designated; empty unless the caller is the owner.</param>
/// <param name="LastChange">The latest designation change; <see langword="null"/> when none was
/// ever made (sent as <c>null</c>).</param>
public sealed record DatabaseAccessView(
    DatabaseAccountView Owner,
    IReadOnlyList<DatabaseDataManagerView> DataManagers,
    IReadOnlyList<DatabaseAccountView> EffectiveReaders,
    bool ViewerIsDataManager,
    bool ViewerCanReadRecords,
    bool ViewerCanManageAccess,
    IReadOnlyList<DatabaseAccessCandidateView> Candidates,
    DatabaseAccessChangeView? LastChange);

/// <summary><c>GET /api/v1/databases/{id}</c> response.</summary>
/// <param name="Form">The current form (highest version).</param>
/// <param name="Access">Data managers and who may read records.</param>
public sealed record DatabaseDetailView(DatabaseSummaryView Summary, DatabaseFormView Form, DatabaseAccessView Access);

/// <summary>
/// <c>PUT /api/v1/databases/{id}/access</c> request: the complete list of data managers after the
/// change (an empty list removes everyone). A <see langword="null"/> list is this endpoint's own
/// <c>422</c>.
/// </summary>
public sealed record UpdateDatabaseAccessRequest(IReadOnlyList<Guid>? DataManagerAccountIds);

/// <summary>
/// <c>POST /api/v1/databases</c> request. All strings on purpose: an unknown
/// <see cref="TemplateId"/> or a missing name is this endpoint's own <c>422</c>
/// (<see cref="DatabaseCreationRules"/>), not a model-binding failure. A missing
/// <see cref="Purpose"/> means the template's description.
/// </summary>
public sealed record CreateDatabaseRequest(string? TemplateId, string? Name, string? Purpose = null);

/// <summary>
/// Databases created from templates (M4 #142), replacing the frontend mock's
/// <c>listDatabaseTemplates</c>, <c>listDatabaseSummaries</c>, <c>createDatabaseFromTemplate</c>
/// and <c>getDatabaseDetail</c> with the same rules (<c>docs/handoff/mock-to-api-mapping.md</c>
/// §2.3).
/// </summary>
/// <remarks>
/// Every endpoint needs a signed-in account. Templates and create also need
/// <see cref="AccountPermission.ManageDataSources"/> (else <c>403 database</c> with the create
/// message). The list and the detail show the caller's own databases plus those they may read the
/// records of (<see cref="DatabaseAccess.ListedFor"/>); changing the data managers needs the
/// caller to be the owner (<see cref="DatabaseAccess.ManageableBy"/>). An id that does not exist,
/// belongs to another organization (hidden by the query filter) or is not visible to the caller
/// gets the byte-identical <c>403 database</c>, never a <c>404</c> (<see cref="ApiErrors.NotFound"/>),
/// and never its name, fields or counts.
/// </remarks>
public static class DatabaseEndpoints
{
    public const string TemplatesPath = "/api/v1/database-templates";

    public const string DatabasesPath = "/api/v1/databases";

    public static IEndpointRouteBuilder MapDatabaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(TemplatesPath, ListTemplates)
            .RequireAuthorization()
            .RequirePermission(AccountPermission.ManageDataSources, ForbiddenReason.DatabaseCreate)
            .Produces<IReadOnlyList<DatabaseTemplateView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        var databases = endpoints.MapGroup(DatabasesPath).RequireAuthorization();

        databases.MapGet("", ListAsync)
            .Produces<IReadOnlyList<DatabaseSummaryView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        databases.MapPost("", CreateAsync)
            .RequirePermission(AccountPermission.ManageDataSources, ForbiddenReason.DatabaseCreate)
            .Produces<DatabaseSummaryView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        databases.MapGet("/{id:guid}", GetAsync)
            .Produces<DatabaseDetailView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        databases.MapPut("/{id:guid}/access", UpdateAccessAsync)
            .Produces<DatabaseAccessView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>The five templates (<see cref="DatabaseTemplates.All"/>), in the dialog's order.</summary>
    internal static IResult ListTemplates() =>
        Results.Ok(DatabaseTemplates.All.Select(template => new DatabaseTemplateView(
            template.Id,
            template.Name,
            template.Description,
            [.. template.Fields.Select(DatabaseFieldView.From)])).ToList());

    /// <summary>The caller's databases and those they may read the records of
    /// (<see cref="DatabaseAccess.ListedFor"/>), oldest first.</summary>
    internal static async Task<IResult> ListAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var hasReadPermission = await DatabaseRecordReaders.HasReadPermissionAsync(permissions, viewerId, cancellationToken);
        var rows = await WithOwnerAndCurrentForm(
                dbContext,
                dbContext.Databases.Where(DatabaseAccess.ListedFor(viewerId, hasReadPermission, dbContext.DatabaseDataManagers)))
            .ToListAsync(cancellationToken);

        // Ordered here rather than in SQL: EF Core cannot order by a member of a record built
        // through its constructor (as in KnowledgeBaseEndpoints), and a caller's databases are few.
        return Results.Ok(rows
            .OrderBy(row => row.Database.CreatedAt)
            .ThenBy(row => row.Database.Id)
            .Select(row => ToSummary(row, viewerId))
            .ToList());
    }

    /// <summary>
    /// A new database owned by the caller, with the template's fields as form version 1, in one
    /// save. Validation first (<c>422</c>, nothing written).
    /// </summary>
    internal static async Task<IResult> CreateAsync(
        CreateDatabaseRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var creation = DatabaseCreationRules.Validate(request.TemplateId, request.Name, request.Purpose);
        if (!creation.IsValid)
        {
            return ApiErrors.ValidationFailed(creation.Failures);
        }

        var ownerName = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.Id == callerId)
            .Select(account => account.DisplayName)
            .SingleAsync(cancellationToken);

        var now = clock.GetUtcNow();
        var value = creation.Value;
        var database = Database.Create(
            CurrentOrganizationId(dbContext), callerId, value.Name, value.Purpose, value.Template.Id, now);
        var form = DatabaseFormVersion.Create(database, 1, value.Template.Fields, callerId, now);
        dbContext.Databases.Add(database);
        dbContext.DatabaseFormVersions.Add(form);

        // Like the mock, the creator starts as the one data manager (the owner can still not read
        // anything without the permission, and can remove themselves). No change row: the
        // designation's own AssignedBy/AssignedAt record it, and "last change" stays empty.
        dbContext.DatabaseDataManagers.Add(DatabaseDataManager.Create(database, callerId, callerId, now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"{DatabasesPath}/{database.Id}",
            ToSummary(new DatabaseRow(database, ownerName, form), callerId));
    }

    /// <summary>The detail of a database the caller owns or may read the records of; the latter
    /// get it read-only (<c>viewerCanManage</c> and <c>access.viewerCanManageAccess</c> are false).</summary>
    internal static async Task<IResult> GetAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var hasReadPermission = await DatabaseRecordReaders.HasReadPermissionAsync(permissions, viewerId, cancellationToken);
        var row = await WithOwnerAndCurrentForm(
                dbContext,
                dbContext.Databases
                    .Where(DatabaseAccess.ListedFor(viewerId, hasReadPermission, dbContext.DatabaseDataManagers))
                    .Where(database => database.Id == id))
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Database);
        }

        return Results.Ok(new DatabaseDetailView(
            ToSummary(row, viewerId),
            new DatabaseFormView(
                row.Form.Id,
                row.Form.VersionNumber,
                row.Form.CreatedAt,
                [.. row.Form.Fields.Select(DatabaseFieldView.From)]),
            await BuildAccessAsync(dbContext, row.Database, row.OwnerName, viewerId, hasReadPermission, cancellationToken)));
    }

    /// <summary>
    /// Replaces the data managers with exactly <c>dataManagerAccountIds</c> (owner only). Every id
    /// must be an account of the caller's organization (<c>422</c> otherwise, nothing written; an
    /// account of another organization is indistinguishable from an unknown id). Each added or
    /// removed account is recorded with the caller and the time; a request that changes nothing
    /// writes nothing. Removing a designation never touches records.
    /// </summary>
    internal static async Task<IResult> UpdateAccessAsync(
        Guid id,
        UpdateDatabaseAccessRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        // Owner only; the same 403 for a missing id, another organization's and someone else's.
        var database = await dbContext.Databases
            .Where(DatabaseAccess.ManageableBy(callerId))
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (database is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Database);
        }

        if (request.DataManagerAccountIds is not { } requestedIds)
        {
            return DataManagersInvalid("請提供資料管理者清單。");
        }

        var requested = requestedIds.Distinct().ToList();
        var knownIds = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => requested.Contains(account.Id))
            .Select(account => account.Id)
            .ToListAsync(cancellationToken);
        if (knownIds.Count != requested.Count)
        {
            return DataManagersInvalid(UnknownAccountMessage);
        }

        var current = await dbContext.DatabaseDataManagers
            .Where(designation => designation.DatabaseId == id)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var removed = current.Where(designation => !requested.Contains(designation.AccountId)).ToList();
        var added = requested.Where(accountId => current.All(designation => designation.AccountId != accountId)).ToList();

        foreach (var designation in removed)
        {
            dbContext.DatabaseDataManagers.Remove(designation);
            dbContext.DatabaseDataManagerChanges.Add(DatabaseDataManagerChange.Create(database, designation.AccountId, false, callerId, now));
        }

        foreach (var accountId in added)
        {
            dbContext.DatabaseDataManagers.Add(DatabaseDataManager.Create(database, accountId, callerId, now));
            dbContext.DatabaseDataManagerChanges.Add(DatabaseDataManagerChange.Create(database, accountId, true, callerId, now));
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two saves designating the same account at once: one loses on the primary key.
            return ApiErrors.WithReason(
                StatusCodes.Status409Conflict,
                "database-access-conflict",
                "資料管理者剛被其他人更新，請重新整理後再試一次。");
        }

        var ownerName = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.Id == database.OwnerAccountId)
            .Select(account => account.DisplayName)
            .SingleAsync(cancellationToken);
        var hasReadPermission = await DatabaseRecordReaders.HasReadPermissionAsync(permissions, callerId, cancellationToken);
        return Results.Ok(await BuildAccessAsync(dbContext, database, ownerName, callerId, hasReadPermission, cancellationToken));
    }

    private const string UnknownAccountMessage = "有不認得的帳號，這次指定沒有儲存。";

    private const string RemovedAccountName = "已停用的帳號";

    private static IResult DataManagersInvalid(string message) =>
        ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["dataManagerAccountIds"] = [message] });

    /// <summary>
    /// The access view as <paramref name="viewerId"/> may see it: designations, who can actually
    /// read right now, the viewer's own standing, and (owner only) the candidates. Everything is
    /// read from the store now, nothing from the token.
    /// </summary>
    private static async Task<DatabaseAccessView> BuildAccessAsync(
        AppDbContext dbContext,
        Database database,
        string ownerName,
        Guid viewerId,
        bool viewerHasReadPermission,
        CancellationToken cancellationToken)
    {
        var designations = (await dbContext.DatabaseDataManagers
                .AsNoTracking()
                .Where(designation => designation.DatabaseId == database.Id)
                .ToListAsync(cancellationToken))
            .OrderBy(designation => designation.AssignedAt)
            .ThenBy(designation => designation.AccountId)
            .ToList();
        var lastChange = (await dbContext.DatabaseDataManagerChanges
                .AsNoTracking()
                .Where(change => change.DatabaseId == database.Id)
                .OrderByDescending(change => change.ChangedAt)
                .Take(1)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
        var isOwner = DatabaseAccess.CanManage(database, viewerId);

        var nameIds = designations.Select(designation => designation.AccountId)
            .Concat(designations.Select(designation => designation.AssignedByAccountId))
            .Concat(lastChange is null ? [] : [lastChange.ChangedByAccountId])
            .Distinct()
            .ToList();
        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => nameIds.Contains(account.Id) || isOwner)
            .Select(account => new { account.Id, account.DisplayName, account.Role })
            .ToListAsync(cancellationToken);
        DatabaseAccountView Named(Guid accountId) => new(
            accountId, accounts.FirstOrDefault(account => account.Id == accountId)?.DisplayName ?? RemovedAccountName);

        var readerIds = await DatabaseRecordReaders.EffectiveReaderIdsAsync(dbContext, database.Id, cancellationToken);
        var viewerIsDataManager = designations.Any(designation => designation.AccountId == viewerId);

        // Candidates (owner only) show each account's permission now, designated or not.
        var holders = isOwner
            ? (await dbContext.AccountPermissions
                .AsNoTracking()
                .Where(grant => grant.Permission == AccountPermission.ReadConsentedSubmissions)
                .Select(grant => grant.AccountId)
                .ToListAsync(cancellationToken)).ToHashSet()
            : [];
        var candidates = isOwner
            ? accounts
                .OrderBy(account => (int)account.Role)
                .ThenBy(account => account.DisplayName, StringComparer.Ordinal)
                .ThenBy(account => account.Id)
                .Select(account => new DatabaseAccessCandidateView(
                    account.Id, account.DisplayName, account.Role, holders.Contains(account.Id)))
                .ToList()
            : [];

        return new DatabaseAccessView(
            new DatabaseAccountView(database.OwnerAccountId, ownerName),
            [.. designations.Select(designation => new DatabaseDataManagerView(
                Named(designation.AccountId),
                readerIds.Contains(designation.AccountId),
                designation.AssignedAt,
                Named(designation.AssignedByAccountId)))],
            [.. designations.Where(designation => readerIds.Contains(designation.AccountId)).Select(designation => Named(designation.AccountId))],
            viewerIsDataManager,
            DatabaseRecordAccess.CanRead(viewerIsDataManager, viewerHasReadPermission),
            isOwner,
            candidates,
            lastChange is null ? null : new DatabaseAccessChangeView(lastChange.ChangedAt, Named(lastChange.ChangedByAccountId)));
    }

    /// <summary>A database with its owner's display name and current (highest) form version.</summary>
    internal sealed record DatabaseRow(Database Database, string OwnerName, DatabaseFormVersion Form);

    /// <summary>
    /// <paramref name="databases"/> (already narrowed by an access rule) joined to the owner's
    /// display name and the current form: the version whose number is the database's highest
    /// (a join and a correlated <c>MAX</c>, one query).
    /// </summary>
    internal static IQueryable<DatabaseRow> WithOwnerAndCurrentForm(AppDbContext dbContext, IQueryable<Database> databases) =>
        from database in databases.AsNoTracking()
        join owner in dbContext.Accounts.AsNoTracking() on database.OwnerAccountId equals owner.Id
        join form in dbContext.DatabaseFormVersions.AsNoTracking() on database.Id equals form.DatabaseId
        where form.VersionNumber == dbContext.DatabaseFormVersions
            .Where(other => other.DatabaseId == database.Id)
            .Max(other => other.VersionNumber)
        select new DatabaseRow(database, owner.DisplayName, form);

    private static DatabaseSummaryView ToSummary(DatabaseRow row, Guid viewerId)
    {
        var database = row.Database;
        return new DatabaseSummaryView(
            database.Id,
            database.Name,
            database.Purpose,
            database.TemplateId,
            DatabaseTemplates.Get(database.TemplateId).Name,
            row.Form.Fields.Count,
            row.Form.VersionNumber,
            new DatabaseAccountView(database.OwnerAccountId, row.OwnerName),
            database.CreatedAt,
            row.Form.CreatedAt > database.UpdatedAt ? row.Form.CreatedAt : database.UpdatedAt,
            DatabaseAccess.CanManage(database, viewerId));
    }

    private static Guid CurrentOrganizationId(AppDbContext dbContext) =>
        dbContext.OrganizationContext.OrganizationId
            ?? throw new InvalidOperationException("An authenticated request must have a current organization.");
}
