using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Ai;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Cases;
using SmartAgri.Api.Errors;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Reports;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Organizations;
using SmartAgri.Application.Reports;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Reports;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

// View records are named after, and shaped like, the frontend's views in
// apps/admin/src/app/core/domain/assistant.model.ts, assistant-settings.model.ts and
// publishing.model.ts, so the generated types line up. Deliberate differences:
// - ids are GUIDs;
// - viewerCanManage / viewerIsOwner are added (M2 plan §3, carried over), so the frontend
//   never compares owner ids itself;
// - sharedWithAccountIds (on AssistantConfigurationView) is absent (databaseIds and the
//   database-write rules exist since M4 #148 and periodicReport since #150);
// - audience is stored and editable since #224, but only shown: the frontend mock models an
//   audience-role gate that is not part of the M3 plan's data model (§4) — "who may use it" here
//   is ownership + AssistantShare + use-shared-assistants only (AssistantUseAccess.UsableBy), not
//   audience/role — and it is not a publishing condition either (M5a plan §3 C);
// - minScore is a backend-only field (grounded-answers ADR), not in the frontend model, and
//   is not exposed by this slice's PATCH endpoint;
// - AssistantPublishingView.website is the real WebsiteChannelView since M5a #194
//   (AssistantWebsiteChannelEndpoints; its differences from the frontend's WebsiteEmbedView are
//   listed there); .line is the real LineChannelView since M5b #229
//   (AssistantLineChannelEndpoints; its differences from the frontend's LineSetupView — write-only
//   credentials, connection checks instead of per-field checks — are listed there).

/// <summary>One row of <c>GET /api/v1/assistants</c> (the caller's own assistants).</summary>
/// <param name="ViewerCanManage">Whether the caller may open, change or delete it
/// (<see cref="AssistantAccess.CanManage"/>); always <see langword="true"/> here, since the
/// list only ever contains the caller's own assistants, but included for the same reason
/// <c>KnowledgeBaseSummaryView</c> includes it.</param>
/// <param name="AcceptanceStatus">Derived from its test set and runs (M3.5 plan §3, issue #125;
/// <see cref="AssistantAcceptanceRules"/>); shown only, it does not restrict use.</param>
/// <param name="Audience">Who it is meant for (#224); shown only, it does not restrict use or
/// publishing (<see cref="AssistantAudience"/>).</param>
public sealed record AssistantConfigurationView(
    Guid Id,
    Guid OwnerAccountId,
    string Name,
    string Purpose,
    AssistantStatus Status,
    AssistantAudience Audience,
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
/// <param name="Audience">Who it is meant for (#224); shown on the card only.</param>
public sealed record AssistantSummaryView(
    Guid Id, string Name, string Purpose, AssistantStatus Status, AssistantAudience Audience, bool ViewerIsOwner);

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

/// <summary><c>GET /api/v1/assistants/{id}/publishing</c> response.</summary>
public sealed record AssistantPublishingView(
    Guid AssistantId,
    string AssistantName,
    PlatformSharingView Platform,
    WebsiteChannelView Website,
    LineChannelView Line);

/// <summary><c>PUT /api/v1/assistants/{id}/publishing/platform</c> request: the full set of
/// accounts to share with (not a delta). Plain strings, like
/// <c>UpdateKnowledgeSharingRequest.SharedWithAccountIds</c>, so an unknown or malformed id is
/// this endpoint's own silent filtering (<see cref="AssistantPublishingPolicy.Normalize"/>),
/// not a model-binding failure.</summary>
public sealed record UpdatePlatformSharingRequest(IReadOnlyList<string?>? AccountIds);

/// <summary><c>PUT /api/v1/assistants/{id}/publishing/platform/paused</c> request.</summary>
public sealed record SetPlatformPausedRequest(bool Paused);

/// <summary>The rules governing how an assistant answers (M3 plan §4) and, since M4 #148, which
/// connected database its form requests fill in and the purpose members are told.</summary>
/// <param name="DataWriteDatabaseId">The connected database the assistant's form requests fill in;
/// <see langword="null"/> for none.</param>
/// <param name="DataWritePurpose">Shown to members before they consent; empty without a target.</param>
/// <param name="PeriodicReport"><c>off</c>, <c>weekly</c> or <c>monthly</c> (M4 #150): how often a report
/// on the form-target database is produced for the owner's databases' readers.</param>
public sealed record AssistantAnswerRulesView(
    AssistantKnowledgeScope KnowledgeScope,
    string RefusalMessage,
    bool ShowCitations,
    bool KeepConversations,
    Guid? DataWriteDatabaseId,
    string DataWritePurpose,
    string PeriodicReport);

/// <summary>
/// <c>GET</c>/<c>PATCH .../settings</c> response: settings, connected knowledge bases and
/// answer rules together, since the settings screen edits them as one form
/// (<c>assistant-settings.model.ts</c>'s <c>AssistantSettingsView</c>).
/// </summary>
/// <param name="DatabaseIds">Connected databases (M4 #148), oldest connection first — including
/// one whose owner may no longer use it (still shown, so it can be disconnected; it is not used).</param>
/// <param name="CaseTypeIds">The case types the assistant may propose in a conversation (M7-9 #254), oldest
/// choice first — including a type deactivated since (still shown, so it can be removed; it is not proposed
/// until reactivated). Empty by default.</param>
/// <param name="PeriodicReportAutoDisabled">Set when the periodic report schedule stopped itself after
/// consecutive skipped periods (#179); <see langword="null"/> otherwise (also with no schedule). While set,
/// <c>rules.periodicReport</c> still shows the configured frequency; naming a frequency in a <c>PATCH</c>
/// re-enables it.</param>
public sealed record AssistantSettingsView(
    AssistantConfigurationView Configuration,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<Guid> DatabaseIds,
    IReadOnlyList<Guid> CaseTypeIds,
    AssistantTone Tone,
    string RoleInstructions,
    AssistantAnswerRulesView Rules,
    PeriodicReportAutoDisabledView? PeriodicReportAutoDisabled);

/// <summary>Why and when an assistant's periodic report schedule disabled itself (#179).</summary>
/// <param name="DisabledAt">When the period that disabled it was processed.</param>
/// <param name="Reason">The skip reason of that (the last of the consecutive skipped) period.</param>
/// <param name="SkippedPeriods">How many periods in a row were skipped (the threshold).</param>
/// <param name="Message">What the settings screen shows (zh-TW).</param>
public sealed record PeriodicReportAutoDisabledView(
    DateTimeOffset DisabledAt,
    ReportSkipReason Reason,
    int SkippedPeriods,
    string Message);

/// <summary><c>PATCH /api/v1/assistants/{id}/settings</c> request: a <see langword="null"/>
/// (or absent) field, at any level, is left unchanged. <see cref="Tone"/>, <see cref="Audience"/>
/// and <see cref="AssistantAnswerRulesPatch.KnowledgeScope"/> are plain strings on purpose (as in
/// <c>UpdateKnowledgeSharingRequest</c>): an unknown value is this endpoint's own <c>422</c>,
/// not a model-binding failure.</summary>
/// <param name="Audience">Who it is meant for (#224): <c>account-members</c>,
/// <c>authorized-external-customers</c> or <c>members-and-external-customers</c>.</param>
public sealed record UpdateAssistantSettingsRequest(
    string? Name = null,
    string? Purpose = null,
    string? Tone = null,
    string? RoleInstructions = null,
    AssistantAnswerRulesPatch? Rules = null,
    string? Audience = null);

/// <summary>The <c>rules</c> part of <see cref="UpdateAssistantSettingsRequest"/>; every field optional.</summary>
/// <param name="DataWriteDatabaseId">A connected database's id to make it the form target, <c>""</c>
/// to have none; <see langword="null"/>/absent keeps the current one (M4 #148).</param>
/// <param name="DataWritePurpose">The collection purpose; required (non-blank) while there is a target.</param>
/// <param name="PeriodicReport"><c>off</c>, <c>weekly</c> or <c>monthly</c>; <see langword="null"/>/absent keeps
/// the current one. Needs the form target (<see cref="DataWriteDatabaseId"/>) to report on (M4 #150).</param>
public sealed record AssistantAnswerRulesPatch(
    string? KnowledgeScope = null,
    string? RefusalMessage = null,
    bool? ShowCitations = null,
    bool? KeepConversations = null,
    string? DataWriteDatabaseId = null,
    string? DataWritePurpose = null,
    string? PeriodicReport = null);

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

        // Databases (M4 #148): the same shape as knowledge bases. The id is a plain string so a
        // malformed one is this endpoint's own 422 (connect) or no-op (disconnect), after the
        // ownership check, never a routing 404.
        assistants.MapPut("/{id:guid}/sources/database/{databaseId}", ConnectDatabaseAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapDelete("/{id:guid}/sources/database/{databaseId}", DisconnectDatabaseAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        // Case types the assistant may propose (M7-9 #254; decision U): the owner chooses among the active
        // types, like databases. A plain-string id, so a malformed one is this endpoint's own 422 / no-op.
        assistants.MapPut("/{id:guid}/sources/case-type/{caseTypeId}", ConnectCaseTypeAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapDelete("/{id:guid}/sources/case-type/{caseTypeId}", DisconnectCaseTypeAsync)
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        // Publishing (M3 plan, Slice 3): S+OWN+MP. The website channel's own endpoints are
        // AssistantWebsiteChannelEndpoints (M5a #194); LINE is not implemented yet (M5b).
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
            now,
            value.Audience);
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
        AssistantFormRequests formRequests,
        ReportScheduleService reportSchedules,
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
            assistant.KeepConversations,
            assistant.Audience);

        var validated = AssistantSettingsRules.ForUpdate(
            current,
            request.Name,
            request.Purpose,
            request.Tone,
            request.RoleInstructions,
            request.Rules?.KnowledgeScope,
            request.Rules?.RefusalMessage,
            request.Rules?.ShowCitations,
            request.Rules?.KeepConversations,
            request.Audience);
        if (!validated.IsValid)
        {
            return ApiErrors.ValidationFailed(validated.Failures);
        }

        // The form target (M4 #148): validated against the databases the owner may use right now,
        // before anything is written.
        var links = await dbContext.AssistantDatabases
            .Where(link => link.AssistantId == assistant.Id)
            .ToListAsync(cancellationToken);
        var currentTarget = links.Find(link => link.CollectsForms);
        AssistantFormTarget? formTarget = null;
        if (request.Rules?.DataWriteDatabaseId is not null || request.Rules?.DataWritePurpose is not null)
        {
            var usable = await formRequests.UsableDatabaseIdsAsync(assistant, cancellationToken);
            var target = AssistantFormRequestRules.ForUpdate(
                new AssistantFormTarget(currentTarget?.DatabaseId, currentTarget?.CollectionPurpose ?? string.Empty),
                request.Rules.DataWriteDatabaseId,
                request.Rules.DataWritePurpose,
                usable);
            if (!target.IsValid)
            {
                return ApiErrors.ValidationFailed(target.Failures);
            }

            formTarget = target.Value;
        }

        // The periodic report (M4 #150) reports on the form target as it will be after this update.
        var existingSchedule = await reportSchedules.FindAsync(assistant.Id, cancellationToken);
        var resultingTarget = formTarget is not null ? formTarget.DatabaseId : currentTarget?.DatabaseId;
        var scheduleRequested = request.Rules?.PeriodicReport;
        // Without a report or target field in the request the schedule is left exactly as it is (it may
        // be waiting to record a skipped period after a disconnection).
        var scheduleChoice = new ReportScheduleChoice(existingSchedule?.Frequency, existingSchedule?.DatabaseId);
        if (scheduleRequested is not null || (formTarget is not null && existingSchedule is not null))
        {
            var usableForReport = scheduleRequested is not null && scheduleRequested != ReportScheduleRules.OffName
                ? await formRequests.UsableDatabaseIdsAsync(assistant, cancellationToken)
                : [];
            var reportChoice = ReportScheduleRules.ForUpdate(
                scheduleRequested, existingSchedule?.Frequency, resultingTarget, usableForReport);
            if (!reportChoice.IsValid)
            {
                return ApiErrors.ValidationFailed(reportChoice.Failures);
            }

            scheduleChoice = reportChoice.Value;
        }

        // #179: naming a frequency on an auto-disabled schedule re-enables it (the target was re-checked
        // above as for a new setting).
        var scheduleResumed = ReportScheduleRules.Resumes(existingSchedule?.IsAutoDisabled == true, scheduleRequested, scheduleChoice);
        var scheduleChanged = scheduleResumed
            || ReportScheduleRules.Differs(existingSchedule?.Frequency, existingSchedule?.DatabaseId, scheduleChoice);

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
            now,
            value.Audience);
        var targetChanged = formTarget is not null
            && (formTarget.DatabaseId != currentTarget?.DatabaseId
                || (formTarget.DatabaseId is not null && formTarget.Purpose != currentTarget?.CollectionPurpose));
        if (changed.Count > 0 || targetChanged || scheduleChanged)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (targetChanged)
            {
                // Two steps: the partial unique index allows one target per assistant at any time.
                if (currentTarget is not null && currentTarget.DatabaseId != formTarget!.DatabaseId)
                {
                    currentTarget.StopCollectingForms();
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                if (formTarget!.DatabaseId is { } targetId)
                {
                    links.Single(link => link.DatabaseId == targetId).CollectForms(formTarget.Purpose);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            if (scheduleChanged)
            {
                await reportSchedules.ApplyAsync(assistant, existingSchedule, scheduleChoice, cancellationToken, scheduleResumed);
            }

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
            if (links.Count + await ConnectedDatabaseCountAsync(dbContext, assistant.Id, cancellationToken) == 1)
            {
                return LastSource();
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

    /// <summary>
    /// Connects a database the assistant's owner may use (<see cref="AssistantDatabaseAccess.ConnectableBy"/>,
    /// M4 #148): their own, or one they are a designated data manager of while holding
    /// <c>read-consented-submissions</c>. A database that does not exist (in this organization), a
    /// malformed id and one that is not connectable get the same <c>422</c>, so this cannot be used to
    /// probe another account's databases. Idempotent. A connection alone does not make the assistant
    /// ask for forms: that is the <c>rules.dataWriteDatabaseId</c> setting.
    /// </summary>
    internal static async Task<IResult> ConnectDatabaseAsync(
        Guid id,
        string databaseId,
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

        var assistant = await FindManageableAsync(dbContext.Assistants, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        if (!Guid.TryParse(databaseId, out var parsedId))
        {
            return DatabaseNotConnectable();
        }

        var ownerPermissions = await permissions.GetAsync(assistant.OwnerAccountId, cancellationToken);
        var database = await dbContext.Databases
            .Where(AssistantDatabaseAccess.ConnectableBy(
                assistant.OwnerAccountId,
                ownerPermissions.Contains(AccountPermission.ReadConsentedSubmissions),
                dbContext.DatabaseDataManagers))
            .SingleOrDefaultAsync(candidate => candidate.Id == parsedId, cancellationToken);
        if (database is null)
        {
            return DatabaseNotConnectable();
        }

        var alreadyConnected = await dbContext.AssistantDatabases
            .AnyAsync(link => link.AssistantId == assistant.Id && link.DatabaseId == database.Id, cancellationToken);
        if (!alreadyConnected)
        {
            dbContext.AssistantDatabases.Add(new AssistantDatabase(assistant, database, clock.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await SettingsAsync(dbContext, assistant, callerId, cancellationToken));
    }

    /// <summary>
    /// Disconnects a database (and with it the form target, if it was the one). Refused with
    /// <c>422 last-source</c> for the assistant's last remaining source, like knowledge bases; a
    /// database that is not connected (or a malformed id) is a no-op. Records already submitted stay
    /// in the database: disconnecting only stops new form requests.
    /// </summary>
    internal static async Task<IResult> DisconnectDatabaseAsync(
        Guid id,
        string databaseId,
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

        if (Guid.TryParse(databaseId, out var parsedId))
        {
            var links = await dbContext.AssistantDatabases
                .Where(link => link.AssistantId == assistant.Id)
                .ToListAsync(cancellationToken);
            var target = links.Find(link => link.DatabaseId == parsedId);
            if (target is not null)
            {
                var knowledgeBases = await dbContext.AssistantKnowledgeBases
                    .CountAsync(link => link.AssistantId == assistant.Id, cancellationToken);
                if (links.Count + knowledgeBases == 1)
                {
                    return LastSource();
                }

                dbContext.AssistantDatabases.Remove(target);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        return Results.Ok(await SettingsAsync(dbContext, assistant, callerId, cancellationToken));
    }

    /// <summary>
    /// Adds a case type the assistant may propose (M7-9, decision U): any <b>active</b> type of the
    /// organization; no manager approval. An inactive, unknown, foreign or malformed id is the same
    /// <c>422 case-type-inactive</c>. Idempotent. Each proposal re-checks that the type is still active.
    /// </summary>
    internal static async Task<IResult> ConnectCaseTypeAsync(
        Guid id,
        string caseTypeId,
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

        if (!Guid.TryParse(caseTypeId, out var parsedId)
            || await CaseTypeEndpoints.FindActiveAsync(dbContext, parsedId, cancellationToken) is not { } type)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity, CaseEndpoints.TypeInactiveReason, CaseEndpoints.TypeInactiveMessage, field: "caseTypeIds");
        }

        var already = await dbContext.AssistantCaseTypes
            .AnyAsync(link => link.AssistantId == assistant.Id && link.CaseTypeId == type.Id, cancellationToken);
        if (!already)
        {
            dbContext.AssistantCaseTypes.Add(new AssistantCaseType(assistant, type, clock.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await SettingsAsync(dbContext, assistant, callerId, cancellationToken));
    }

    /// <summary>Removes a case type from the assistant's list (active or not); one not on it, or a
    /// malformed id, is a no-op. Proposals already shown read back as 「無法建立」.</summary>
    internal static async Task<IResult> DisconnectCaseTypeAsync(
        Guid id,
        string caseTypeId,
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

        if (Guid.TryParse(caseTypeId, out var parsedId)
            && await dbContext.AssistantCaseTypes.SingleOrDefaultAsync(
                link => link.AssistantId == assistant.Id && link.CaseTypeId == parsedId, cancellationToken) is { } target)
        {
            dbContext.AssistantCaseTypes.Remove(target);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await SettingsAsync(dbContext, assistant, callerId, cancellationToken));
    }

    private static Task<int> ConnectedDatabaseCountAsync(AppDbContext dbContext, Guid assistantId, CancellationToken cancellationToken) =>
        dbContext.AssistantDatabases.CountAsync(link => link.AssistantId == assistantId, cancellationToken);

    private static IResult LastSource() =>
        ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity,
            "last-source",
            "助理至少要連接一個知識庫或資料庫才能回答問題，無法解除最後一個來源。",
            field: "sources");

    private static IResult DatabaseNotConnectable() =>
        ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity,
            "source-not-connectable",
            "這個資料庫無法連接到這個助理，或已不存在。",
            field: "sources");

    private static IResult SourceNotConnectable() =>
        ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity,
            "source-not-connectable",
            "這個知識庫無法連接到這個助理，或已不存在。",
            field: "sources");

    /// <summary>Platform sharing's and the website channel's real data (the latter since M5a
    /// #194, <see cref="AssistantWebsiteChannelEndpoints.ViewAsync"/>), plus a fixed
    /// <c>not-available</c> placeholder for LINE (M5b).</summary>
    internal static async Task<IResult> GetPublishingAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        IOptions<PublicChannelsOptions> publicChannels,
        OrganizationTokenUsage tokenUsage,
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
        var website = await AssistantWebsiteChannelEndpoints.ViewAsync(dbContext, assistant, publicChannels.Value, tokenUsage, cancellationToken);
        var line = await AssistantLineChannelEndpoints.ViewAsync(dbContext, assistant, publicChannels.Value, tokenUsage, cancellationToken);
        return Results.Ok(new AssistantPublishingView(assistant.Id, assistant.Name, platform, website, line));
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
        var databases = await dbContext.AssistantDatabases
            .AsNoTracking()
            .Where(link => link.AssistantId == assistant.Id)
            .OrderBy(link => link.ConnectedAt)
            .ThenBy(link => link.DatabaseId)
            .ToListAsync(cancellationToken);
        var caseTypeIds = await dbContext.AssistantCaseTypes
            .AsNoTracking()
            .Where(link => link.AssistantId == assistant.Id)
            .OrderBy(link => link.CreatedAt)
            .ThenBy(link => link.CaseTypeId)
            .Select(link => link.CaseTypeId)
            .ToListAsync(cancellationToken);
        var acceptance = await AcceptanceStatusesAsync(dbContext, [assistant.Id], cancellationToken);
        var report = await dbContext.ReportSchedules.AsNoTracking()
            .SingleOrDefaultAsync(schedule => schedule.AssistantId == assistant.Id, cancellationToken);
        return ToSettings(assistant, viewerId, knowledgeBaseIds, databases, caseTypeIds, acceptance[assistant.Id], report);
    }

    private static AssistantConfigurationView ToConfiguration(
        Assistant assistant, Guid viewerId, AssistantAcceptanceStatus acceptanceStatus) =>
        new(
            assistant.Id,
            assistant.OwnerAccountId,
            assistant.Name,
            assistant.Purpose,
            assistant.Status,
            assistant.Audience,
            AssistantAccess.CanManage(assistant, viewerId),
            assistant.CreatedAt,
            assistant.UpdatedAt,
            acceptanceStatus);

    private static AssistantSummaryView ToSummary(Assistant assistant, Guid viewerId) =>
        new(assistant.Id, assistant.Name, assistant.Purpose, assistant.Status, assistant.Audience, assistant.OwnerAccountId == viewerId);

    private static AssistantSettingsView ToSettings(
        Assistant assistant,
        Guid viewerId,
        IReadOnlyList<Guid> knowledgeBaseIds,
        IReadOnlyList<AssistantDatabase> databases,
        IReadOnlyList<Guid> caseTypeIds,
        AssistantAcceptanceStatus acceptanceStatus,
        ReportSchedule? schedule)
    {
        var formTarget = databases.FirstOrDefault(link => link.CollectsForms);
        return new(
            ToConfiguration(assistant, viewerId, acceptanceStatus),
            knowledgeBaseIds,
            [.. databases.Select(link => link.DatabaseId)],
            caseTypeIds,
            assistant.Tone,
            assistant.RoleInstructions,
            new AssistantAnswerRulesView(
                assistant.KnowledgeScope,
                assistant.RefusalMessage,
                assistant.ShowCitations,
                assistant.KeepConversations,
                formTarget?.DatabaseId,
                formTarget?.CollectionPurpose ?? string.Empty,
                ReportScheduleChoice.Wire(schedule?.Frequency)),
            schedule is { AutoDisabledAt: { } disabledAt, AutoDisabledReason: { } reason }
                ? new PeriodicReportAutoDisabledView(
                    disabledAt, reason, schedule.ConsecutiveSkips, ReportScheduleRules.AutoDisabledMessage(reason))
                : null);
    }
}
