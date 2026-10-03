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
// - connectedAssistantNames / connectedAssistants / access are absent: assistant connections are
//   #148 and data managers #144;
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

/// <summary><c>GET /api/v1/databases/{id}</c> response.</summary>
/// <param name="Form">The current form (highest version).</param>
public sealed record DatabaseDetailView(DatabaseSummaryView Summary, DatabaseFormView Form);

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
/// message). The detail needs the caller to be the owner (<see cref="DatabaseAccess.ManageableBy"/>):
/// an id that does not exist, belongs to another organization (hidden by the query filter) or
/// belongs to someone else gets the byte-identical <c>403 database</c>, never a <c>404</c>
/// (<see cref="ApiErrors.NotFound"/>), and never its name, fields or counts.
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

        return endpoints;
    }

    /// <summary>The five templates (<see cref="DatabaseTemplates.All"/>), in the dialog's order.</summary>
    internal static IResult ListTemplates() =>
        Results.Ok(DatabaseTemplates.All.Select(template => new DatabaseTemplateView(
            template.Id,
            template.Name,
            template.Description,
            [.. template.Fields.Select(DatabaseFieldView.From)])).ToList());

    /// <summary>The caller's databases (<see cref="DatabaseAccess.ListedFor"/>), oldest first.</summary>
    internal static async Task<IResult> ListAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var rows = await WithOwnerAndCurrentForm(dbContext, dbContext.Databases.Where(DatabaseAccess.ListedFor(viewerId)))
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
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"{DatabasesPath}/{database.Id}",
            ToSummary(new DatabaseRow(database, ownerName, form), callerId));
    }

    internal static async Task<IResult> GetAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var row = await WithOwnerAndCurrentForm(
                dbContext,
                dbContext.Databases.Where(DatabaseAccess.ManageableBy(viewerId)).Where(database => database.Id == id))
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
                [.. row.Form.Fields.Select(DatabaseFieldView.From)])));
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
