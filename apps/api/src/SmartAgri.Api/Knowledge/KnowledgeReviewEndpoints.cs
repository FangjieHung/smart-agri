using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Knowledge;

/// <summary>An account next to what it did: id and display name only.</summary>
public sealed record KnowledgeAccountView(Guid Id, string DisplayName);

/// <summary>One version in a document's history, and the response of an approval.</summary>
/// <param name="FileName">The file this version was uploaded as (the document keeps the name of
/// its first file).</param>
/// <param name="Status">Its processing status ("處理狀態").</param>
/// <param name="State">Its review state: pending review, scheduled, effective (the version in
/// effect) or archived (<see cref="KnowledgeVersionStates"/>). Whether the whole document is
/// disabled is the document's own flag.</param>
/// <param name="EffectiveFrom">From when it is (or was) in effect; <see langword="null"/> while
/// pending review.</param>
public sealed record KnowledgeVersionView(
    Guid Id,
    Guid DocumentId,
    int VersionNumber,
    string FileName,
    string ContentType,
    long SizeBytes,
    KnowledgeDocumentStatus Status,
    string? Issue,
    KnowledgeVersionState State,
    DateTimeOffset? EffectiveFrom,
    KnowledgeAccountView UploadedBy,
    DateTimeOffset UploadedAt,
    KnowledgeAccountView? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One entry of a document's activity log: who did what, and when; never any
/// content.</summary>
/// <param name="Actor"><see langword="null"/> for system actions.</param>
/// <param name="VersionNumber">The version acted on, if the action concerns one.</param>
public sealed record KnowledgeActivityView(
    Guid Id,
    KnowledgeActivityAction Action,
    KnowledgeAccountView? Actor,
    DateTimeOffset At,
    Guid? VersionId,
    int? VersionNumber);

/// <summary><c>GET /api/v1/knowledge-bases/{id}/documents/{docId}</c> response.</summary>
/// <param name="Document">The document as the knowledge base lists it.</param>
/// <param name="DisabledAt">While disabled in an emergency: when, by whom and why; otherwise
/// all <see langword="null"/>.</param>
/// <param name="Versions">Newest first.</param>
/// <param name="Activities">Everything done to the document, newest first.</param>
public sealed record KnowledgeDocumentDetailView(
    KnowledgeDocumentView Document,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    KnowledgeAccountView? DisabledBy,
    string? DisabledReason,
    IReadOnlyList<KnowledgeVersionView> Versions,
    IReadOnlyList<KnowledgeActivityView> Activities);

/// <summary>
/// <c>POST /api/v1/knowledge-bases/{id}/versions/approve</c> request. Strings on purpose: an
/// entry that is not a GUID is refused like any version not in the knowledge base, and a
/// malformed or zone-less <see cref="EffectiveFrom"/> is this endpoint's own <c>422</c>
/// (<see cref="KnowledgeReviewRules"/>), not a model-binding failure.
/// </summary>
/// <param name="EffectiveFrom">ISO 8601 with a time zone; absent means now. Up to a minute in
/// the past counts as now (clock skew).</param>
public sealed record ApproveKnowledgeVersionsRequest(IReadOnlyList<string?>? VersionIds, string? EffectiveFrom = null);

/// <summary><c>POST .../documents/{docId}/disable</c> request; <see cref="Reason"/> is required
/// (at most 500 characters), and missing it is this endpoint's own <c>422</c>.</summary>
public sealed record DisableKnowledgeDocumentRequest(string? Reason);

/// <summary>
/// Versions, approval and emergency disabling (M2 plan, Slice 8; ticket #42): a document's
/// version history with its activity log, batch approval, disable and enable. (Uploading a new
/// version is <see cref="KnowledgeDocumentEndpoints"/>'.)
/// </summary>
/// <remarks>
/// <para>
/// Owner only, exactly like the other knowledge endpoints: a knowledge base that does not exist,
/// belongs to another organization or to someone else — and a document that is not in it — all
/// get the same <c>403 knowledge-base</c>. Every change writes a content-free activity row naming
/// the caller in the same save.
/// </para>
/// <para>
/// What is retrievable follows from these rows alone (<see cref="RetrievableChunks"/>): an
/// approval takes effect at its <c>effectiveFrom</c> with nothing further to run, and a disable
/// stops retrieval with the very save that records it.
/// </para>
/// </remarks>
public static class KnowledgeReviewEndpoints
{
    /// <summary>The <c>409</c> reason of disabling a disabled document.</summary>
    public const string AlreadyDisabledReason = "document-already-disabled";

    /// <summary>The <c>409</c> reason of enabling a document that is not disabled.</summary>
    public const string NotDisabledReason = "document-not-disabled";

    /// <summary>The <c>409</c> reason of an approval that raced another change and cannot tell
    /// what to refuse (the versions are approvable again, e.g. a concurrent request failed).</summary>
    public const string ApprovalConflictReason = "approval-conflict";

    public const string ApprovalConflictMessage = "這些版本剛剛有其他變更，請重新整理後再試一次。";

    /// <summary>Shown when an account an entry refers to cannot be found (accounts are not
    /// deleted in M2, so this should not happen).</summary>
    public const string UnknownAccountName = "（找不到的帳號）";

    public static IEndpointRouteBuilder MapKnowledgeReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var knowledgeBase = endpoints.MapGroup("/api/v1/knowledge-bases/{id:guid}")
            .RequireAuthorization();

        knowledgeBase.MapGet("/documents/{documentId:guid}", GetDocumentAsync)
            .Produces<KnowledgeDocumentDetailView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        knowledgeBase.MapPost("/versions/approve", ApproveAsync)
            .Produces<IReadOnlyList<KnowledgeVersionView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        knowledgeBase.MapPost("/documents/{documentId:guid}/disable", DisableAsync)
            .Produces<KnowledgeDocumentView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        knowledgeBase.MapPost("/documents/{documentId:guid}/enable", EnableAsync)
            .Produces<KnowledgeDocumentView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        return endpoints;
    }

    /// <summary>The document as listed, its disable details, every version (newest first) with
    /// its processing status, review state, uploader and approver, and its activity log
    /// (newest first). File contents and chunk text are never read.</summary>
    internal static async Task<IResult> GetDocumentAsync(
        Guid id,
        Guid documentId,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var knowledgeBase = await KnowledgeBaseEndpoints.FindManageableAsync(
            dbContext.KnowledgeBases.AsNoTracking(), id, callerId, cancellationToken);
        if (knowledgeBase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var document = await dbContext.KnowledgeDocuments
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == documentId && candidate.KnowledgeBaseId == knowledgeBase.Id, cancellationToken);
        if (document is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var now = clock.GetUtcNow();
        var row = await KnowledgeDocumentEndpoints.DocumentViewAsync(dbContext, documentId, now, cancellationToken);
        var versions = await dbContext.KnowledgeDocumentVersions
            .AsNoTracking()
            .Where(version => version.DocumentId == documentId)
            .OrderByDescending(version => version.VersionNumber)
            .ToListAsync(cancellationToken);
        var activities = await dbContext.KnowledgeActivities
            .AsNoTracking()
            .Where(activity => activity.KnowledgeBaseId == knowledgeBase.Id && activity.DocumentId == documentId)
            .OrderByDescending(activity => activity.At)
            .ThenByDescending(activity => activity.Id)
            .ToListAsync(cancellationToken);

        var names = await DisplayNamesAsync(
            dbContext,
            [
                .. versions.Select(version => version.UploadedByAccountId),
                .. versions.Select(version => version.ApprovedByAccountId),
                document.DisabledByAccountId,
                .. activities.Select(activity => activity.ActorAccountId),
            ],
            cancellationToken);
        var numbers = versions.ToDictionary(version => version.Id, version => version.VersionNumber);

        return Results.Ok(new KnowledgeDocumentDetailView(
            row,
            document.CreatedAt,
            document.DisabledAt,
            Account(names, document.DisabledByAccountId),
            document.DisabledReason,
            [.. versions.Select(version => ToView(version, version.VersionNumber == row.EffectiveVersionNumber, now, names))],
            [
                .. activities.Select(activity => new KnowledgeActivityView(
                    activity.Id,
                    activity.Action,
                    Account(names, activity.ActorAccountId),
                    activity.At,
                    activity.VersionId,
                    activity.VersionId is { } versionId && numbers.TryGetValue(versionId, out var number) ? number : null)),
            ]));
    }

    /// <summary>
    /// Approves every listed version, effective from <c>effectiveFrom</c> (default now), or none:
    /// the request's shape first (<c>422</c> with field errors), then every version must be this
    /// knowledge base's, processed <c>ready</c> or <c>partially-readable</c> and still pending
    /// review — otherwise <c>422 versions-not-approvable</c> naming each refused entry as
    /// <c>versionIds[i]</c>, and nothing is written. One save: every version, and one
    /// <see cref="KnowledgeActivityAction.VersionApproved"/> row per version naming the caller.
    /// Returns the approved versions (each once, in request order) as they now stand.
    /// </summary>
    internal static async Task<IResult> ApproveAsync(
        Guid id,
        ApproveKnowledgeVersionsRequest? request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var knowledgeBase = await KnowledgeBaseEndpoints.FindManageableAsync(
            dbContext.KnowledgeBases.AsNoTracking(), id, callerId, cancellationToken);
        if (knowledgeBase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var now = clock.GetUtcNow();
        var validation = KnowledgeReviewRules.ValidateApproval(request?.VersionIds, request?.EffectiveFrom, now);
        if (!validation.IsValid)
        {
            return ApiErrors.ValidationFailed(validation.Failures);
        }

        var approval = validation.Value;
        var ids = approval.VersionIds.Where(versionId => versionId is not null).Select(versionId => versionId!.Value).Distinct().ToList();

        // Tracked: approved in place, with ReviewState and ProcessingStatus as concurrency tokens.
        var versions = await dbContext.KnowledgeDocumentVersions
            .Where(version => version.KnowledgeBaseId == knowledgeBase.Id && ids.Contains(version.Id))
            .ToDictionaryAsync(version => version.Id, cancellationToken);
        if (Refusal(approval, versions) is { } refused)
        {
            return refused;
        }

        foreach (var versionId in ids)
        {
            var version = versions[versionId];
            version.Approve(callerId, approval.EffectiveFrom, now);
            dbContext.KnowledgeActivities.Add(KnowledgeActivity.VersionApproved(version, callerId, now));
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A version changed since it was read (approved by a concurrent request, or deleted
            // with its document): the whole save was rolled back, as the rule requires.
            dbContext.ChangeTracker.Clear();
            var current = await dbContext.KnowledgeDocumentVersions
                .AsNoTracking()
                .Where(version => version.KnowledgeBaseId == knowledgeBase.Id && ids.Contains(version.Id))
                .ToDictionaryAsync(version => version.Id, cancellationToken);
            return Refusal(approval, current)
                ?? ApiErrors.WithReason(StatusCodes.Status409Conflict, ApprovalConflictReason, ApprovalConflictMessage);
        }

        return Results.Ok(await VersionViewsAsync(dbContext, ids, now, cancellationToken));
    }

    /// <summary>
    /// Disables the document at once: from this save on, none of its chunks is retrievable,
    /// whatever its versions' approval. The reason is required (<c>422</c>); a document already
    /// disabled is <c>409</c>. Records who and when (on the document and as a
    /// <see cref="KnowledgeActivityAction.DocumentDisabled"/> row) and returns the document as
    /// listed.
    /// </summary>
    internal static async Task<IResult> DisableAsync(
        Guid id,
        Guid documentId,
        DisableKnowledgeDocumentRequest? request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var document = await FindManageableDocumentAsync(dbContext, id, documentId, callerId, cancellationToken);
        if (document is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var reason = KnowledgeReviewRules.ValidateDisableReason(request?.Reason);
        if (!reason.IsValid)
        {
            return ApiErrors.ValidationFailed(reason.Failures);
        }

        if (document.DisabledAt is not null)
        {
            return ApiErrors.WithReason(StatusCodes.Status409Conflict, AlreadyDisabledReason, KnowledgeReviewRules.AlreadyDisabledMessage);
        }

        var now = clock.GetUtcNow();
        document.Disable(callerId, reason.Value, now);
        dbContext.KnowledgeActivities.Add(KnowledgeActivity.DocumentDisabled(document, callerId, now));
        return await SaveStateChangeAsync(dbContext, document, now, AlreadyDisabledReason, KnowledgeReviewRules.AlreadyDisabledMessage, cancellationToken);
    }

    /// <summary>
    /// Lifts an emergency disable: the document is exactly as before it (plus whatever was
    /// approved meanwhile, which takes effect now). A document that is not disabled is
    /// <c>409</c>. Records a <see cref="KnowledgeActivityAction.DocumentEnabled"/> row naming the
    /// caller and returns the document as listed.
    /// </summary>
    internal static async Task<IResult> EnableAsync(
        Guid id,
        Guid documentId,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var document = await FindManageableDocumentAsync(dbContext, id, documentId, callerId, cancellationToken);
        if (document is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        if (document.DisabledAt is null)
        {
            return ApiErrors.WithReason(StatusCodes.Status409Conflict, NotDisabledReason, KnowledgeReviewRules.NotDisabledMessage);
        }

        var now = clock.GetUtcNow();
        document.Enable();
        dbContext.KnowledgeActivities.Add(KnowledgeActivity.DocumentEnabled(document, callerId, now));
        return await SaveStateChangeAsync(dbContext, document, now, NotDisabledReason, KnowledgeReviewRules.NotDisabledMessage, cancellationToken);
    }

    /// <summary>The document (tracked) if it is in a knowledge base the caller may manage;
    /// <see langword="null"/> alike for every other case.</summary>
    private static async Task<KnowledgeDocument?> FindManageableDocumentAsync(
        AppDbContext dbContext,
        Guid knowledgeBaseId,
        Guid documentId,
        Guid callerId,
        CancellationToken cancellationToken)
    {
        var knowledgeBase = await KnowledgeBaseEndpoints.FindManageableAsync(
            dbContext.KnowledgeBases.AsNoTracking(), knowledgeBaseId, callerId, cancellationToken);
        return knowledgeBase is null
            ? null
            : await dbContext.KnowledgeDocuments.SingleOrDefaultAsync(
                document => document.Id == documentId && document.KnowledgeBaseId == knowledgeBase.Id,
                cancellationToken);
    }

    /// <summary>Saves a disable or enable. <c>DisabledAt</c> is a concurrency token, so when a
    /// concurrent request changed it first, nothing is written and the caller gets the same
    /// <c>409</c> as if it had come second (or <c>403</c> if the document is gone).</summary>
    private static async Task<IResult> SaveStateChangeAsync(
        AppDbContext dbContext,
        KnowledgeDocument document,
        DateTimeOffset now,
        string conflictReason,
        string conflictMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            var exists = await dbContext.KnowledgeDocuments.AnyAsync(candidate => candidate.Id == document.Id, cancellationToken);
            return exists
                ? ApiErrors.WithReason(StatusCodes.Status409Conflict, conflictReason, conflictMessage)
                : ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        return Results.Ok(await KnowledgeDocumentEndpoints.DocumentViewAsync(dbContext, document.Id, now, cancellationToken));
    }

    /// <summary>The <c>422</c> for a batch with any version that cannot be approved, or
    /// <see langword="null"/> when all can.</summary>
    private static IResult? Refusal(KnowledgeApprovalRequest approval, IReadOnlyDictionary<Guid, KnowledgeDocumentVersion> versions)
    {
        var refusals = KnowledgeReviewRules.ApprovalRefusals(approval.VersionIds, versions);
        return refusals.Count == 0
            ? null
            : ApiErrors.Refused(KnowledgeReviewRules.VersionsNotApprovableReason, KnowledgeReviewRules.BatchRefusedMessage(refusals.Count), refusals);
    }

    /// <summary>The versions with these ids as they stand at <paramref name="now"/>, in the
    /// given order.</summary>
    private static async Task<List<KnowledgeVersionView>> VersionViewsAsync(
        AppDbContext dbContext,
        IReadOnlyList<Guid> ids,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var versions = await dbContext.KnowledgeDocumentVersions
            .AsNoTracking()
            .Where(version => ids.Contains(version.Id))
            .ToDictionaryAsync(version => version.Id, cancellationToken);
        var documentIds = versions.Values.Select(version => version.DocumentId).Distinct().ToList();
        var inEffect = (await dbContext.KnowledgeDocumentVersions
                .AsNoTracking()
                .Where(version => documentIds.Contains(version.DocumentId))
                .Where(RetrievableChunks.CurrentEffectiveVersion(now))
                .Select(version => version.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var names = await DisplayNamesAsync(
            dbContext,
            [.. versions.Values.Select(version => version.UploadedByAccountId), .. versions.Values.Select(version => version.ApprovedByAccountId)],
            cancellationToken);
        return [.. ids.Where(versions.ContainsKey).Select(versionId => ToView(versions[versionId], inEffect.Contains(versionId), now, names))];
    }

    private static KnowledgeVersionView ToView(
        KnowledgeDocumentVersion version,
        bool isCurrentEffective,
        DateTimeOffset now,
        IReadOnlyDictionary<Guid, string> names) =>
        new(
            version.Id,
            version.DocumentId,
            version.VersionNumber,
            version.FileName,
            version.ContentType,
            version.SizeBytes,
            version.ProcessingStatus,
            version.Issue,
            KnowledgeVersionStates.Of(version.ReviewState, version.EffectiveFrom, isCurrentEffective, now),
            version.EffectiveFrom,
            Account(names, version.UploadedByAccountId)!,
            version.UploadedAt,
            Account(names, version.ApprovedByAccountId),
            version.ApprovedAt,
            version.UpdatedAt);

    /// <summary>Display names of the organization's accounts among <paramref name="ids"/>.</summary>
    private static async Task<Dictionary<Guid, string>> DisplayNamesAsync(
        AppDbContext dbContext,
        IEnumerable<Guid?> ids,
        CancellationToken cancellationToken)
    {
        var wanted = ids.Where(accountId => accountId is not null).Select(accountId => accountId!.Value).Distinct().ToList();
        return await dbContext.Accounts
            .AsNoTracking()
            .Where(account => wanted.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, account => account.DisplayName, cancellationToken);
    }

    private static KnowledgeAccountView? Account(IReadOnlyDictionary<Guid, string> names, Guid? accountId) =>
        accountId is { } known
            ? new KnowledgeAccountView(known, names.TryGetValue(known, out var name) ? name : UnknownAccountName)
            : null;
}
