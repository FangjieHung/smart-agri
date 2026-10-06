using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

/// <summary>One allowed domain with its passive installation detection (M5a plan §3 H).</summary>
/// <param name="AddedAt">When it was added to the list.</param>
/// <param name="LastSeenAt">When a visitor's chat window last reported being embedded on it
/// (written from M5a Slice 4 on); for information only.</param>
public sealed record WebsiteDomainView(string Domain, DateTimeOffset AddedAt, DateTimeOffset? LastSeenAt);

/// <summary>A connected knowledge base the assistant's owner does not own (decision B).</summary>
public sealed record WebsiteKnowledgeBaseView(Guid Id, string Name);

/// <summary>
/// The website channel (「官網嵌入」), shaped after the frontend's <c>WebsiteEmbedView</c>
/// (<c>publishing.model.ts</c>): <see cref="Channel"/> plus the settings. Deliberate differences:
/// <list type="bullet">
/// <item><see cref="State"/> (the owner's choice) and <see cref="ServingState"/> /
/// <see cref="ServingMessage"/> (whether it answers visitors right now, derived on this read, M5a
/// plan §3 C) replace the mock's simulated install check;</item>
/// <item><see cref="Domains"/> carries each allowed domain's <c>lastSeenAt</c> (passive detection,
/// plan §3 H) alongside the plain <see cref="AllowedDomains"/> list the settings form edits;</item>
/// <item><see cref="EmbedCode"/> is real (<c>PublicChannels:PublicBaseUrl</c>), or
/// <see langword="null"/> while the server has no public address;</item>
/// <item><see cref="Revision"/> is <c>0</c> until the settings are first saved (the values shown
/// are then the defaults); a <c>PUT</c> sends back the revision it read.</item>
/// </list>
/// </summary>
/// <param name="Channel">Its card: <c>not-configured</c> before the first save, <c>testing</c> while
/// not published, <c>published</c> while serving, <c>needs-attention</c> while suspended, <c>paused</c>.</param>
/// <param name="AcceptanceStatus">The assistant's acceptance status now (the publishing gate needs <c>passed</c>).</param>
/// <param name="NonOwnedKnowledgeBases">Connected knowledge bases not owned by the assistant's owner:
/// publishing is refused, and a published channel is suspended, while there is any.</param>
/// <param name="PublishedAt">When it was published from a draft; <see langword="null"/> while a draft.</param>
public sealed record WebsiteChannelView(
    PublishingChannelView Channel,
    string DisplayName,
    string WelcomeMessage,
    WebsiteBrandColor BrandColor,
    WebsiteLauncherPosition Position,
    IReadOnlyList<string> AllowedDomains,
    IReadOnlyList<WebsiteDomainView> Domains,
    WebsiteChannelState State,
    WebsiteServingState ServingState,
    string ServingMessage,
    AssistantAcceptanceStatus AcceptanceStatus,
    IReadOnlyList<WebsiteKnowledgeBaseView> NonOwnedKnowledgeBases,
    string? EmbedCode,
    DateTimeOffset? PublishedAt,
    int Revision);

/// <summary>
/// <c>PUT /api/v1/assistants/{id}/publishing/website</c>: the whole settings form, replacing the
/// stored one, and the <see cref="Revision"/> it was read at (<c>0</c> before the first save).
/// Colour and position are plain strings, so an unknown value is this endpoint's own <c>422</c>.
/// </summary>
public sealed record UpdateWebsiteChannelRequest(
    string? DisplayName,
    string? WelcomeMessage,
    string? BrandColor,
    string? Position,
    IReadOnlyList<string?>? AllowedDomains,
    int Revision);

/// <summary><c>PUT /api/v1/assistants/{id}/publishing/website/paused</c> request.</summary>
public sealed record SetWebsitePausedRequest(bool Paused);

/// <summary>
/// The website channel's settings and publishing (M5a plan §5 Slice 2, issue #194): S+OWN+MP,
/// exactly like platform sharing — <c>manage-publishing</c> and the caller owning the assistant. An
/// id that does not exist, belongs to another organization or to someone else gets the same
/// <c>403 publishing</c>, byte for byte.
/// </summary>
/// <remarks>
/// Rules (validation, the publishing gate, the serving state) live in
/// <see cref="WebsiteChannelRules"/> and <see cref="WebsiteChannelServing"/> and are unit tested
/// there; this class only loads, calls them, writes and maps.
/// </remarks>
public static class AssistantWebsiteChannelEndpoints
{
    public const string RevisionConflictReason = "website-revision-conflict";

    public const string PublishRefusedReason = "website-publish-refused";

    public const string NotPublishedReason = "website-not-published";

    public static IEndpointRouteBuilder MapAssistantWebsiteChannelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var assistants = endpoints.MapGroup("/api/v1/assistants")
            .RequireAuthorization();

        assistants.MapGet("/{id:guid}/publishing/website", GetAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<WebsiteChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        assistants.MapPut("/{id:guid}/publishing/website", UpdateAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<WebsiteChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapPost("/{id:guid}/publishing/website:publish", PublishAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<WebsiteChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapPut("/{id:guid}/publishing/website/paused", SetPausedAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<WebsiteChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapPost("/{id:guid}/publishing/website:unpublish", UnpublishAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<WebsiteChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    internal static async Task<IResult> GetAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        IOptions<PublicChannelsOptions> options,
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

        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>
    /// Order of checks: owner (else <c>403</c>), <see cref="WebsiteChannelRules.ForUpdate"/> (else
    /// <c>422</c> naming every broken field), then the revision (else <c>409</c>) — nothing is
    /// written on any refusal. The first save creates the channel as a draft (revision 0 → 1). The
    /// domains are replaced as a set: a kept domain keeps its <c>addedAt</c> and <c>lastSeenAt</c>.
    /// </summary>
    internal static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateWebsiteChannelRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        IOptions<PublicChannelsOptions> options,
        OrganizationTokenUsage tokenUsage,
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

        var validated = WebsiteChannelRules.ForUpdate(
            request.DisplayName, request.WelcomeMessage, request.BrandColor, request.Position, request.AllowedDomains);
        if (!validated.IsValid)
        {
            return ApiErrors.ValidationFailed(validated.Failures);
        }

        var settings = validated.Value;
        var now = clock.GetUtcNow();
        var channel = await dbContext.AssistantWebsiteChannels
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        if (channel is null)
        {
            if (request.Revision != 0)
            {
                return RevisionConflict();
            }

            channel = new AssistantWebsiteChannel(
                assistant, settings.DisplayName, settings.WelcomeMessage, settings.BrandColor, settings.Position, now);
            dbContext.AssistantWebsiteChannels.Add(channel);
        }
        else if (!channel.TryApplySettings(
                     settings.DisplayName, settings.WelcomeMessage, settings.BrandColor, settings.Position, request.Revision, now))
        {
            return RevisionConflict();
        }

        // Tracked, so removed rows become real DELETEs carrying their own OrganizationId (the write
        // guard's concurrency token), as in AssistantEndpoints.UpdatePlatformSharingAsync.
        var existing = await dbContext.AssistantWebsiteDomains
            .Where(domain => domain.AssistantId == assistant.Id)
            .ToListAsync(cancellationToken);
        var keep = settings.AllowedDomains.ToHashSet(StringComparer.Ordinal);
        var existingNames = existing.Select(domain => domain.Domain).ToHashSet(StringComparer.Ordinal);
        dbContext.AssistantWebsiteDomains.RemoveRange(existing.Where(domain => !keep.Contains(domain.Domain)));
        dbContext.AssistantWebsiteDomains.AddRange(settings.AllowedDomains
            .Where(domain => !existingNames.Contains(domain))
            .Select(domain => new AssistantWebsiteDomain(channel, domain, now)));

        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>
    /// The publishing gate (M5a plan §3 C, decision B; <see cref="WebsiteChannelRules.PublishFailures"/>):
    /// on any failure <c>422</c> with every reason under its own <c>errors</c> key
    /// (<c>acceptance</c>, <c>allowed-domains</c>, <c>assistant-paused</c>,
    /// <c>knowledge-ownership</c> — one message per knowledge base —, <c>public-base-url</c>) and
    /// nothing written. Publishing an already published channel changes nothing; a paused one
    /// resumes.
    /// </summary>
    internal static async Task<IResult> PublishAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        IOptions<PublicChannelsOptions> options,
        OrganizationTokenUsage tokenUsage,
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

        var channel = await dbContext.AssistantWebsiteChannels
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        var domainCount = await dbContext.AssistantWebsiteDomains
            .CountAsync(domain => domain.AssistantId == assistant.Id, cancellationToken);
        var acceptance = await AcceptanceAsync(dbContext, assistant.Id, cancellationToken);
        var nonOwned = await NonOwnedKnowledgeBasesAsync(dbContext, assistant, cancellationToken);

        var failures = WebsiteChannelRules.PublishFailures(
            acceptance.Status,
            domainCount,
            assistant.Status,
            nonOwned,
            options.Value.ResolvedPublicBaseUrl is not null);
        if (failures.Count > 0)
        {
            return ApiErrors.Refused(PublishRefusedReason, WebsiteChannelRules.PublishRefusedMessage, failures);
        }

        // A channel with an allowed domain always has its row (domains hang off it).
        if (channel!.Publish(callerId, clock.GetUtcNow()))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>
    /// Pauses or resumes a published channel; settings and publication are kept. A channel that is
    /// not published (or never saved) has nothing to pause: <c>422 website-not-published</c>.
    /// </summary>
    internal static async Task<IResult> SetPausedAsync(
        Guid id,
        SetWebsitePausedRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        IOptions<PublicChannelsOptions> options,
        OrganizationTokenUsage tokenUsage,
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

        var channel = await dbContext.AssistantWebsiteChannels
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        if (channel is null || channel.State == WebsiteChannelState.Draft)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity,
                NotPublishedReason,
                "官網嵌入尚未發布，沒有可以暫停或恢復的服務。",
                field: "paused");
        }

        if (channel.SetPaused(request.Paused, clock.GetUtcNow()))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>Back to a draft, keeping every setting and domain; a draft (or a channel never
    /// saved) is a no-op.</summary>
    internal static async Task<IResult> UnpublishAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        IOptions<PublicChannelsOptions> options,
        OrganizationTokenUsage tokenUsage,
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

        var channel = await dbContext.AssistantWebsiteChannels
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        if (channel is not null && channel.Unpublish(clock.GetUtcNow()))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>
    /// The website channel as it stands now, its serving state derived on this read
    /// (<see cref="WebsiteChannelServing.Evaluate"/>). Also <c>GET …/publishing</c>'s
    /// <c>website</c>. The monthly token limit (#195) is read through <paramref name="tokenUsage"/>
    /// (cached 30 seconds), so an organization at its limit shows <c>suspended-quota</c>.
    /// </summary>
    internal static async Task<WebsiteChannelView> ViewAsync(
        AppDbContext dbContext,
        Assistant assistant,
        PublicChannelsOptions options,
        OrganizationTokenUsage tokenUsage,
        CancellationToken cancellationToken)
    {
        var channel = await dbContext.AssistantWebsiteChannels
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        var domains = await dbContext.AssistantWebsiteDomains
            .AsNoTracking()
            .Where(domain => domain.AssistantId == assistant.Id)
            .OrderBy(domain => domain.AddedAt)
            .ThenBy(domain => domain.Domain)
            .Select(domain => new WebsiteDomainView(domain.Domain, domain.AddedAt, domain.LastSeenAt))
            .ToListAsync(cancellationToken);
        var acceptance = await AcceptanceAsync(dbContext, assistant.Id, cancellationToken);
        var nonOwned = await NonOwnedKnowledgeBasesAsync(dbContext, assistant, cancellationToken);

        var usage = await tokenUsage.GetAsync(assistant.OrganizationId, cancellationToken);

        var serving = WebsiteChannelServing.Evaluate(new WebsiteChannelServingInput(
            channel?.State, domains.Count, assistant.Status, acceptance, nonOwned.Count,
            QuotaExceeded: usage.State == TokenUsageState.Exceeded));
        var settings = channel is null
            ? WebsiteChannelRules.Defaults(assistant.Name)
            : new WebsiteChannelSettings(channel.DisplayName, channel.WelcomeMessage, channel.BrandColor, channel.Position, []);
        var (cardStatus, message) = Describe(channel, serving, assistant.Status);

        return new WebsiteChannelView(
            new PublishingChannelView(
                $"channel-website:{assistant.Id}",
                assistant.Id,
                assistant.OwnerAccountId,
                assistant.Name,
                "website",
                cardStatus,
                message,
                channel?.UpdatedAt ?? assistant.UpdatedAt),
            settings.DisplayName,
            settings.WelcomeMessage,
            settings.BrandColor,
            settings.Position,
            [.. domains.Select(domain => domain.Domain)],
            domains,
            channel?.State ?? WebsiteChannelState.Draft,
            serving,
            message,
            acceptance.Status,
            [.. nonOwned.Select(knowledgeBase => new WebsiteKnowledgeBaseView(knowledgeBase.Id, knowledgeBase.Name))],
            options.EmbedCode(assistant.Id, settings.Position),
            channel?.PublishedAt,
            channel?.Revision ?? 0);
    }

    /// <summary>
    /// Whether the assistant's website channel answers visitors right now — the same derivation as
    /// <see cref="ViewAsync"/> (channel state, allowed domains, assistant status, acceptance, knowledge
    /// ownership, monthly token limit), for the visitor API (M5a #196), which asks on every session and
    /// every question. Reads under the current organization: the assistant's.
    /// </summary>
    internal static async Task<(WebsiteServingState State, AssistantWebsiteChannel? Channel)> ServingStateAsync(
        AppDbContext dbContext,
        Assistant assistant,
        OrganizationTokenUsage tokenUsage,
        CancellationToken cancellationToken)
    {
        var channel = await dbContext.AssistantWebsiteChannels
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        var domainCount = await dbContext.AssistantWebsiteDomains
            .CountAsync(domain => domain.AssistantId == assistant.Id, cancellationToken);
        var acceptance = await AcceptanceAsync(dbContext, assistant.Id, cancellationToken);
        var nonOwned = await NonOwnedKnowledgeBasesAsync(dbContext, assistant, cancellationToken);
        var usage = await tokenUsage.GetAsync(assistant.OrganizationId, cancellationToken);

        var serving = WebsiteChannelServing.Evaluate(new WebsiteChannelServingInput(
            channel?.State, domainCount, assistant.Status, acceptance, nonOwned.Count,
            QuotaExceeded: usage.State == TokenUsageState.Exceeded));
        return (serving, channel);
    }

    /// <summary>The channel card's status (M5a plan §3 H's mapping) and the sentence shown with it.</summary>
    private static (string Status, string Message) Describe(
        AssistantWebsiteChannel? channel, WebsiteServingState serving, AssistantStatus assistantStatus) =>
        serving switch
        {
            WebsiteServingState.NotPublished when channel is null =>
                ("not-configured", "尚未設定官網嵌入。"),
            WebsiteServingState.NotPublished when channel.State != WebsiteChannelState.Draft =>
                ("testing", "允許網域清單是空的，目前沒有任何網站可以嵌入；請新增網域。"),
            WebsiteServingState.NotPublished =>
                ("testing", "尚未發布：設定已儲存，驗收通過後即可發布。"),
            WebsiteServingState.Paused when assistantStatus == AssistantStatus.Paused =>
                ("paused", "助理已暫停，官網訪客目前看到「暫停服務」；恢復助理後即可繼續使用。"),
            WebsiteServingState.Paused =>
                ("paused", "已暫停：官網訪客目前看到「暫停服務」，設定會保留。"),
            WebsiteServingState.SuspendedAcceptance =>
                ("needs-attention", "驗收未通過，已自動暫停對外回覆；請到題組頁處理，重跑通過後會自動恢復。"),
            WebsiteServingState.SuspendedKnowledge =>
                ("needs-attention", "連接了不是助理擁有者自己的知識庫，已自動暫停對外回覆；解除連接這些知識庫後會自動恢復。"),
            WebsiteServingState.SuspendedQuota =>
                ("needs-attention", "本月用量已達上限，已暫停對外回覆；下個月或調高上限後會自動恢復。"),
            _ => ("published", "已發布，官網訪客可以使用。"),
        };

    /// <summary><see cref="AssistantAcceptanceRules.Summarize"/> over the assistant's kept runs (the
    /// same inputs as <c>AssistantEndpoints.AcceptanceStatusesAsync</c>).</summary>
    private static async Task<AssistantAcceptanceSummary> AcceptanceAsync(
        AppDbContext dbContext, Guid assistantId, CancellationToken cancellationToken)
    {
        var hasTestCases = await dbContext.AssistantTestCases
            .AnyAsync(testCase => testCase.AssistantId == assistantId, cancellationToken);
        var runs = await dbContext.AssistantTestRuns
            .AsNoTracking()
            .Where(run => run.AssistantId == assistantId)
            .Select(run => new AssistantTestRunSnapshot(run.Status, run.Trigger, run.RerunTrigger, run.QueuedAt, run.FailedCount))
            .ToListAsync(cancellationToken);
        return AssistantAcceptanceRules.Summarize(hasTestCases, runs);
    }

    /// <summary>Connected knowledge bases whose owner is not the assistant's (decision B), oldest
    /// connection first.</summary>
    private static Task<List<WebsiteKnowledgeBaseRef>> NonOwnedKnowledgeBasesAsync(
        AppDbContext dbContext, Assistant assistant, CancellationToken cancellationToken) =>
        dbContext.AssistantKnowledgeBases
            .AsNoTracking()
            .Where(link => link.AssistantId == assistant.Id)
            .Join(
                dbContext.KnowledgeBases.AsNoTracking(),
                link => link.KnowledgeBaseId,
                knowledgeBase => knowledgeBase.Id,
                (link, knowledgeBase) => new { link.ConnectedAt, knowledgeBase.Id, knowledgeBase.Name, knowledgeBase.OwnerAccountId })
            .Where(row => row.OwnerAccountId != assistant.OwnerAccountId)
            .OrderBy(row => row.ConnectedAt)
            .ThenBy(row => row.Id)
            .Select(row => new WebsiteKnowledgeBaseRef(row.Id, row.Name))
            .ToListAsync(cancellationToken);

    private static IResult RevisionConflict() =>
        ApiErrors.WithReason(
            StatusCodes.Status409Conflict,
            RevisionConflictReason,
            "官網嵌入的設定已在其他分頁被更新過，請重新載入後再修改。");

    /// <summary>The assistant if the caller may manage it; <see langword="null"/> alike when it does
    /// not exist, belongs to another organization (query filter) or to someone else.</summary>
    private static Task<Assistant?> FindManageableAsync(
        IQueryable<Assistant> assistants, Guid id, Guid callerId, CancellationToken cancellationToken) =>
        assistants
            .Where(AssistantAccess.ManageableBy(callerId))
            .SingleOrDefaultAsync(assistant => assistant.Id == id, cancellationToken);
}
