using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

// View records are named after, and shaped like, the frontend's views in
// apps/admin/src/app/core/domain/assistant.model.ts and assistant-settings.model.ts, so the
// generated types line up. Deliberate differences:
// - ids are GUIDs;
// - viewerCanManage is added (M2 plan §3, carried over), so the frontend never compares
//   owner ids itself;
// - audience / sharedWithAccountIds / databaseIds / permission are absent: the frontend mock
//   models an audience-role gate and platform sharing that M3 Slice 1 does not build yet
//   (sharing is #73; audience-role gating is not part of the M3 plan's data model, §4);
// - minScore is a backend-only field (grounded-answers ADR), not in the frontend model, and
//   is not exposed by this slice's PATCH endpoint.

/// <summary>One row of <c>GET /api/v1/assistants</c> (the caller's own assistants).</summary>
/// <param name="ViewerCanManage">Whether the caller may open, change or delete it
/// (<see cref="AssistantAccess.CanManage"/>); always <see langword="true"/> here, since the
/// list only ever contains the caller's own assistants, but included for the same reason
/// <c>KnowledgeBaseSummaryView</c> includes it.</param>
public sealed record AssistantConfigurationView(
    Guid Id,
    Guid OwnerAccountId,
    string Name,
    string Purpose,
    AssistantStatus Status,
    bool ViewerCanManage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One row of <c>GET /api/v1/assistants?usable=true</c>: enough to pick an assistant
/// to chat with, nothing about its configuration.</summary>
public sealed record AssistantSummaryView(Guid Id, string Name, string Purpose, AssistantStatus Status);

/// <summary>The rules governing how an assistant answers (M3 plan §4).</summary>
public sealed record AssistantAnswerRulesView(
    AssistantKnowledgeScope KnowledgeScope,
    string RefusalMessage,
    bool ShowCitations,
    bool KeepConversations);

/// <summary>
/// <c>GET</c>/<c>PATCH .../settings</c> response: settings, connected knowledge bases and
/// answer rules together, since the settings screen edits them as one form
/// (<c>assistant-settings.model.ts</c>'s <c>AssistantSettingsView</c>).
/// </summary>
public sealed record AssistantSettingsView(
    AssistantConfigurationView Configuration,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    AssistantTone Tone,
    string RoleInstructions,
    AssistantAnswerRulesView Rules);

/// <summary><c>PATCH /api/v1/assistants/{id}/settings</c> request: a <see langword="null"/>
/// (or absent) field, at any level, is left unchanged. <see cref="Tone"/> and
/// <see cref="AssistantAnswerRulesPatch.KnowledgeScope"/> are plain strings on purpose (as in
/// <c>UpdateKnowledgeSharingRequest</c>): an unknown value is this endpoint's own <c>422</c>,
/// not a model-binding failure.</summary>
public sealed record UpdateAssistantSettingsRequest(
    string? Name = null,
    string? Purpose = null,
    string? Tone = null,
    string? RoleInstructions = null,
    AssistantAnswerRulesPatch? Rules = null);

/// <summary>The <c>rules</c> part of <see cref="UpdateAssistantSettingsRequest"/>; every field optional.</summary>
public sealed record AssistantAnswerRulesPatch(
    string? KnowledgeScope = null,
    string? RefusalMessage = null,
    bool? ShowCitations = null,
    bool? KeepConversations = null);

/// <summary>
/// Assistant listing, settings, source connections and deletion (M3 plan, Slice 1;
/// <c>docs/handoff/mock-to-api-mapping.md</c> §2.6). Creation from a wizard draft is #72;
/// platform sharing is #73; the conversation endpoints are later Slices.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint needs a signed-in account. Listing (without <c>usable=true</c>), settings,
/// source connections and deletion also need <see cref="AccountPermission.ManageAssistants"/>
/// and the caller to be the owner (<see cref="AssistantAccess.ManageableBy"/>). An id that
/// does not exist, belongs to another organization (hidden by the query filter) or belongs to
/// someone else gets the exact same <c>403 assistant-configuration</c>, never a <c>404</c>.
/// </para>
/// <para>
/// Business rules (visibility, validation, which knowledge bases may be connected) live in
/// <c>SmartAgri.Application.Assistants</c> and are unit tested there; this class only loads,
/// calls them, writes and maps.
/// </para>
/// </remarks>
public static class AssistantEndpoints
{
    private const string DatabaseSourcesNotYetAvailableMessage = "資料庫來源將於後續版本開放，目前只能連接知識庫。";

    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var assistants = endpoints.MapGroup("/api/v1/assistants")
            .RequireAuthorization();

        // usable=true needs only a signed-in account (S); the owner listing additionally
        // needs manage-assistants, checked inside the handler because it depends on the
        // query string (RequirePermission on the whole route would wrongly gate usable=true too).
        assistants.MapGet("", ListAsync)
            .Produces<IReadOnlyList<AssistantConfigurationView>>(StatusCodes.Status200OK)
            .Produces<IReadOnlyList<AssistantSummaryView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        assistants.MapGet("/{id:guid}/settings", GetSettingsAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        assistants.MapPatch("/{id:guid}/settings", UpdateSettingsAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapDelete("/{id:guid}", DeleteAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        assistants.MapPut("/{id:guid}/sources/knowledge-base/{knowledgeBaseId:guid}", ConnectKnowledgeBaseAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapDelete("/{id:guid}/sources/knowledge-base/{knowledgeBaseId:guid}", DisconnectKnowledgeBaseAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        // Databases (M4) do not exist yet: both verbs always refuse, after the same
        // ownership check as the knowledge-base source endpoints, so they cannot be used to
        // probe whether an assistant id exists.
        assistants.MapPut("/{id:guid}/sources/database/{databaseId}", ConnectDatabaseAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapDelete("/{id:guid}/sources/database/{databaseId}", DisconnectDatabaseAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>
    /// <c>usable=true</c>: assistants <paramref name="httpContext"/>'s caller may chat with
    /// (<see cref="AssistantUseAccess.UsableBy"/>), any signed-in account. Otherwise: the
    /// caller's own assistants (<see cref="AssistantAccess.ListedFor"/>), which needs
    /// <c>manage-assistants</c> — checked here, not by <c>RequirePermission</c>, because it
    /// must not apply to the <c>usable=true</c> branch.
    /// </summary>
    internal static async Task<IResult> ListAsync(
        bool? usable,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (usable == true)
        {
            var usableAssistants = await dbContext.Assistants
                .AsNoTracking()
                .Where(AssistantUseAccess.UsableBy(viewerId))
                .OrderBy(assistant => assistant.CreatedAt)
                .ThenBy(assistant => assistant.Id)
                .ToListAsync(cancellationToken);
            return Results.Ok(usableAssistants.ConvertAll(ToSummary));
        }

        var granted = await permissions.GetAsync(viewerId, cancellationToken);
        if (!granted.Contains(AccountPermission.ManageAssistants))
        {
            return ApiErrors.Forbidden(ForbiddenReason.AssistantConfiguration);
        }

        var owned = await dbContext.Assistants
            .AsNoTracking()
            .Where(AssistantAccess.ListedFor(viewerId))
            .OrderBy(assistant => assistant.CreatedAt)
            .ThenBy(assistant => assistant.Id)
            .ToListAsync(cancellationToken);
        return Results.Ok(owned.ConvertAll(assistant => ToConfiguration(assistant, viewerId)));
    }

    internal static async Task<IResult> GetSettingsAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext.Assistants.AsNoTracking(), id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var knowledgeBaseIds = await ConnectedKnowledgeBaseIdsAsync(dbContext, assistant.Id, cancellationToken);
        return Results.Ok(ToSettings(assistant, viewerId, knowledgeBaseIds));
    }

    /// <summary>
    /// Order of checks: owner (else <c>403</c>), then
    /// <see cref="AssistantSettingsRules.ForUpdate"/> (else <c>422</c>, with nothing written).
    /// An unchanged request writes nothing (<see cref="Assistant.ApplySettings"/>).
    /// </summary>
    internal static async Task<IResult> UpdateSettingsAsync(
        Guid id,
        UpdateAssistantSettingsRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext.Assistants, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var current = new AssistantSettingsDetails(
            assistant.Name,
            assistant.Purpose,
            assistant.Tone,
            assistant.RoleInstructions,
            assistant.KnowledgeScope,
            assistant.RefusalMessage,
            assistant.ShowCitations,
            assistant.KeepConversations);

        var validated = AssistantSettingsRules.ForUpdate(
            current,
            request.Name,
            request.Purpose,
            request.Tone,
            request.RoleInstructions,
            request.Rules?.KnowledgeScope,
            request.Rules?.RefusalMessage,
            request.Rules?.ShowCitations,
            request.Rules?.KeepConversations);
        if (!validated.IsValid)
        {
            return ApiErrors.ValidationFailed(validated.Failures);
        }

        var value = validated.Value;
        var now = clock.GetUtcNow();
        var changed = assistant.ApplySettings(
            value.Name,
            value.Purpose,
            value.Tone,
            value.RoleInstructions,
            value.KnowledgeScope,
            value.RefusalMessage,
            value.ShowCitations,
            value.KeepConversations,
            now);
        if (changed.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var knowledgeBaseIds = await ConnectedKnowledgeBaseIdsAsync(dbContext, assistant.Id, cancellationToken);
        return Results.Ok(ToSettings(assistant, callerId, knowledgeBaseIds));
    }

    /// <summary>
    /// Deletes the assistant and its source connections (database cascade). Later Slices add
    /// their own tables here: from #76, every account's <c>ChatThread</c>s for this assistant
    /// must be deleted in the same save (M3 plan §7 decision G — deleting an assistant deletes
    /// everyone's conversations with it, and the confirmation text must say so).
    /// </summary>
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

        var assistant = await FindManageableAsync(dbContext.Assistants, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        dbContext.Assistants.Remove(assistant);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Connects a knowledge base the assistant's owner may connect
    /// (<see cref="AssistantKnowledgeAccess.ConnectableBy"/>); a knowledge base that does not
    /// exist (in this organization) and one that exists but is not connectable get the same
    /// <c>422</c>, so this cannot be used to probe another account's private knowledge bases.
    /// Idempotent: connecting an already-connected knowledge base changes nothing and still
    /// returns <c>200</c>.
    /// </summary>
    internal static async Task<IResult> ConnectKnowledgeBaseAsync(
        Guid id,
        Guid knowledgeBaseId,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext.Assistants, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var knowledgeBase = await dbContext.KnowledgeBases
            .Where(AssistantKnowledgeAccess.ConnectableBy(assistant.OwnerAccountId, dbContext.KnowledgeBaseShares))
            .SingleOrDefaultAsync(candidate => candidate.Id == knowledgeBaseId, cancellationToken);
        if (knowledgeBase is null)
        {
            return SourceNotConnectable();
        }

        var alreadyConnected = await dbContext.AssistantKnowledgeBases
            .AnyAsync(link => link.AssistantId == assistant.Id && link.KnowledgeBaseId == knowledgeBase.Id, cancellationToken);
        if (!alreadyConnected)
        {
            dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, clock.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var knowledgeBaseIds = await ConnectedKnowledgeBaseIdsAsync(dbContext, assistant.Id, cancellationToken);
        return Results.Ok(ToSettings(assistant, callerId, knowledgeBaseIds));
    }

    /// <summary>
    /// Disconnects a knowledge base. Disconnecting the assistant's last remaining source is
    /// refused with <c>422</c> (an assistant must always have at least one source to answer
    /// from); disconnecting one that is not connected is a no-op, so the endpoint stays
    /// idempotent.
    /// </summary>
    internal static async Task<IResult> DisconnectKnowledgeBaseAsync(
        Guid id,
        Guid knowledgeBaseId,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext.Assistants, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var links = await dbContext.AssistantKnowledgeBases
            .Where(link => link.AssistantId == assistant.Id)
            .ToListAsync(cancellationToken);
        var target = links.Find(link => link.KnowledgeBaseId == knowledgeBaseId);
        if (target is not null)
        {
            if (links.Count == 1)
            {
                return ApiErrors.WithReason(
                    StatusCodes.Status422UnprocessableEntity,
                    "last-source",
                    "助理至少要連接一個知識庫或資料庫才能回答問題，無法解除最後一個來源。",
                    field: "sources");
            }

            dbContext.AssistantKnowledgeBases.Remove(target);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var knowledgeBaseIds = await ConnectedKnowledgeBaseIdsAsync(dbContext, assistant.Id, cancellationToken);
        return Results.Ok(ToSettings(assistant, callerId, knowledgeBaseIds));
    }

    /// <summary>Databases do not exist yet (M4): always <c>422</c>, after the same ownership
    /// check as every other settings endpoint.</summary>
    internal static async Task<IResult> ConnectDatabaseAsync(
        Guid id,
        string databaseId,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken) =>
        await DatabaseSourceRefusedAsync(id, httpContext, dbContext, cancellationToken);

    internal static async Task<IResult> DisconnectDatabaseAsync(
        Guid id,
        string databaseId,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken) =>
        await DatabaseSourceRefusedAsync(id, httpContext, dbContext, cancellationToken);

    private static async Task<IResult> DatabaseSourceRefusedAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext.Assistants.AsNoTracking(), id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        return ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity,
            "database-not-available",
            DatabaseSourcesNotYetAvailableMessage,
            field: "sources");
    }

    private static IResult SourceNotConnectable() =>
        ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity,
            "source-not-connectable",
            "這個知識庫無法連接到這個助理，或已不存在。",
            field: "sources");

    /// <summary>
    /// The assistant with this id if the caller may manage it; <see langword="null"/> alike
    /// when it does not exist, belongs to another organization (the query filter hides it) or
    /// belongs to someone else.
    /// </summary>
    private static Task<Assistant?> FindManageableAsync(
        IQueryable<Assistant> assistants,
        Guid id,
        Guid callerId,
        CancellationToken cancellationToken) =>
        assistants
            .Where(AssistantAccess.ManageableBy(callerId))
            .SingleOrDefaultAsync(assistant => assistant.Id == id, cancellationToken);

    private static Task<List<Guid>> ConnectedKnowledgeBaseIdsAsync(
        AppDbContext dbContext, Guid assistantId, CancellationToken cancellationToken) =>
        dbContext.AssistantKnowledgeBases
            .AsNoTracking()
            .Where(link => link.AssistantId == assistantId)
            .OrderBy(link => link.ConnectedAt)
            .ThenBy(link => link.KnowledgeBaseId)
            .Select(link => link.KnowledgeBaseId)
            .ToListAsync(cancellationToken);

    private static AssistantConfigurationView ToConfiguration(Assistant assistant, Guid viewerId) =>
        new(
            assistant.Id,
            assistant.OwnerAccountId,
            assistant.Name,
            assistant.Purpose,
            assistant.Status,
            AssistantAccess.CanManage(assistant, viewerId),
            assistant.CreatedAt,
            assistant.UpdatedAt);

    private static AssistantSummaryView ToSummary(Assistant assistant) =>
        new(assistant.Id, assistant.Name, assistant.Purpose, assistant.Status);

    private static AssistantSettingsView ToSettings(Assistant assistant, Guid viewerId, IReadOnlyList<Guid> knowledgeBaseIds) =>
        new(
            ToConfiguration(assistant, viewerId),
            knowledgeBaseIds,
            assistant.Tone,
            assistant.RoleInstructions,
            new AssistantAnswerRulesView(
                assistant.KnowledgeScope, assistant.RefusalMessage, assistant.ShowCitations, assistant.KeepConversations));
}
