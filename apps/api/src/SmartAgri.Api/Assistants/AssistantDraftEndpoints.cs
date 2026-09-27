using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Knowledge;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

/// <summary>One row of <c>GET /api/v1/assistant-drafts</c>, and the response of every other
/// draft endpoint. Shaped after the frontend's <c>NamedAssistantDraftView</c>
/// (<c>assistant-draft.model.ts</c>): <see cref="Payload"/> takes the place of its
/// <c>draft</c> field (the backend never parses it except at creation time, #72's own
/// scope), and <see cref="SchemaVersion"/> / <see cref="Revision"/> are backend-only
/// additions the frontend needs to round-trip a save (M3 plan §3).</summary>
public sealed record AssistantDraftView(
    Guid Id,
    JsonElement Payload,
    int SchemaVersion,
    int Revision,
    DateTimeOffset SavedAt);

/// <summary><c>POST /api/v1/assistant-drafts</c> request. <see cref="SchemaVersion"/>
/// defaults to 1 for a first draft.</summary>
public sealed record CreateAssistantDraftRequest(JsonElement Payload, int SchemaVersion = 1);

/// <summary><c>PUT /api/v1/assistant-drafts/{id}</c> request: <see cref="Revision"/> must
/// equal the draft's current revision (from the last read), or the save is refused with
/// <c>409</c> (M3 plan Slice 2 acceptance: two tabs saving the same draft).</summary>
public sealed record SaveAssistantDraftRequest(JsonElement Payload, int Revision, int SchemaVersion = 1);

/// <summary><c>POST /api/v1/assistants</c> request: build an assistant from a saved draft.</summary>
public sealed record CreateAssistantFromDraftRequest(Guid DraftId);

/// <summary>
/// A source the caller may connect to an assistant right now
/// (<c>docs/handoff/mock-to-api-mapping.md</c> §2.1's <c>listConnectableSources</c>). M3 only
/// returns knowledge bases — <see cref="Type"/> is always <c>"knowledge-base"</c>; databases
/// are M4. Shaped after the frontend's <c>ConnectableSourceView</c>
/// (<c>assistant-draft.model.ts</c>).
/// </summary>
public sealed record ConnectableSourceView(
    Guid Id,
    string Type,
    string Name,
    string Summary,
    string Permission,
    string Status,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Assistant wizard drafts and "由草稿建立助理" (M3 plan §3, Slice 2; ticket #72;
/// <c>docs/handoff/mock-to-api-mapping.md</c> §2.1). A draft belongs to exactly one account
/// (<see cref="AssistantDraftAccess"/>) — never shared, never listed for anyone else. An id
/// that does not exist, belongs to another organization, or belongs to another account of
/// the same organization gets the same <c>403 assistant-draft</c>, never a <c>404</c>.
/// </summary>
public static class AssistantDraftEndpoints
{
    public static IEndpointRouteBuilder MapAssistantDraftEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var drafts = endpoints.MapGroup("/api/v1/assistant-drafts")
            .RequireAuthorization()
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration);

        drafts.MapGet("", ListAsync)
            .Produces<IReadOnlyList<AssistantDraftView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        drafts.MapPost("", CreateAsync)
            .Produces<AssistantDraftView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        drafts.MapGet("/{id:guid}", GetAsync)
            .Produces<AssistantDraftView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        drafts.MapPut("/{id:guid}", SaveAsync)
            .Produces<AssistantDraftView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        drafts.MapDelete("/{id:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        endpoints.MapGet("/api/v1/connectable-sources", ListConnectableSourcesAsync)
            .RequireAuthorization()
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<IReadOnlyList<ConnectableSourceView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>The caller's own drafts, most recently saved first.</summary>
    internal static async Task<IResult> ListAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var drafts = await dbContext.AssistantDrafts
            .AsNoTracking()
            .Where(AssistantDraftAccess.OwnedBy(viewerId))
            .OrderByDescending(draft => draft.SavedAt)
            .ThenBy(draft => draft.Id)
            .ToListAsync(cancellationToken);

        return Results.Ok(drafts.ConvertAll(ToView));
    }

    /// <summary>A new draft owned by the caller.</summary>
    internal static async Task<IResult> CreateAsync(
        CreateAssistantDraftRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var payload = AssistantDraftPayloadRules.ForSave(request.Payload);
        if (!payload.IsValid)
        {
            return ApiErrors.ValidationFailed(payload.Failures);
        }

        var now = clock.GetUtcNow();
        var draft = new AssistantDraft(
            CurrentOrganizationId(dbContext), callerId, payload.Value, request.SchemaVersion, now);
        dbContext.AssistantDrafts.Add(draft);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/v1/assistant-drafts/{draft.Id}", ToView(draft));
    }

    internal static async Task<IResult> GetAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var draft = await FindOwnedAsync(dbContext.AssistantDrafts.AsNoTracking(), id, callerId, cancellationToken);
        if (draft is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantDraft);
        }

        return Results.Ok(ToView(draft));
    }

    /// <summary>
    /// Validates <see cref="AssistantDraftPayloadRules"/> first (else <c>422</c>), then
    /// applies <see cref="AssistantDraft.TrySave"/>: a revision mismatch — another tab
    /// already saved — is <c>409</c>, nothing written.
    /// </summary>
    internal static async Task<IResult> SaveAsync(
        Guid id,
        SaveAssistantDraftRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var draft = await FindOwnedAsync(dbContext.AssistantDrafts, id, callerId, cancellationToken);
        if (draft is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantDraft);
        }

        var payload = AssistantDraftPayloadRules.ForSave(request.Payload);
        if (!payload.IsValid)
        {
            return ApiErrors.ValidationFailed(payload.Failures);
        }

        var now = clock.GetUtcNow();
        if (!draft.TrySave(payload.Value, request.SchemaVersion, request.Revision, now))
        {
            return ApiErrors.WithReason(
                StatusCodes.Status409Conflict,
                "draft-revision-conflict",
                "這份草稿已在其他分頁被更新過，請重新載入後再修改。");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToView(draft));
    }

    internal static async Task<IResult> DeleteAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var draft = await FindOwnedAsync(dbContext.AssistantDrafts, id, callerId, cancellationToken);
        if (draft is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantDraft);
        }

        dbContext.AssistantDrafts.Remove(draft);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// The knowledge bases the caller may connect right now
    /// (<see cref="AssistantKnowledgeAccess.ConnectableBy"/>, with the caller as the future
    /// assistant owner): their own, any public one, or one specifically shared with them.
    /// </summary>
    internal static async Task<IResult> ListConnectableSourcesAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var knowledgeBases = await dbContext.KnowledgeBases
            .AsNoTracking()
            .Where(AssistantKnowledgeAccess.ConnectableBy(callerId, dbContext.KnowledgeBaseShares))
            .OrderBy(knowledgeBase => knowledgeBase.CreatedAt)
            .ThenBy(knowledgeBase => knowledgeBase.Id)
            .ToListAsync(cancellationToken);

        var ids = knowledgeBases.ConvertAll(knowledgeBase => knowledgeBase.Id);
        var items = (await KnowledgeBaseEndpoints.ItemStatesAsync(
                dbContext,
                dbContext.KnowledgeDocuments.Where(document => ids.Contains(document.KnowledgeBaseId)),
                clock.GetUtcNow(),
                cancellationToken))
            .ToLookup(item => item.KnowledgeBaseId);

        var views = knowledgeBases.ConvertAll(knowledgeBase =>
            ToConnectableSource(knowledgeBase, callerId, KnowledgeBaseTally.Of(items[knowledgeBase.Id])));
        return Results.Ok(views);
    }

    private static Task<AssistantDraft?> FindOwnedAsync(
        IQueryable<AssistantDraft> drafts, Guid id, Guid callerId, CancellationToken cancellationToken) =>
        drafts
            .Where(AssistantDraftAccess.OwnedBy(callerId))
            .SingleOrDefaultAsync(draft => draft.Id == id, cancellationToken);

    private static AssistantDraftView ToView(AssistantDraft draft) =>
        new(
            draft.Id,
            JsonDocument.Parse(draft.Payload).RootElement.Clone(),
            draft.SchemaVersion,
            draft.Revision,
            draft.SavedAt);

    private static ConnectableSourceView ToConnectableSource(
        KnowledgeBase knowledgeBase, Guid viewerId, KnowledgeBaseTally tally) =>
        new(
            knowledgeBase.Id,
            "knowledge-base",
            knowledgeBase.Name,
            $"{tally.DocumentCount} 份文件、{tally.FaqCount} 則 FAQ",
            knowledgeBase.OwnerAccountId == viewerId ? "owner" : "read-only",
            ConnectableStatus(tally),
            tally.LastItemUpdate > knowledgeBase.UpdatedAt ? tally.LastItemUpdate.Value : knowledgeBase.UpdatedAt);

    private static string ConnectableStatus(KnowledgeBaseTally tally)
    {
        var counts = tally.StatusCounts;
        if (counts[KnowledgeDocumentStatus.Queued] + counts[KnowledgeDocumentStatus.Processing] > 0)
        {
            return "processing";
        }

        if (counts[KnowledgeDocumentStatus.PartiallyReadable] + counts[KnowledgeDocumentStatus.Failed] > 0)
        {
            return "needs-attention";
        }

        return "ready";
    }

    private static Guid CurrentOrganizationId(AppDbContext dbContext) =>
        dbContext.OrganizationContext.OrganizationId
            ?? throw new InvalidOperationException("An authenticated request must have a current organization.");
}
