using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Ai;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Knowledge;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

// View records are named after, and shaped like, the frontend's views in
// apps/admin/src/app/core/domain/assistant.model.ts, assistant-settings.model.ts and
// publishing.model.ts, so the generated types line up. Deliberate differences:
// - ids are GUIDs;
// - viewerCanManage / viewerIsOwner are added (M2 plan §3, carried over), so the frontend
//   never compares owner ids itself;
// - audience / sharedWithAccountIds (on AssistantConfigurationView) / databaseIds are absent:
//   the frontend mock models an audience-role gate that is not part of the M3 plan's data
//   model (§4) — "who may use it" here is ownership + AssistantShare + use-shared-assistants
//   only (AssistantUseAccess.UsableBy), not audience/role;
// - minScore is a backend-only field (grounded-answers ADR), not in the frontend model, and
//   is not exposed by this slice's PATCH endpoint;
// - AssistantPublishingView.website / .line are NotAvailablePublishingChannelView, not the
//   frontend's full WebsiteEmbedView / LineSetupView: those channels are not implemented until
//   a later milestone (M3 plan §5 Slice 3: "網站嵌入與 LINE 在 API 模式顯示「對外發布將於後續版本
//   開放」"), so there is no embed code, webhook URL or field-level state to report yet.

/// <summary>One row of <c>GET /api/v1/assistants</c> (the caller's own assistants).</summary>
/// <param name="ViewerCanManage">Whether the caller may open, change or delete it
/// (<see cref="AssistantAccess.CanManage"/>); always <see langword="true"/> here, since the
/// list only ever contains the caller's own assistants, but included for the same reason
/// <c>KnowledgeBaseSummaryView</c> includes it.</param>
/// <param name="AcceptanceStatus">Derived from its test set and runs (M3.5 plan §3, issue #125;
/// <see cref="AssistantAcceptanceRules"/>); shown only, it does not restrict use.</param>
public sealed record AssistantConfigurationView(
    Guid Id,
    Guid OwnerAccountId,
    string Name,
    string Purpose,
    AssistantStatus Status,
    bool ViewerCanManage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    AssistantAcceptanceStatus AcceptanceStatus);

/// <summary>One row of <c>GET /api/v1/assistants?usable=true</c>: enough to pick an assistant
/// to chat with, nothing about its configuration.</summary>
/// <param name="ViewerIsOwner">Whether the caller owns it, as opposed to it being shared with
/// them (<see cref="AssistantUseAccess.UsableBy"/>) — the frontend mock's richer
/// <c>AssistantSummaryView.permission</c> (<c>'use' | 'configure' | 'publish'</c>) does not
/// apply here (M3's data model has no per-assistant publish/configure grant separate from
/// ownership, §4), so this is a plain owner/shared flag instead.</param>
public sealed record AssistantSummaryView(Guid Id, string Name, string Purpose, AssistantStatus Status, bool ViewerIsOwner);

/// <summary>One publishing channel's status, shaped like the frontend's
/// <c>PublishingChannelView</c>. <see cref="Id"/> follows the frontend's
/// <c>channel-{type}:{assistantId}</c> convention.</summary>
public sealed record PublishingChannelView(
    string Id,
    Guid AssistantId,
    Guid OwnerAccountId,
    string Name,
    string Type,
    string Status,
    string StatusDetail,
    DateTimeOffset UpdatedAt);

/// <summary>One candidate the platform channel can be shared with (an org account other than
/// the assistant's owner).</summary>
public sealed record PlatformShareTargetView(Guid Id, string DisplayName);

/// <summary>
/// The platform channel's real data (<c>getAssistantPublishing</c> /
/// <c>updatePlatformSharing</c>). <see cref="UsagePath"/> is the in-platform chat route
/// (M3 plan §6: home's "開始對話" goes to <c>/app/chat/:assistantId</c>).
/// </summary>
public sealed record PlatformSharingView(
    PublishingChannelView Channel,
    string UsagePath,
    IReadOnlyList<Guid> AllowedAccountIds,
    IReadOnlyList<PlatformShareTargetView> Candidates);

/// <summary>A channel M3 does not implement yet (website embed, LINE): fixed
/// <c>"not-available"</c> status and an explanatory message, in place of the frontend's full
/// per-channel view (see the class-level comment on why the shape differs).</summary>
public sealed record NotAvailablePublishingChannelView(string Status, string Message);

/// <summary><c>GET /api/v1/assistants/{id}/publishing</c> response.</summary>
public sealed record AssistantPublishingView(
    Guid AssistantId,
    string AssistantName,
    PlatformSharingView Platform,
    NotAvailablePublishingChannelView Website,
    NotAvailablePublishingChannelView Line);

/// <summary><c>PUT /api/v1/assistants/{id}/publishing/platform</c> request: the full set of
/// accounts to share with (not a delta). Plain strings, like
/// <c>UpdateKnowledgeSharingRequest.SharedWithAccountIds</c>, so an unknown or malformed id is
/// this endpoint's own silent filtering (<see cref="AssistantPublishingPolicy.Normalize"/>),
/// not a model-binding failure.</summary>
public sealed record UpdatePlatformSharingRequest(IReadOnlyList<string?>? AccountIds);

/// <summary><c>PUT /api/v1/assistants/{id}/publishing/platform/paused</c> request.</summary>
public sealed record SetPlatformPausedRequest(bool Paused);

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
/// Assistant listing, settings, source connections, deletion and platform sharing (M3 plan,
/// Slices 1 and 3; <c>docs/handoff/mock-to-api-mapping.md</c> §2.5/§2.6). Creation from a
/// wizard draft is #72; the conversation endpoints are #76.
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

        // "由草稿建立助理" (M3 plan §3, Slice 2; ticket #72). A draft that does not exist or
        // belongs to another account gets 403 assistant-draft, not assistant-configuration —
        // it is a different resource with its own access rule (AssistantDraftAccess).
        assistants.MapPost("", CreateFromDraftAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantConfigurationView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

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

        // Publishing (M3 plan, Slice 3): S+OWN+MP. Website and LINE are not implemented yet
        // (M3 plan §5 Slice 3), so only the platform channel has real read/write endpoints.
        assistants.MapGet("/{id:guid}/publishing", GetPublishingAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<AssistantPublishingView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        assistants.MapPut("/{id:guid}/publishing/platform", UpdatePlatformSharingAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<PlatformSharingView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        // A built assistant's own trial answer (M3.5 plan Slice 1, issue #123): same response
        // shape as the wizard draft's (AssistantDraftEndpoints.TrialAnswerAsync), but answered
        // with the assistant's own rules and currently connected knowledge bases, and attributed
        // to it (ModelInvocation.AssistantId) instead of null.
        assistants.MapPost("/{id:guid}/trial-answers", TrialAnswerAsync)
            .Produces<TrialAnswerResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        assistants.MapPut("/{id:guid}/publishing/platform/paused", SetPlatformPausedAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<PublishingChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

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
            var viewerPermissions = await permissions.GetAsync(viewerId, cancellationToken);
            var hasUseSharedAssistants = viewerPermissions.Contains(AccountPermission.UseSharedAssistants);
            var usableAssistants = await dbContext.Assistants
                .AsNoTracking()
                .Where(AssistantUseAccess.UsableBy(viewerId, hasUseSharedAssistants, dbContext.AssistantShares))
                .OrderBy(assistant => assistant.CreatedAt)
                .ThenBy(assistant => assistant.Id)
                .ToListAsync(cancellationToken);
            return Results.Ok(usableAssistants.ConvertAll(assistant => ToSummary(assistant, viewerId)));
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
        var acceptance = await AcceptanceStatusesAsync(dbContext, owned.ConvertAll(assistant => assistant.Id), cancellationToken);
        return Results.Ok(owned.ConvertAll(assistant => ToConfiguration(assistant, viewerId, acceptance[assistant.Id])));
    }

    /// <summary>
    /// Builds an <see cref="Assistant"/> from the caller's own draft
    /// (<see cref="AssistantDraftAccess.OwnedBy"/>; someone else's draft id gets
    /// <see cref="ForbiddenReason.AssistantDraft"/>, byte-identical to a missing one).
    /// Validates every field (<see cref="AssistantDraftCreationRules"/>) against the
    /// knowledge bases the caller may connect right now
    /// (<see cref="AssistantKnowledgeAccess.ConnectableBy"/>) before writing anything: on
    /// success the assistant, its knowledge-base connections and the draft's deletion are
    /// one <see cref="AppDbContext.SaveChangesAsync"/>; on <c>422</c> nothing is written and
    /// the draft still exists (M3 plan Slice 2 acceptance).
    /// </summary>
    internal static async Task<IResult> CreateFromDraftAsync(
        CreateAssistantFromDraftRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var draft = await dbContext.AssistantDrafts
            .Where(AssistantDraftAccess.OwnedBy(callerId))
            .SingleOrDefaultAsync(candidate => candidate.Id == request.DraftId, cancellationToken);
        if (draft is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantDraft);
        }

        var connectableKnowledgeBaseIds = await dbContext.KnowledgeBases
            .AsNoTracking()
            .Where(AssistantKnowledgeAccess.ConnectableBy(callerId, dbContext.KnowledgeBaseShares))
            .Select(knowledgeBase => knowledgeBase.Id)
            .ToListAsync(cancellationToken);

        var validated = AssistantDraftCreationRules.Validate(draft.Payload, connectableKnowledgeBaseIds.ToHashSet());
        if (!validated.IsValid)
        {
            return ApiErrors.ValidationFailed(validated.Failures);
        }

        var value = validated.Value;
        var now = clock.GetUtcNow();
        var assistant = Assistant.Create(
            draft.OrganizationId,
            callerId,
            value.Name,
            value.Purpose,
            value.TemplateId,
            value.Tone,
            value.RoleInstructions,
            value.KnowledgeScope,
            value.RefusalMessage,
            value.ShowCitations,
            value.KeepConversations,
            now);
        dbContext.Assistants.Add(assistant);

        var knowledgeBasesById = await dbContext.KnowledgeBases
            .Where(knowledgeBase => value.KnowledgeBaseIds.Contains(knowledgeBase.Id))
            .ToDictionaryAsync(knowledgeBase => knowledgeBase.Id, cancellationToken);
        foreach (var knowledgeBaseId in value.KnowledgeBaseIds)
        {
            dbContext.AssistantKnowledgeBases.Add(
                new AssistantKnowledgeBase(assistant, knowledgeBasesById[knowledgeBaseId], now));
        }

        dbContext.AssistantDrafts.Remove(draft);
        await dbContext.SaveChangesAsync(cancellationToken);

        // A new assistant has no test case yet.
        return Results.Created(
            $"/api/v1/assistants/{assistant.Id}/settings",
            ToConfiguration(assistant, callerId, AssistantAcceptanceStatus.NotAccepted));
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

        return Results.Ok(await SettingsAsync(dbContext, assistant, viewerId, cancellationToken));
    }

    /// <summary>
    /// Order of checks: owner (else <c>403</c>), then
    /// <see cref="AssistantSettingsRules.ForUpdate"/> (else <c>422</c>, with nothing written).
    /// An unchanged request writes nothing (<see cref="Assistant.ApplySettings"/>). A change to a
    /// field the answers are built from (<see cref="AnswerAffectingSettings"/>) asks, in the same
    /// transaction, for an <c>assistant-changed</c> rerun if the assistant has test cases (issue #125).
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
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (changed.Any(AnswerAffectingSettings.Contains))
            {
                await AssistantTestRunQueue.RequestIfTestedAsync(
                    dbContext, assistant.OrganizationId, assistant.Id, AssistantTestRunTrigger.AssistantChanged, now, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await SettingsAsync(dbContext, assistant, callerId, cancellationToken));
    }

    /// <summary>
    /// Deletes the assistant, its source connections, its shares, and — since #76 —
    /// every account's <see cref="Domain.Chat.ChatThread"/>s with it, all by database cascade
    /// (<c>ChatThreadConfiguration</c>): one <c>DELETE</c> here removes everyone's
    /// conversations with this assistant in the same transaction (M3 plan §7 decision G — the
    /// confirmation text must say so).
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
    /// returns <c>200</c>. A new connection asks, in the same transaction, for an
    /// <c>assistant-changed</c> rerun if the assistant has test cases (issue #125).
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
            var now = clock.GetUtcNow();
            dbContext.AssistantKnowledgeBases.Add(new AssistantKnowledgeBase(assistant, knowledgeBase, now));
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await AssistantTestRunQueue.RequestIfTestedAsync(
                dbContext, assistant.OrganizationId, assistant.Id, AssistantTestRunTrigger.AssistantChanged, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await SettingsAsync(dbContext, assistant, callerId, cancellationToken));
    }

    /// <summary>
    /// Disconnects a knowledge base. Disconnecting the assistant's last remaining source is
    /// refused with <c>422</c> (an assistant must always have at least one source to answer
    /// from); disconnecting one that is not connected is a no-op, so the endpoint stays
    /// idempotent. A removed connection asks, in the same transaction, for an
    /// <c>assistant-changed</c> rerun if the assistant has test cases (issue #125).
    /// </summary>
    internal static async Task<IResult> DisconnectKnowledgeBaseAsync(
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
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await AssistantTestRunQueue.RequestIfTestedAsync(
                dbContext, assistant.OrganizationId, assistant.Id, AssistantTestRunTrigger.AssistantChanged, clock.GetUtcNow(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await SettingsAsync(dbContext, assistant, callerId, cancellationToken));
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

    /// <summary>Platform sharing's real data, plus fixed <c>not-available</c> placeholders for
    /// website and LINE (M3 plan §5 Slice 3).</summary>
    internal static async Task<IResult> GetPublishingAsync(
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
            return ApiErrors.NotFound(ForbiddenReason.Publishing);
        }

        var allowedAccountIds = await SharedAccountIdsAsync(dbContext, assistant.Id, cancellationToken);
        var platform = await ToPlatformSharingAsync(dbContext, assistant, allowedAccountIds, cancellationToken);
        return Results.Ok(new AssistantPublishingView(assistant.Id, assistant.Name, platform, NotYetAvailable, NotYetAvailable));
    }

    /// <summary>
    /// Replaces the platform channel's share list wholesale. Order of checks mirrors
    /// <c>KnowledgeBaseEndpoints.UpdateSharingAsync</c>: owner (else <c>403</c>), then
    /// <see cref="AssistantPublishingPolicy.Normalize"/> silently drops anything not a valid
    /// share target — there is no failure case here (see the policy's remarks). An unchanged
    /// request writes nothing.
    /// </summary>
    internal static async Task<IResult> UpdatePlatformSharingAsync(
        Guid id,
        UpdatePlatformSharingRequest request,
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
            return ApiErrors.NotFound(ForbiddenReason.Publishing);
        }

        var shareTargetIds = await ShareTargetIdsAsync(dbContext, assistant, cancellationToken);
        var next = AssistantPublishingPolicy.Normalize(request.AccountIds, shareTargetIds);

        // Tracked, so removed rows become real DELETEs carrying their own OrganizationId (the
        // write guard's concurrency token), as in KnowledgeBaseEndpoints.UpdateSharingAsync.
        var existingShares = await dbContext.AssistantShares
            .Where(share => share.AssistantId == assistant.Id)
            .ToListAsync(cancellationToken);
        var existingIds = existingShares.Select(share => share.AccountId).ToHashSet();
        var keep = next.ToHashSet();

        if (!keep.SetEquals(existingIds))
        {
            dbContext.AssistantShares.RemoveRange(existingShares.Where(share => !keep.Contains(share.AccountId)));
            dbContext.AssistantShares.AddRange(next
                .Where(accountId => !existingIds.Contains(accountId))
                .Select(accountId => new AssistantShare(assistant, accountId)));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await ToPlatformSharingAsync(dbContext, assistant, next, cancellationToken));
    }

    /// <summary>
    /// Pauses or resumes the platform channel by pausing/resuming the assistant itself
    /// (<see cref="Assistant.SetStatus"/>): M3 has one usable channel, so "pause this channel"
    /// and "pause the assistant" (acceptance: "助理暫停後，非擁有者無法使用，擁有者可以") are the same
    /// thing. Only this one channel's view is affected in the response, per the mapping's
    /// <c>setPublishingChannelPaused</c> contract ("只影響這一個管道").
    /// </summary>
    internal static async Task<IResult> SetPlatformPausedAsync(
        Guid id,
        SetPlatformPausedRequest request,
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
            return ApiErrors.NotFound(ForbiddenReason.Publishing);
        }

        var targetStatus = request.Paused ? AssistantStatus.Paused : AssistantStatus.Ready;
        if (assistant.SetStatus(targetStatus, clock.GetUtcNow()))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var allowedAccountIds = await SharedAccountIdsAsync(dbContext, assistant.Id, cancellationToken);
        return Results.Ok(ToChannelView(assistant, allowedAccountIds));
    }

    /// <summary>
    /// Answers <paramref name="request"/>'s question through the real answer pipeline
    /// (<see cref="GroundedAnswerService"/>), using this assistant's own rules and currently
    /// connected knowledge bases (M3.5 plan Slice 1, issue #123). Exactly the same validation,
    /// response shape and failure handling as the wizard draft's trial answer
    /// (<see cref="AssistantDraftEndpoints.TrialAnswerAsync"/>), except the call is attributed to
    /// this assistant (<see cref="GroundedAnswerRequest.AssistantId"/>) rather than
    /// <see langword="null"/>. Below the relevance threshold with
    /// <see cref="AssistantKnowledgeScope.CompanyDataOnly"/>, the model is never called (grounded-
    /// answers ADR) — the issue's acceptance criterion "試問低於門檻時不呼叫模型".
    /// </summary>
    internal static async Task<IResult> TrialAnswerAsync(
        Guid id,
        TrialAnswerRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        GroundedAnswerService answerService,
        ILoggerFactory loggerFactory,
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

        var question = AssistantDraftTrialAnswerRules.ValidateQuestion(request.Question);
        if (!question.IsValid)
        {
            return ApiErrors.ValidationFailed(question.Failures);
        }

        var knowledgeBaseIds = await ConnectedKnowledgeBaseIdsAsync(dbContext, assistant.Id, cancellationToken);
        var profile = GroundedAnswerProfile.For(assistant, knowledgeBaseIds);
        var answerRequest = new GroundedAnswerRequest(
            profile, question.Value, [], callerId, assistant.Id, ModelInvocationPurpose.TrialAnswer);

        GroundedAnswerResult result;
        try
        {
            result = await answerService.AnswerAsync(answerRequest, cancellationToken);
        }
        catch (KnowledgeEmbeddingException exception)
        {
            loggerFactory.CreateLogger(typeof(AssistantEndpoints).FullName!)
                .LogWarning(exception.InnerException, "A trial answer could not embed the question: {Issue}", exception.Message);
            return ApiErrors.WithReason(
                StatusCodes.Status503ServiceUnavailable,
                exception.ProviderNotConfigured
                    ? KnowledgeRetrievalEndpoints.EmbeddingNotConfiguredReason
                    : KnowledgeRetrievalEndpoints.EmbeddingUnavailableReason,
                exception.Message);
        }
        catch (ChatGenerationException exception)
        {
            return ChatErrors.ToApiResult(exception);
        }

        var citedKnowledgeBaseIds = result.Retrieval.Passages.Select(passage => passage.KnowledgeBaseId).Distinct().ToList();
        var knowledgeBaseNames = await dbContext.KnowledgeBases.AsNoTracking()
            .Where(knowledgeBase => citedKnowledgeBaseIds.Contains(knowledgeBase.Id))
            .ToDictionaryAsync(knowledgeBase => knowledgeBase.Id, knowledgeBase => knowledgeBase.Name, cancellationToken);

        return Results.Ok(AssistantDraftEndpoints.ToTrialAnswerResponse(result, knowledgeBaseNames));
    }

    private static readonly NotAvailablePublishingChannelView NotYetAvailable = new(
        "not-available", "官網嵌入與 LINE 對外發布將於後續版本開放。");

    private static Task<List<Guid>> SharedAccountIdsAsync(AppDbContext dbContext, Guid assistantId, CancellationToken cancellationToken) =>
        dbContext.AssistantShares
            .AsNoTracking()
            .Where(share => share.AssistantId == assistantId)
            .OrderBy(share => share.AccountId)
            .Select(share => share.AccountId)
            .ToListAsync(cancellationToken);

    private static async Task<IReadOnlyList<Guid>> ShareTargetIdsAsync(
        AppDbContext dbContext, Assistant assistant, CancellationToken cancellationToken)
    {
        var accountIds = await dbContext.Accounts.AsNoTracking().Select(account => account.Id).ToListAsync(cancellationToken);
        return AssistantPublishingPolicy.ShareTargetIds(assistant.OwnerAccountId, accountIds);
    }

    private static async Task<PlatformSharingView> ToPlatformSharingAsync(
        AppDbContext dbContext, Assistant assistant, IReadOnlyList<Guid> allowedAccountIds, CancellationToken cancellationToken)
    {
        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Id)
            .Select(account => new PlatformShareTargetView(account.Id, account.DisplayName))
            .ToListAsync(cancellationToken);

        var candidateIds = AssistantPublishingPolicy
            .ShareTargetIds(assistant.OwnerAccountId, accounts.Select(account => account.Id))
            .ToHashSet();
        var candidates = accounts.FindAll(account => candidateIds.Contains(account.Id));

        return new PlatformSharingView(
            ToChannelView(assistant, allowedAccountIds), $"/app/chat/{assistant.Id}", allowedAccountIds, candidates);
    }

    private static PublishingChannelView ToChannelView(Assistant assistant, IReadOnlyList<Guid> allowedAccountIds)
    {
        var (status, detail) = assistant.Status == AssistantStatus.Paused
            ? ("paused", "已暫停，除了你自己以外沒有人可以使用這個助理。")
            : allowedAccountIds.Count > 0
                ? ("published", $"已分享給組織內 {allowedAccountIds.Count} 位成員。")
                : ("not-configured", "尚未分享給任何組織成員，目前只有你自己可以使用。");

        return new PublishingChannelView(
            $"channel-platform:{assistant.Id}",
            assistant.Id,
            assistant.OwnerAccountId,
            assistant.Name,
            "platform",
            status,
            detail,
            assistant.UpdatedAt);
    }

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

    /// <summary>
    /// The <see cref="Assistant.ApplySettings"/> field names that change what the assistant
    /// answers — everything <see cref="GroundedAnswerProfile.For"/> builds the prompt and
    /// retrieval from. <c>showCitations</c> and <c>keepConversations</c> only change how replies
    /// are shown or kept, so they do not ask for a rerun.
    /// </summary>
    internal static readonly IReadOnlySet<string> AnswerAffectingSettings = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "purpose", "tone", "roleInstructions", "knowledgeScope", "refusalMessage",
    };

    /// <summary>
    /// Every listed assistant's <see cref="AssistantAcceptanceStatus"/>, in two queries whatever
    /// the number of assistants (which of them have test cases; their kept runs — at most
    /// <see cref="AssistantTestRun.KeptPerAssistant"/> each), derived by
    /// <see cref="AssistantAcceptanceRules.Derive"/>.
    /// </summary>
    internal static async Task<Dictionary<Guid, AssistantAcceptanceStatus>> AcceptanceStatusesAsync(
        AppDbContext dbContext, IReadOnlyCollection<Guid> assistantIds, CancellationToken cancellationToken)
    {
        if (assistantIds.Count == 0)
        {
            return [];
        }

        var tested = (await dbContext.AssistantTestCases
                .AsNoTracking()
                .Where(testCase => assistantIds.Contains(testCase.AssistantId))
                .Select(testCase => testCase.AssistantId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var runs = (await dbContext.AssistantTestRuns
                .AsNoTracking()
                .Where(run => assistantIds.Contains(run.AssistantId))
                .Select(run => new { run.AssistantId, run.Status, run.Trigger, run.RerunTrigger, run.QueuedAt, run.FailedCount })
                .ToListAsync(cancellationToken))
            .ToLookup(
                run => run.AssistantId,
                run => new AssistantTestRunSnapshot(run.Status, run.Trigger, run.RerunTrigger, run.QueuedAt, run.FailedCount));
        return assistantIds.Distinct().ToDictionary(
            assistantId => assistantId,
            assistantId => AssistantAcceptanceRules.Derive(tested.Contains(assistantId), runs[assistantId]));
    }

    private static async Task<AssistantSettingsView> SettingsAsync(
        AppDbContext dbContext, Assistant assistant, Guid viewerId, CancellationToken cancellationToken)
    {
        var knowledgeBaseIds = await ConnectedKnowledgeBaseIdsAsync(dbContext, assistant.Id, cancellationToken);
        var acceptance = await AcceptanceStatusesAsync(dbContext, [assistant.Id], cancellationToken);
        return ToSettings(assistant, viewerId, knowledgeBaseIds, acceptance[assistant.Id]);
    }

    private static AssistantConfigurationView ToConfiguration(
        Assistant assistant, Guid viewerId, AssistantAcceptanceStatus acceptanceStatus) =>
        new(
            assistant.Id,
            assistant.OwnerAccountId,
            assistant.Name,
            assistant.Purpose,
            assistant.Status,
            AssistantAccess.CanManage(assistant, viewerId),
            assistant.CreatedAt,
            assistant.UpdatedAt,
            acceptanceStatus);

    private static AssistantSummaryView ToSummary(Assistant assistant, Guid viewerId) =>
        new(assistant.Id, assistant.Name, assistant.Purpose, assistant.Status, assistant.OwnerAccountId == viewerId);

    private static AssistantSettingsView ToSettings(
        Assistant assistant, Guid viewerId, IReadOnlyList<Guid> knowledgeBaseIds, AssistantAcceptanceStatus acceptanceStatus) =>
        new(
            ToConfiguration(assistant, viewerId, acceptanceStatus),
            knowledgeBaseIds,
            assistant.Tone,
            assistant.RoleInstructions,
            new AssistantAnswerRulesView(
                assistant.KnowledgeScope, assistant.RefusalMessage, assistant.ShowCitations, assistant.KeepConversations));
}
