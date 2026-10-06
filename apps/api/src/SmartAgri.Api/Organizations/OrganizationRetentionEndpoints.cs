using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Organizations;

/// <summary>A shorter retention waiting out its 7-day buffer.</summary>
/// <param name="Days">The retention that will apply.</param>
/// <param name="EffectiveAt">When it applies: the first daily cleanup at or after it switches.</param>
public sealed record OrganizationRetentionPendingView(int Days, DateTimeOffset EffectiveAt);

/// <summary><c>GET /api/v1/organization/retention</c>'s response (M6 plan §3 F, issue #241).</summary>
/// <param name="Days">The retention now: days, or <see langword="null"/> for forever.</param>
/// <param name="Pending">A shorter retention waiting out its buffer, or <see langword="null"/>.</param>
/// <param name="Options">The periods a manager may choose, in days (forever is <c>days: null</c>).</param>
/// <param name="CanChange">Whether the caller is the organization's manager (may preview and <c>PUT</c>).</param>
/// <param name="LastChange">The last change (a pending retention taking effect is the system's), or
/// <see langword="null"/> before the first.</param>
/// <param name="Revision">What a <c>PUT</c> sends back (<c>Organization.SettingsRevision</c>, shared with
/// the chat model).</param>
public sealed record OrganizationRetentionView(
    int? Days,
    OrganizationRetentionPendingView? Pending,
    IReadOnlyList<int> Options,
    bool CanChange,
    OrganizationSettingChangeView? LastChange,
    int Revision);

/// <summary><c>GET /api/v1/organization/retention/preview?days=N</c>: roughly how many threads a
/// retention of <see cref="Days"/> would delete.</summary>
/// <param name="ThreadCount">Threads whose last message is before <see cref="Cutoff"/> now — what a
/// cleanup at this instant would delete. After the buffer the real number is larger.</param>
/// <param name="Cutoff">00:00 of the <c>Statistics:TimeZone</c> day <see cref="Days"/> days ago.</param>
public sealed record OrganizationRetentionPreviewView(int Days, int ThreadCount, DateTimeOffset Cutoff);

/// <summary><c>PUT /api/v1/organization/retention</c>: <see cref="Days"/> one of the options or
/// <see langword="null"/> for forever; <see cref="Revision"/> as read.</summary>
public sealed record UpdateOrganizationRetentionRequest(int? Days, int Revision);

/// <summary>
/// The organization's conversation retention (M6 plan §5 Slice 4, issue #241). Anyone in the
/// organization may read it — their conversations are kept by it — while only the manager may preview
/// a change or make one (<see cref="OrganizationAdminPolicy.RequireOrganizationAdmin{TBuilder}"/>,
/// <c>403 organization-settings</c>). The daily cleanup is <see cref="RetentionCleanupHandler"/>.
/// </summary>
public static class OrganizationRetentionEndpoints
{
    public const string Path = "/api/v1/organization/retention";

    public const string PreviewPath = Path + "/preview";

    internal const string UnknownDaysMessage = "保存期限只能是 30、90、180、365 天或永久。";

    /// <summary>The changes a section's 「上次變更」 shows: the manager's, and the system switching
    /// to a pending value. A cleanup is not a settings change.</summary>
    private static readonly OrganizationActivityAction[] ChangeActions =
    [
        OrganizationActivityAction.RetentionChanged,
        OrganizationActivityAction.RetentionChangeCancelled,
        OrganizationActivityAction.RetentionTookEffect,
    ];

    public static IEndpointRouteBuilder MapOrganizationRetentionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, GetAsync)
            .RequireAuthorization()
            .Produces<OrganizationRetentionView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        endpoints.MapGet(PreviewPath, PreviewAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<OrganizationRetentionPreviewView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPut(Path, UpdateAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<OrganizationRetentionView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    internal static async Task<IResult> GetAsync(
        HttpContext httpContext, AppDbContext dbContext, RequestAccountRole roles, CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is null
            || await OrganizationSettings.FindAsync(dbContext, tracked: false, cancellationToken) is not { } organization)
        {
            return ApiErrors.Unauthorized();
        }

        return Results.Ok(await ViewAsync(dbContext, roles, httpContext, organization, cancellationToken));
    }

    /// <summary><paramref name="days"/> must be one of the options (else <c>422</c> <c>errors.days</c>).
    /// Counted with the same cutoff the cleanup deletes by.</summary>
    internal static async Task<IResult> PreviewAsync(
        int? days, HttpContext httpContext, RetentionCleanupService cleanup, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is null)
        {
            return ApiErrors.Unauthorized();
        }

        if (days is not { } value || !OrganizationRetention.IsOffered(value))
        {
            return UnknownDays();
        }

        var now = clock.GetUtcNow();
        var count = await cleanup.CountExpiredThreadsAsync(value, now, cancellationToken);
        return Results.Ok(new OrganizationRetentionPreviewView(
            value, count, Application.Organizations.RetentionCleanupRules.Cutoff(now, value, cleanup.TimeZone)));
    }

    /// <summary>
    /// Order of checks after the manager check: <c>days</c> is offered (else <c>422</c>), the revision is
    /// current (else <c>409</c>); a value that changes nothing answers <c>200</c> without writing. A
    /// change writes its activity in the same transaction and, the first time the organization has a
    /// retention in days, starts the daily cleanup chain.
    /// </summary>
    internal static async Task<IResult> UpdateAsync(
        UpdateOrganizationRetentionRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountRole roles,
        RetentionCleanupService cleanup,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId
            || await OrganizationSettings.FindAsync(dbContext, tracked: true, cancellationToken) is not { } organization)
        {
            return ApiErrors.Unauthorized();
        }

        if (request.Days is { } requested && !OrganizationRetention.IsOffered(requested))
        {
            return UnknownDays();
        }

        // PostgreSQL keeps microseconds: the effective time in the activity's detail (jsonb) must be
        // the one stored in the column and answered.
        var now = clock.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        var (fromDays, fromPending, fromPendingAt) =
            (organization.RetentionDays, organization.PendingRetentionDays, organization.PendingRetentionEffectiveAt);
        switch (organization.ChangeRetention(request.Days, request.Revision, now))
        {
            case OrganizationSettingsChange.RevisionConflict:
                return OrganizationSettings.RevisionConflict();
            case OrganizationSettingsChange.Changed:
                dbContext.OrganizationActivities.Add(
                    fromPending is { } cancelled && organization.PendingRetentionDays is null && organization.RetentionDays == fromDays
                        ? OrganizationActivity.RetentionChangeCancelled(organization.Id, callerId, now, fromDays, cancelled, fromPendingAt!.Value)
                        : OrganizationActivity.RetentionChanged(
                            organization.Id, callerId, now, fromDays, request.Days, organization.PendingRetentionEffectiveAt ?? now));
                try
                {
                    await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await RetentionCleanupChain.EnsureStartedAsync(dbContext, organization.Id, now, cleanup.TimeZone, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Another PUT (or the cleanup making a pending value current) saved between our
                    // read and our write: nothing of ours was written.
                    return OrganizationSettings.RevisionConflict();
                }

                break;
        }

        return Results.Ok(await ViewAsync(dbContext, roles, httpContext, organization, cancellationToken));
    }

    private static IResult UnknownDays() =>
        ApiErrors.ValidationFailed(UnknownDaysMessage, new Dictionary<string, string[]> { ["days"] = [UnknownDaysMessage] });

    private static async Task<OrganizationRetentionView> ViewAsync(
        AppDbContext dbContext,
        RequestAccountRole roles,
        HttpContext httpContext,
        Organization organization,
        CancellationToken cancellationToken) =>
        new(
            organization.RetentionDays,
            organization is { PendingRetentionDays: { } pending, PendingRetentionEffectiveAt: { } effectiveAt }
                ? new OrganizationRetentionPendingView(pending, effectiveAt)
                : null,
            OrganizationRetention.Options,
            await roles.IsOrganizationAdminAsync(httpContext.User, cancellationToken),
            await OrganizationSettings.LastChangeAsync(dbContext, ChangeActions, cancellationToken),
            organization.SettingsRevision);
}
