using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Secrets;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Line;
using SmartAgri.Application.Organizations;
using SmartAgri.Application.Secrets;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

/// <summary>One connection check of the LINE channel's 「測試連線」 (M5b plan §3 B): always all three, in
/// order; <c>pending</c> until a test runs (from M5b #230 on) and again after the connection settings
/// change.</summary>
public sealed record LineConnectionCheckView(
    LineConnectionCheckKind Check,
    string Label,
    LineConnectionCheckState State,
    string Message);

/// <summary>
/// The LINE channel (M5b plan §3 A, §3 I). Replaces the frontend mock's <c>LineSetupView</c>, with
/// these deliberate differences:
/// <list type="bullet">
/// <item>the channel secret and the access token are write-only: only <see cref="ChannelSecret"/> /
/// <see cref="AccessToken"/> (configured, last four, when set) — never the plaintext nor the
/// ciphertext;</item>
/// <item><see cref="Checks"/> are the three connection checks (decision B) instead of a check per field
/// and a test message; per-field format errors are this endpoint's <c>422</c>;</item>
/// <item><see cref="State"/> (the owner's choice) and <see cref="ServingState"/> /
/// <see cref="ServingMessage"/> (derived on this read, as on the website channel);</item>
/// <item><see cref="WebhookUrl"/> is real (<c>PublicChannels:PublicBaseUrl</c>), or
/// <see langword="null"/> while the server has no public address;</item>
/// <item><see cref="Revision"/> is <c>0</c> until the settings are first saved (the values shown are
/// then empty, with decision D's default welcome message).</item>
/// </list>
/// </summary>
/// <param name="Channel">Its card: <c>not-configured</c> before the first save, <c>testing</c> while
/// not enabled, <c>published</c> while serving, <c>needs-attention</c> while suspended or after a failed
/// connection test, <c>paused</c>.</param>
/// <param name="ConnectionCheckedAt">When the connection was last tested; <see langword="null"/> while
/// every check is pending.</param>
/// <param name="PushFallbackCount">Answers sent by push instead of reply this month (「本月補送次數」, in
/// <c>Statistics:TimeZone</c>; counted from M5b Slice 4 on).</param>
public sealed record LineChannelView(
    PublishingChannelView Channel,
    string OfficialAccountId,
    string ChannelId,
    string WelcomeMessage,
    SecretStatusView ChannelSecret,
    SecretStatusView AccessToken,
    string? WebhookUrl,
    IReadOnlyList<LineConnectionCheckView> Checks,
    DateTimeOffset? ConnectionCheckedAt,
    LineChannelState State,
    ChannelServingState ServingState,
    string ServingMessage,
    AssistantAcceptanceStatus AcceptanceStatus,
    IReadOnlyList<WebsiteKnowledgeBaseView> NonOwnedKnowledgeBases,
    DateTimeOffset? PublishedAt,
    int PushFallbackCount,
    int Revision);

/// <summary>
/// <c>PUT /api/v1/assistants/{id}/publishing/line</c>: the settings form and the
/// <see cref="Revision"/> it was read at (<c>0</c> before the first save). The official account id,
/// the channel id and the welcome message replace the stored ones; <see cref="ChannelSecret"/> and
/// <see cref="AccessToken"/> are write-only — <see langword="null"/> or empty keeps the stored value
/// (required only on the first save), a value replaces it.
/// </summary>
public sealed record UpdateLineChannelRequest(
    string? OfficialAccountId,
    string? ChannelId,
    string? WelcomeMessage,
    int Revision,
    string? ChannelSecret = null,
    string? AccessToken = null)
{
    /// <summary>Never shows the credentials (the generated <c>ToString</c> would).</summary>
    public override string ToString() =>
        $"{nameof(UpdateLineChannelRequest)} {{ OfficialAccountId = {OfficialAccountId}, ChannelId = {ChannelId}, Revision = {Revision} }}";
}

/// <summary><c>PUT /api/v1/assistants/{id}/publishing/line/paused</c> request.</summary>
public sealed record SetLinePausedRequest(bool Paused);

/// <summary>
/// The LINE channel's settings, connection test, enabling, pause and unpublish (M5b plan §5 Slices 1
/// and 2, issues #229 and #230): S+OWN+MP like the website channel — <c>manage-publishing</c> and the
/// caller owning the assistant; an id that does not exist, belongs to another organization or to
/// someone else gets the same <c>403 publishing</c>, byte for byte.
/// </summary>
/// <remarks>
/// Rules live in <see cref="LineChannelRules"/>, <see cref="AssistantLineChannel"/> (the reset of the
/// connection test on a credential change), <see cref="LineChannelServing"/> and
/// <see cref="LineConnectionTester"/> (the three checks, against <see cref="ILineMessagingClient"/>),
/// unit tested there; this class only loads, calls them, protects and unprotects the credentials, writes and maps.
/// </remarks>
public static class AssistantLineChannelEndpoints
{
    public const string RevisionConflictReason = "line-revision-conflict";

    public const string NotPublishedReason = "line-not-published";

    /// <summary><c>:test</c> refused for this server's own preconditions (never for LINE failing).</summary>
    public const string TestRefusedReason = "line-test-refused";

    /// <summary><c>:test</c>'s <c>errors</c> key for "no settings saved yet".</summary>
    public const string TestSettingsField = "settings";

    public const string PublishRefusedReason = "line-publish-refused";

    public static IEndpointRouteBuilder MapAssistantLineChannelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var assistants = endpoints.MapGroup("/api/v1/assistants")
            .RequireAuthorization();

        assistants.MapGet("/{id:guid}/publishing/line", GetAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<LineChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        assistants.MapPut("/{id:guid}/publishing/line", UpdateAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<LineChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapPost("/{id:guid}/publishing/line:test", TestConnectionAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<LineChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapPost("/{id:guid}/publishing/line:publish", PublishAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<LineChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapPut("/{id:guid}/publishing/line/paused", SetPausedAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<LineChannelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistants.MapPost("/{id:guid}/publishing/line:unpublish", UnpublishAsync)
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<LineChannelView>(StatusCodes.Status200OK)
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

        var assistant = await AssistantWebsiteChannelEndpoints.FindManageableAsync(
            dbContext.Assistants.AsNoTracking(), id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Publishing);
        }

        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>
    /// Order of checks: owner (else <c>403</c>), <see cref="LineChannelRules.ForUpdate"/> (else
    /// <c>422</c> naming every broken field), then the revision (else <c>409</c>) — nothing is written
    /// on any refusal. The first save creates the channel as a draft (revision 0 → 1) and needs both
    /// credentials. A new credential is protected here and never kept in plaintext; changing the
    /// official account id, the channel id or a credential resets the connection test and sends an
    /// enabled channel back to draft (<see cref="AssistantLineChannel.TryApplySettings"/>).
    /// </summary>
    internal static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateLineChannelRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        IOptions<PublicChannelsOptions> options,
        OrganizationTokenUsage tokenUsage,
        ISecretProtector secretProtector,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await AssistantWebsiteChannelEndpoints.FindManageableAsync(
            dbContext.Assistants, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Publishing);
        }

        var channel = await dbContext.AssistantLineChannels
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);

        var validated = LineChannelRules.ForUpdate(
            request.OfficialAccountId,
            request.ChannelId,
            request.ChannelSecret,
            request.AccessToken,
            request.WelcomeMessage,
            credentialsStored: channel is not null);
        if (!validated.IsValid)
        {
            return ApiErrors.ValidationFailed(validated.Failures);
        }

        var settings = validated.Value;
        var now = clock.GetUtcNow();
        if (channel is null)
        {
            if (request.Revision != 0)
            {
                return RevisionConflict();
            }

            channel = new AssistantLineChannel(
                assistant,
                settings.OfficialAccountId,
                settings.ChannelId,
                secretProtector.Protect(AssistantLineChannel.ChannelSecretPurpose, settings.ChannelSecret!, now),
                secretProtector.Protect(AssistantLineChannel.AccessTokenPurpose, settings.AccessToken!, now),
                settings.WelcomeMessage,
                now);
            dbContext.AssistantLineChannels.Add(channel);
        }
        else
        {
            if (request.Revision != channel.Revision)
            {
                return RevisionConflict();
            }

            var newSecret = settings.ChannelSecret is { } secret
                ? secretProtector.Protect(AssistantLineChannel.ChannelSecretPurpose, secret, now)
                : null;
            var newToken = settings.AccessToken is { } token
                ? secretProtector.Protect(AssistantLineChannel.AccessTokenPurpose, token, now)
                : null;
            if (!channel.TryApplySettings(
                    settings.OfficialAccountId, settings.ChannelId, newSecret, newToken, settings.WelcomeMessage, request.Revision, now))
            {
                return RevisionConflict();
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>
    /// 「測試連線」 (M5b plan §3 B; <see cref="LineConnectionTester"/>): with the stored access token,
    /// (1) <c>GET /v2/bot/info</c> — the token is accepted and belongs to the official account id
    /// entered; (2) <c>PUT /v2/bot/channel/webhook/endpoint</c> — LINE's webhook URL becomes
    /// <see cref="PublicChannelsOptions.LineWebhookUrl"/> (decision C); (3)
    /// <c>POST /v2/bot/channel/webhook/test</c> — LINE delivers a signed test event there. A failed
    /// check skips the ones after it. LINE failing is never an HTTP error here (mapping §3.4): the
    /// answer is <c>200</c> with the channel view, whose <c>checks</c> say what passed and why not;
    /// the results (and, when the token check passed, the bot's user id) are stored.
    /// </summary>
    /// <remarks>
    /// Only this server's own preconditions are refused, with <c>422 line-test-refused</c> and nothing
    /// called or written: no saved settings (<c>settings</c>), no
    /// <c>PublicChannels:PublicBaseUrl</c> (<c>public-base-url</c>). A token that no longer decrypts
    /// fails the token check without calling LINE. If the settings are saved again while LINE is being
    /// called, the results describe settings that no longer exist: <c>409 line-revision-conflict</c>,
    /// nothing stored.
    /// </remarks>
    internal static async Task<IResult> TestConnectionAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        IOptions<PublicChannelsOptions> options,
        OrganizationTokenUsage tokenUsage,
        ISecretProtector secretProtector,
        ILineMessagingClient lineClient,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await AssistantWebsiteChannelEndpoints.FindManageableAsync(
            dbContext.Assistants, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Publishing);
        }

        var channel = await dbContext.AssistantLineChannels
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        var webhookUrl = options.Value.LineWebhookUrl(assistant.Id);
        var refusals = new List<ValidationFailure>();
        if (channel is null)
        {
            refusals.Add(new ValidationFailure(TestSettingsField, "請先填寫並儲存 LINE 官方帳號的連接資訊，再測試連線。"));
        }

        if (webhookUrl is null)
        {
            refusals.Add(new ValidationFailure(WebsiteChannelRules.PublicBaseUrlField, LineChannelRules.PublicBaseUrlMissingMessage));
        }

        if (refusals.Count > 0)
        {
            return ApiErrors.Refused(TestRefusedReason, "目前還不能測試連線，請先處理下列項目。", refusals);
        }

        var testedRevision = channel!.Revision;
        string? accessToken = null;
        try
        {
            accessToken = secretProtector.Unprotect(AssistantLineChannel.AccessTokenPurpose, channel.AccessToken);
        }
        catch (SecretUnprotectException)
        {
        }

        var result = accessToken is null
            ? LineConnectionTester.TokenUnreadable()
            : await LineConnectionTester.RunAsync(lineClient, accessToken, channel.OfficialAccountId, webhookUrl!, cancellationToken);

        // LINE took a while: a settings save in the meantime (another tab) makes these results stale.
        var currentRevision = await dbContext.AssistantLineChannels
            .AsNoTracking()
            .Where(candidate => candidate.AssistantId == assistant.Id)
            .Select(candidate => (int?)candidate.Revision)
            .SingleOrDefaultAsync(cancellationToken);
        if (currentRevision != testedRevision)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status409Conflict,
                RevisionConflictReason,
                "測試連線期間 LINE 頻道的設定已在其他分頁被更新過，請重新載入後再測試一次。");
        }

        channel.RecordConnectionChecks(result.Checks, result.BotUserId, clock.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>
    /// 「啟用」: the publishing gate (<see cref="LineChannelRules.PublishFailures"/>) — every connection
    /// check passed (<c>connection</c>), acceptance <c>passed</c> now (<c>acceptance</c>), the
    /// assistant not paused (<c>assistant-paused</c>), every connected knowledge base the owner's own
    /// (<c>knowledge-ownership</c>, one message per knowledge base), the server's public URL known
    /// (<c>public-base-url</c>). On any failure <c>422 line-publish-refused</c> with every reason under
    /// its own <c>errors</c> key, nothing written. Enabling an enabled channel changes nothing; a
    /// paused one resumes.
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

        var assistant = await AssistantWebsiteChannelEndpoints.FindManageableAsync(
            dbContext.Assistants, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Publishing);
        }

        var channel = await dbContext.AssistantLineChannels
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        var acceptance = await AssistantWebsiteChannelEndpoints.AcceptanceAsync(dbContext, assistant.Id, cancellationToken);
        var nonOwned = await AssistantWebsiteChannelEndpoints.NonOwnedKnowledgeBasesAsync(dbContext, assistant, cancellationToken);

        var failures = LineChannelRules.PublishFailures(
            channel?.ConnectionChecksPassed ?? false,
            acceptance.Status,
            assistant.Status,
            nonOwned,
            options.Value.ResolvedPublicBaseUrl is not null);
        if (failures.Count > 0)
        {
            return ApiErrors.Refused(PublishRefusedReason, LineChannelRules.PublishRefusedMessage, failures);
        }

        // Every connection check passed, so the channel has its row.
        if (channel!.Publish(callerId, clock.GetUtcNow()))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>
    /// Pauses or resumes an enabled channel; settings, credentials and publication are kept. A channel
    /// that is not enabled (or never saved) has nothing to pause: <c>422 line-not-published</c>.
    /// </summary>
    internal static async Task<IResult> SetPausedAsync(
        Guid id,
        SetLinePausedRequest request,
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

        var assistant = await AssistantWebsiteChannelEndpoints.FindManageableAsync(
            dbContext.Assistants, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Publishing);
        }

        var channel = await dbContext.AssistantLineChannels
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        if (channel is null || channel.State == LineChannelState.Draft)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity,
                NotPublishedReason,
                "LINE 頻道尚未啟用，沒有可以暫停或恢復的服務。",
                field: "paused");
        }

        if (channel.SetPaused(request.Paused, clock.GetUtcNow()))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>Back to a draft, keeping every setting, credential and connection check result; a
    /// draft (or a channel never saved) is a no-op.</summary>
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

        var assistant = await AssistantWebsiteChannelEndpoints.FindManageableAsync(
            dbContext.Assistants, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.Publishing);
        }

        var channel = await dbContext.AssistantLineChannels
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        if (channel is not null && channel.Unpublish(clock.GetUtcNow()))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(await ViewAsync(dbContext, assistant, options.Value, tokenUsage, cancellationToken));
    }

    /// <summary>
    /// Whether the assistant's LINE channel answers LINE users right now — the same derivation as
    /// <see cref="ViewAsync"/> — for the LINE webhook processor (M5b #231), which asks on every
    /// delivery. Reads under the current organization: the assistant's.
    /// </summary>
    internal static async Task<(ChannelServingState State, AssistantLineChannel? Channel)> ServingStateAsync(
        AppDbContext dbContext,
        Assistant assistant,
        OrganizationTokenUsage tokenUsage,
        CancellationToken cancellationToken)
    {
        var channel = await dbContext.AssistantLineChannels
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        var acceptance = await AssistantWebsiteChannelEndpoints.AcceptanceAsync(dbContext, assistant.Id, cancellationToken);
        var nonOwned = await AssistantWebsiteChannelEndpoints.NonOwnedKnowledgeBasesAsync(dbContext, assistant, cancellationToken);
        var usage = await tokenUsage.GetAsync(assistant.OrganizationId, cancellationToken);

        var serving = LineChannelServing.Evaluate(new LineChannelServingInput(
            channel?.State,
            channel?.ConnectionChecksPassed ?? false,
            assistant.Status,
            acceptance,
            nonOwned.Count,
            QuotaExceeded: usage.State == TokenUsageState.Exceeded));
        return (serving, channel);
    }

    /// <summary>
    /// The LINE channel as it stands now, its serving state derived on this read
    /// (<see cref="LineChannelServing.Evaluate"/>: the website channel's conditions, with "every
    /// connection check passed" in place of "has an allowed domain"). Also <c>GET …/publishing</c>'s
    /// <c>line</c>.
    /// </summary>
    internal static async Task<LineChannelView> ViewAsync(
        AppDbContext dbContext,
        Assistant assistant,
        PublicChannelsOptions options,
        OrganizationTokenUsage tokenUsage,
        CancellationToken cancellationToken)
    {
        var channel = await dbContext.AssistantLineChannels
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id, cancellationToken);
        var acceptance = await AssistantWebsiteChannelEndpoints.AcceptanceAsync(dbContext, assistant.Id, cancellationToken);
        var nonOwned = await AssistantWebsiteChannelEndpoints.NonOwnedKnowledgeBasesAsync(dbContext, assistant, cancellationToken);
        var usage = await tokenUsage.GetAsync(assistant.OrganizationId, cancellationToken);

        var serving = LineChannelServing.Evaluate(new LineChannelServingInput(
            channel?.State,
            channel?.ConnectionChecksPassed ?? false,
            assistant.Status,
            acceptance,
            nonOwned.Count,
            QuotaExceeded: usage.State == TokenUsageState.Exceeded));
        var (cardStatus, message) = Describe(channel, serving, assistant.Status);

        return new LineChannelView(
            new PublishingChannelView(
                $"channel-line:{assistant.Id}",
                assistant.Id,
                assistant.OwnerAccountId,
                assistant.Name,
                "line",
                cardStatus,
                message,
                channel?.UpdatedAt ?? assistant.UpdatedAt),
            channel?.OfficialAccountId ?? string.Empty,
            channel?.ChannelId ?? string.Empty,
            channel?.WelcomeMessage ?? AssistantLineChannel.DefaultWelcomeMessage,
            SecretStatusView.From(channel?.ChannelSecret),
            SecretStatusView.From(channel?.AccessToken),
            options.LineWebhookUrl(assistant.Id),
            Checks(channel?.ConnectionChecks ?? []),
            channel?.ConnectionCheckedAt,
            channel?.State ?? LineChannelState.Draft,
            serving,
            message,
            acceptance.Status,
            [.. nonOwned.Select(knowledgeBase => new WebsiteKnowledgeBaseView(knowledgeBase.Id, knowledgeBase.Name))],
            channel?.PublishedAt,
            channel?.PushFallbackCountIn(usage.Month) ?? 0,
            channel?.Revision ?? 0);
    }

    /// <summary>All three checks in order; a check without a stored result is <c>pending</c>.</summary>
    private static List<LineConnectionCheckView> Checks(IReadOnlyList<LineConnectionCheck> stored) =>
    [
        .. LineConnectionCheck.All.Select(kind =>
            stored.FirstOrDefault(check => check.Check == kind) is { } result
                ? new LineConnectionCheckView(kind, LabelOf(kind), result.State, result.Message)
                : new LineConnectionCheckView(kind, LabelOf(kind), LineConnectionCheckState.Pending, "尚未測試。")),
    ];

    private static string LabelOf(LineConnectionCheckKind kind) =>
        kind switch
        {
            LineConnectionCheckKind.AccessToken => "Channel access token 與官方帳號",
            LineConnectionCheckKind.WebhookEndpoint => "設定 Webhook 網址",
            _ => "Webhook 連線測試",
        };

    /// <summary>The channel card's status (the website channel's mapping, M5a plan §3 H, plus the
    /// connection test) and the sentence shown with it.</summary>
    private static (string Status, string Message) Describe(
        AssistantLineChannel? channel, ChannelServingState serving, AssistantStatus assistantStatus) =>
        serving switch
        {
            ChannelServingState.NotPublished when channel is null =>
                ("not-configured", "尚未填寫 LINE 官方帳號連接資訊。"),
            ChannelServingState.NotPublished when channel.State != LineChannelState.Draft =>
                ("needs-attention", "連線測試未通過，LINE 使用者目前收不到回覆；請依檢查結果修正後重新測試連線。"),
            ChannelServingState.NotPublished when channel.ConnectionChecks.Count == 0 =>
                ("testing", "連接資訊已儲存，請測試連線；三項檢查都通過、驗收通過後即可啟用。"),
            ChannelServingState.NotPublished when !channel.ConnectionChecksPassed =>
                ("needs-attention", "連線測試未通過，請依檢查結果修正後重新測試連線。"),
            ChannelServingState.NotPublished =>
                ("testing", "連線測試已通過，驗收通過後即可啟用。"),
            ChannelServingState.Paused when assistantStatus == AssistantStatus.Paused =>
                ("paused", "助理已暫停，LINE 使用者目前會收到「暫停服務」；恢復助理後即可繼續使用。"),
            ChannelServingState.Paused =>
                ("paused", "已暫停：LINE 使用者目前會收到「暫停服務」，設定會保留。"),
            ChannelServingState.SuspendedAcceptance =>
                ("needs-attention", "驗收未通過，已自動暫停 LINE 回覆；請到題組頁處理，重跑通過後會自動恢復。"),
            ChannelServingState.SuspendedKnowledge =>
                ("needs-attention", "連接了不是助理擁有者自己的知識庫，已自動暫停 LINE 回覆；解除連接這些知識庫後會自動恢復。"),
            ChannelServingState.SuspendedQuota =>
                ("needs-attention", "本月用量已達上限，已暫停 LINE 回覆；下個月或調高上限後會自動恢復。"),
            _ => ("published", $"{channel!.OfficialAccountId} 已啟用，可在 LINE 對話中使用。"),
        };

    private static IResult RevisionConflict() =>
        ApiErrors.WithReason(
            StatusCodes.Status409Conflict,
            RevisionConflictReason,
            "LINE 頻道的設定已在其他分頁被更新過，請重新載入後再修改。");
}
