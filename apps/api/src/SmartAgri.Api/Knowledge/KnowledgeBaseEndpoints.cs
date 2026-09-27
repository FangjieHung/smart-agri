using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Knowledge;

// View records are named after, and shaped like, the frontend's views in
// apps/admin/src/app/core/domain/knowledge-base.model.ts, so the generated types line up
// field for field. Differences, all deliberate:
// - ids are GUIDs (the frontend widens KnowledgeBaseId to string when it switches over);
// - viewerCanManage is added, so the frontend never compares owner ids itself (M2 plan §3);
// - connectedAssistantNames / connectedAssistants are absent: assistants stay frontend mock
//   data until M3, and the frontend derives them from its mock assistants (M2 plan, Slice 15).

/// <summary>How many of a knowledge base's items are in each
/// <see cref="KnowledgeDocumentStatus"/>. All five keys are always present.</summary>
public sealed record KnowledgeDocumentStatusCounts(
    int Queued,
    int Processing,
    int Ready,
    [property: JsonPropertyName("partially-readable")] int PartiallyReadable,
    int Failed)
{
    public static KnowledgeDocumentStatusCounts None { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>One row of <c>GET /api/v1/knowledge-bases</c>, and the <c>summary</c> of the
/// detail. The document counts are zero until documents exist (M2 plan, Slice 5).</summary>
/// <param name="UpdatedAt">The last change to the knowledge base itself; later slices also
/// take its documents' changes into account, as the mock does.</param>
/// <param name="ViewerCanManage">Whether the caller may open, change, share and delete
/// it (<see cref="KnowledgeBaseAccess.CanManage"/>).</param>
public sealed record KnowledgeBaseSummaryView(
    Guid Id,
    string Name,
    string Purpose,
    int DocumentCount,
    int FaqCount,
    KnowledgeDocumentStatusCounts StatusCounts,
    KnowledgeSharingScope SharingScope,
    DateTimeOffset UpdatedAt,
    bool ViewerCanManage);

/// <summary>A document or FAQ entry in the detail. Always an empty list until the upload
/// slice (M2 plan, Slice 5) adds documents.</summary>
public sealed record KnowledgeDocumentView(
    Guid Id,
    KnowledgeItemKind Kind,
    string Name,
    KnowledgeDocumentStatus Status,
    string? Issue,
    DateTimeOffset UpdatedAt);

/// <summary>A knowledge base's sharing; also the response of <c>PUT .../sharing</c>.</summary>
/// <param name="SharedWithAccountIds">Empty unless <paramref name="Scope"/> is
/// <c>specific-accounts</c>; in the same order as the detail's <c>shareTargets</c>.</param>
/// <param name="AllowOriginalDownload">Only ever true for <c>public</c>.</param>
public sealed record KnowledgeSharingView(
    KnowledgeSharingScope Scope,
    IReadOnlyList<Guid> SharedWithAccountIds,
    bool AllowOriginalDownload);

/// <summary>An account the owner may share with: id and display name only, never login
/// names, roles or permissions — this is not an account directory.</summary>
public sealed record KnowledgeShareTargetView(Guid Id, string DisplayName);

/// <summary><c>GET /api/v1/knowledge-bases/{id}</c> response.</summary>
/// <param name="ShareTargets">Every other account of the caller's organization (the caller
/// is the owner), ordered like the team list.</param>
public sealed record KnowledgeBaseDetailView(
    KnowledgeBaseSummaryView Summary,
    IReadOnlyList<KnowledgeDocumentView> Documents,
    KnowledgeSharingView Sharing,
    IReadOnlyList<KnowledgeShareTargetView> ShareTargets);

/// <summary><c>POST /api/v1/knowledge-bases</c> request. <see cref="Name"/> is required and
/// <see cref="Purpose"/> optional; a missing name is this endpoint's own <c>422</c>, not a
/// model-binding failure.</summary>
public sealed record CreateKnowledgeBaseRequest(string? Name, string? Purpose = null);

/// <summary><c>PATCH /api/v1/knowledge-bases/{id}</c> request: a <see langword="null"/> (or
/// absent) field is left unchanged.</summary>
public sealed record UpdateKnowledgeBaseRequest(string? Name = null, string? Purpose = null);

/// <summary>
/// <c>PUT /api/v1/knowledge-bases/{id}/sharing</c> request, shaped like
/// <see cref="KnowledgeSharingView"/>. <see cref="Scope"/> and the account ids are plain
/// strings on purpose (as in <c>TeamEndpoints</c>): an unknown scope must be this endpoint's
/// own <c>422</c>, and an id that is not a GUID is dropped like any other unknown id, as
/// the mock does, instead of failing model binding.
/// </summary>
public sealed record UpdateKnowledgeSharingRequest(
    string? Scope,
    IReadOnlyList<string>? SharedWithAccountIds,
    bool? AllowOriginalDownload);

/// <summary>
/// Knowledge base CRUD and sharing (M2 plan, Slice 3), replacing the frontend mock's
/// <c>listKnowledgeBaseSummaries</c>, <c>getKnowledgeBaseDetail</c> and
/// <c>updateKnowledgeSharing</c> with the same rules (<c>docs/handoff/mock-to-api-mapping.md</c>
/// §2.2), plus create, rename and delete.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint needs a signed-in account. Creating also needs
/// <see cref="AccountPermission.ManageDataSources"/>; everything addressed by id needs the
/// caller to be the owner (<see cref="KnowledgeBaseAccess.ManageableBy"/>). An id that does
/// not exist, belongs to another organization (hidden by the query filter) or belongs to
/// someone else gets the exact same <c>403 knowledge-base</c>, never a <c>404</c>
/// (<see cref="ApiErrors.NotFound"/>).
/// </para>
/// <para>
/// Business rules (visibility, validation, sharing normalization) live in
/// <c>SmartAgri.Application.Knowledge</c> and are unit tested there; this class only loads,
/// calls them, writes and maps. Every change also writes a <see cref="KnowledgeActivity"/>
/// row in the same save.
/// </para>
/// </remarks>
public static class KnowledgeBaseEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeBaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var knowledgeBases = endpoints.MapGroup("/api/v1/knowledge-bases")
            .RequireAuthorization();

        knowledgeBases.MapGet("", ListAsync)
            .Produces<IReadOnlyList<KnowledgeBaseSummaryView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        knowledgeBases.MapPost("", CreateAsync)
            .RequirePermission(AccountPermission.ManageDataSources, ForbiddenReason.KnowledgeBaseCreate)
            .Produces<KnowledgeBaseSummaryView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        knowledgeBases.MapGet("/{id:guid}", GetAsync)
            .Produces<KnowledgeBaseDetailView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        knowledgeBases.MapPatch("/{id:guid}", UpdateAsync)
            .Produces<KnowledgeBaseSummaryView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        knowledgeBases.MapDelete("/{id:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        knowledgeBases.MapPut("/{id:guid}/sharing", UpdateSharingAsync)
            .Produces<KnowledgeSharingView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>The caller's knowledge bases (<see cref="KnowledgeBaseAccess.ListedFor"/>),
    /// oldest first.</summary>
    internal static async Task<IResult> ListAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var knowledgeBases = await dbContext.KnowledgeBases
            .AsNoTracking()
            .Where(KnowledgeBaseAccess.ListedFor(viewerId))
            .OrderBy(knowledgeBase => knowledgeBase.CreatedAt)
            .ThenBy(knowledgeBase => knowledgeBase.Id)
            .ToListAsync(cancellationToken);

        return Results.Ok(knowledgeBases.ConvertAll(knowledgeBase => ToSummary(knowledgeBase, viewerId)));
    }

    /// <summary>A new, private knowledge base owned by the caller.</summary>
    internal static async Task<IResult> CreateAsync(
        CreateKnowledgeBaseRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var details = KnowledgeBaseDetailsRules.ForCreate(request.Name, request.Purpose);
        if (!details.IsValid)
        {
            return ApiErrors.ValidationFailed(details.Failures);
        }

        var now = clock.GetUtcNow();
        var knowledgeBase = KnowledgeBase.Create(
            CurrentOrganizationId(dbContext), callerId, details.Value.Name, details.Value.Purpose, now);
        dbContext.KnowledgeBases.Add(knowledgeBase);
        dbContext.KnowledgeActivities.Add(KnowledgeActivity.KnowledgeBaseCreated(knowledgeBase, callerId, now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/v1/knowledge-bases/{knowledgeBase.Id}", ToSummary(knowledgeBase, callerId));
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

        var knowledgeBase = await FindManageableAsync(dbContext.KnowledgeBases.AsNoTracking(), id, viewerId, cancellationToken);
        if (knowledgeBase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var shareTargets = await ShareTargetsAsync(dbContext, knowledgeBase, cancellationToken);
        var sharedWith = (await dbContext.KnowledgeBaseShares
                .AsNoTracking()
                .Where(share => share.KnowledgeBaseId == knowledgeBase.Id)
                .Select(share => share.AccountId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return Results.Ok(new KnowledgeBaseDetailView(
            ToSummary(knowledgeBase, viewerId),
            Documents: [],
            new KnowledgeSharingView(
                knowledgeBase.SharingScope,
                [.. shareTargets.Where(target => sharedWith.Contains(target.Id)).Select(target => target.Id)],
                knowledgeBase.AllowOriginalDownload),
            shareTargets));
    }

    /// <summary>Renames and/or changes the purpose. Owner check first, then validation; an
    /// unchanged request writes nothing, not even an activity row.</summary>
    internal static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateKnowledgeBaseRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var knowledgeBase = await FindManageableAsync(dbContext.KnowledgeBases, id, callerId, cancellationToken);
        if (knowledgeBase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var details = KnowledgeBaseDetailsRules.ForUpdate(
            new KnowledgeBaseDetails(knowledgeBase.Name, knowledgeBase.Purpose), request.Name, request.Purpose);
        if (!details.IsValid)
        {
            return ApiErrors.ValidationFailed(details.Failures);
        }

        var now = clock.GetUtcNow();
        var changed = knowledgeBase.ChangeDetails(details.Value.Name, details.Value.Purpose, now);
        if (changed.Count > 0)
        {
            dbContext.KnowledgeActivities.Add(KnowledgeActivity.KnowledgeBaseUpdated(knowledgeBase, callerId, now, changed));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(ToSummary(knowledgeBase, callerId));
    }

    /// <summary>
    /// Deletes the knowledge base and everything under it, in one transaction: its shares
    /// (database cascade) and its activity rows (no foreign key, see
    /// <see cref="KnowledgeActivity"/>), then records the deletion itself. Later slices add
    /// their own tables here — by cascading foreign keys to <c>KnowledgeBases</c> where they
    /// can.
    /// </summary>
    internal static async Task<IResult> DeleteAsync(
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

        var knowledgeBase = await FindManageableAsync(dbContext.KnowledgeBases, id, callerId, cancellationToken);
        if (knowledgeBase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // A set-based DELETE rather than loading every row: it bypasses the change tracker
        // (and so the save interceptor), but it is still a query, so the "Organization"
        // filter scopes it to the caller's organization like any read.
        await dbContext.KnowledgeActivities
            .Where(activity => activity.KnowledgeBaseId == knowledgeBase.Id)
            .ExecuteDeleteAsync(cancellationToken);

        var now = clock.GetUtcNow();
        dbContext.KnowledgeBases.Remove(knowledgeBase);
        dbContext.KnowledgeActivities.Add(KnowledgeActivity.KnowledgeBaseDeleted(knowledgeBase, callerId, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.NoContent();
    }

    /// <summary>
    /// Order of checks mirrors the mock's <c>updateKnowledgeSharing</c>: owner (else
    /// <c>403</c>), then <see cref="KnowledgeSharingPolicy.Normalize"/> (else <c>422</c>, with
    /// nothing written), then the share rows are replaced to match. An unchanged request
    /// writes nothing.
    /// </summary>
    internal static async Task<IResult> UpdateSharingAsync(
        Guid id,
        UpdateKnowledgeSharingRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var knowledgeBase = await FindManageableAsync(dbContext.KnowledgeBases, id, callerId, cancellationToken);
        if (knowledgeBase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var shareTargets = await ShareTargetsAsync(dbContext, knowledgeBase, cancellationToken);
        var normalized = KnowledgeSharingPolicy.Normalize(
            request.Scope,
            request.SharedWithAccountIds,
            request.AllowOriginalDownload ?? false,
            [.. shareTargets.Select(target => target.Id)]);
        if (!normalized.IsValid)
        {
            return ApiErrors.ValidationFailed(normalized.Failures);
        }

        var next = normalized.Value;

        // Tracked, so removed rows become real DELETEs carrying their own OrganizationId
        // (the write guard's concurrency token), as in TeamEndpoints.
        var existingShares = await dbContext.KnowledgeBaseShares
            .Where(share => share.KnowledgeBaseId == knowledgeBase.Id)
            .ToListAsync(cancellationToken);
        var current = new KnowledgeSharingSettings(
            knowledgeBase.SharingScope,
            [.. existingShares.Select(share => share.AccountId)],
            knowledgeBase.AllowOriginalDownload);

        if (!next.IsEquivalentTo(current))
        {
            var now = clock.GetUtcNow();
            knowledgeBase.ChangeSharing(next.Scope, next.AllowOriginalDownload, now);

            var keep = next.SharedWithAccountIds.ToHashSet();
            dbContext.KnowledgeBaseShares.RemoveRange(existingShares.Where(share => !keep.Contains(share.AccountId)));
            var existing = current.SharedWithAccountIds.ToHashSet();
            dbContext.KnowledgeBaseShares.AddRange(next.SharedWithAccountIds
                .Where(accountId => !existing.Contains(accountId))
                .Select(accountId => new KnowledgeBaseShare(knowledgeBase, accountId)));

            dbContext.KnowledgeActivities.Add(
                KnowledgeActivity.SharingChanged(knowledgeBase, callerId, now, next.SharedWithAccountIds));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(new KnowledgeSharingView(next.Scope, next.SharedWithAccountIds, next.AllowOriginalDownload));
    }

    /// <summary>
    /// The knowledge base with this id if the caller may manage it; <see langword="null"/>
    /// alike when it does not exist, belongs to another organization (the query filter hides
    /// it) or belongs to someone else.
    /// </summary>
    private static Task<KnowledgeBase?> FindManageableAsync(
        IQueryable<KnowledgeBase> knowledgeBases,
        Guid id,
        Guid callerId,
        CancellationToken cancellationToken) =>
        knowledgeBases
            .Where(KnowledgeBaseAccess.ManageableBy(callerId))
            .SingleOrDefaultAsync(knowledgeBase => knowledgeBase.Id == id, cancellationToken);

    /// <summary><see cref="KnowledgeSharingPolicy.ShareTargetIds"/> with display names, in the
    /// team list's order (account id).</summary>
    private static async Task<IReadOnlyList<KnowledgeShareTargetView>> ShareTargetsAsync(
        AppDbContext dbContext,
        KnowledgeBase knowledgeBase,
        CancellationToken cancellationToken)
    {
        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Id)
            .Select(account => new KnowledgeShareTargetView(account.Id, account.DisplayName))
            .ToListAsync(cancellationToken);

        var targetIds = KnowledgeSharingPolicy
            .ShareTargetIds(knowledgeBase.OwnerAccountId, accounts.Select(account => account.Id))
            .ToHashSet();
        return accounts.FindAll(account => targetIds.Contains(account.Id));
    }

    private static KnowledgeBaseSummaryView ToSummary(KnowledgeBase knowledgeBase, Guid viewerId) =>
        new(
            knowledgeBase.Id,
            knowledgeBase.Name,
            knowledgeBase.Purpose,
            DocumentCount: 0,
            FaqCount: 0,
            KnowledgeDocumentStatusCounts.None,
            knowledgeBase.SharingScope,
            knowledgeBase.UpdatedAt,
            KnowledgeBaseAccess.CanManage(knowledgeBase, viewerId));

    private static Guid CurrentOrganizationId(AppDbContext dbContext) =>
        dbContext.OrganizationContext.OrganizationId
            ?? throw new InvalidOperationException("An authenticated request must have a current organization.");
}
