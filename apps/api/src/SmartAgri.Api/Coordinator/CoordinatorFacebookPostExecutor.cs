using Microsoft.Extensions.Options;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Observability;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>A shop's usable FB 粉專: its active connection (the page id is <see cref="CoordinatorConnection.ExternalId"/>)
/// and the decrypted page access token. The token is only passed to <see cref="FacebookGraphClient"/>;
/// <see cref="ToString"/> shows none of it.</summary>
public sealed record CoordinatorFacebookAccess(CoordinatorConnection Connection, string PageToken)
{
    public string PageId => Connection.ExternalId;

    public override string ToString() => $"{nameof(CoordinatorFacebookAccess)} {{ {Connection} }}";
}

/// <summary>
/// FB 粉專貼文 carried out (store-coordinator design §3 Q3, Q13, Q18, Q21, §5 Slice 8, #379): the command's announcement
/// posted on the shop's page — with the scenario's image a photo post (<c>POST /{page-id}/photos</c>, <c>url</c> and
/// <c>caption</c>), without one a text post (<c>POST /{page-id}/feed</c>) — and, on a reopen, that post deleted
/// (<c>DELETE /{post-id}</c>). Available exactly while <c>Coordinator:Facebook</c> is configured and the shop has an
/// active FB connection (a page chosen by its owner through an authorization link, #377), so the preview card and the
/// execution agree on the mode.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never twice</b>: Graph has no idempotency key. The post's id is kept on the action row the moment Facebook
/// answers (<see cref="CoordinatorCommandActions.KeepExternalRefAsync"/>), before the try is recorded; a run of a row
/// that has one (the job retried, 〔重試〕) is a success without posting again. What remains: Facebook accepting a post
/// but its answer never arriving (a timeout) — that try fails as <see cref="CoordinatorFailureCodes.FacebookTimedOut"/>,
/// whose reason asks the member to look at the page before pressing 〔重試〕.
/// </para>
/// <para>
/// <b>Undo</b> deletes the post id the applied action kept (<see cref="CoordinatorActionRequest.UndoneExternalRef"/>) —
/// only one of the connected page (<c>{page-id}_…</c>); otherwise (no id, another page connected since) it is a
/// reminder. A post that is gone already (Graph code 100, subcode 33) counts as deleted.
/// </para>
/// <para>
/// <b>Errors</b> (<see cref="FacebookGraphErrors"/>): the token refused (190, 102, authentication subcodes) is
/// <see cref="CoordinatorFailureCodes.TokenRevoked"/> — the run handler then revokes the connection and pushes the
/// owners a re-authorization link (#377) — as is a token that can no longer be decrypted; a missing permission (10,
/// 200–299: App Review not passed, publishing not granted) is a reminder with
/// <see cref="CoordinatorFailureCodes.FacebookPermission"/>; throttling (4, 17, 32, 341, 613, 80001) fails as
/// <see cref="CoordinatorFailureCodes.RateLimited"/>; Graph down (1, 2, 5xx, unreachable) as
/// <see cref="CoordinatorFailureCodes.FacebookUnavailable"/>; an image Facebook cannot use (324, or 100 other than 100/33
/// on a photo post) as <see cref="CoordinatorFailureCodes.FacebookImage"/>; a post that timed out (or answered without an
/// id) as <see cref="CoordinatorFailureCodes.FacebookTimedOut"/>; anything else as
/// <see cref="CoordinatorFailureCodes.Rejected"/> — each for 〔重試〕. Logs carry the action, the page and post ids and what Graph answered (status, code, subcode,
/// fbtrace_id) — never a token, the announcement or Graph's message.
/// </para>
/// </remarks>
public sealed class CoordinatorFacebookPostExecutor : ICoordinatorActionExecutor
{
    private readonly CoordinatorConnections _connections;
    private readonly CoordinatorCommandActions _actions;
    private readonly ISecretProtector _secrets;
    private readonly FacebookGraphClient _graph;
    private readonly CoordinatorFacebookOptions _options;
    private readonly ILogger<CoordinatorFacebookPostExecutor> _logger;

    public CoordinatorFacebookPostExecutor(
        CoordinatorConnections connections,
        CoordinatorCommandActions actions,
        ISecretProtector secrets,
        FacebookGraphClient graph,
        IOptions<CoordinatorFacebookOptions> options,
        ILogger<CoordinatorFacebookPostExecutor> logger)
    {
        _connections = connections;
        _actions = actions;
        _secrets = secrets;
        _graph = graph;
        _options = options.Value;
        _logger = logger;
    }

    public ShopActionKind Kind => ShopActionKind.FacebookPost;

    public async ValueTask<bool> IsAvailableAsync(Guid organizationId, CancellationToken cancellationToken) =>
        _options.IsConfigured && await _connections.IsActiveAsync(organizationId, CoordinatorService.Facebook, cancellationToken);

    public async Task<CoordinatorActionResult> ExecuteAsync(CoordinatorActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Step != CoordinatorActionStep.Apply || !_options.IsConfigured)
        {
            return CoordinatorActionResult.Unavailable;
        }

        if (string.IsNullOrWhiteSpace(request.Announcement))
        {
            _logger.LogError("統籌助理 action {ActionId}: an FB 貼文 without an announcement.", request.ActionId);
            return CoordinatorActionResult.Failed(CoordinatorFailureCodes.Error);
        }

        if ((await _actions.FindAsync(request.OrganizationId, request.ActionId, cancellationToken))?.ExternalRef is { Length: > 0 } posted)
        {
            _logger.LogInformation("統籌助理 action {ActionId}: FB 貼文 already posted as {PostId}; not posted again.", request.ActionId, posted);
            return CoordinatorActionResult.Succeeded(posted);
        }

        var (access, unavailable) = await AccessAsync(request.OrganizationId, cancellationToken);
        if (access is null)
        {
            return unavailable!;
        }

        var photo = request.ImageUrl is { Length: > 0 };
        var result = photo
            ? await _graph.PostPhotoAsync(access.PageId, access.PageToken, request.ImageUrl!, request.Announcement, cancellationToken)
            : await _graph.PostTextAsync(access.PageId, access.PageToken, request.Announcement, cancellationToken);
        if (result is { Succeeded: true, Value: { Length: > 0 and <= CoordinatorCommandAction.ExternalRefMaxLength } postId })
        {
            await _actions.KeepExternalRefAsync(request.OrganizationId, request.ActionId, postId);
            _logger.LogInformation(
                "統籌助理 action {ActionId}: FB 貼文 posted on page {PageId} as {PostId} ({Kind}).",
                request.ActionId, access.PageId, postId, photo ? "photo" : "text");
            return CoordinatorActionResult.Succeeded(postId);
        }

        return Failure(request, access, result, photo ? "photo post" : "text post");
    }

    public async Task<CoordinatorActionResult> UndoAsync(CoordinatorActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Step != CoordinatorActionStep.Undo || !_options.IsConfigured)
        {
            return CoordinatorActionResult.Unavailable;
        }

        if (request.UndoneExternalRef is not { Length: > 0 } postId)
        {
            _logger.LogWarning("統籌助理 action {ActionId}: the FB 貼文 to delete has no post id; a reminder.", request.ActionId);
            return CoordinatorActionResult.Unavailable;
        }

        var (access, unavailable) = await AccessAsync(request.OrganizationId, cancellationToken);
        if (access is null)
        {
            return unavailable!;
        }

        if (!postId.StartsWith(access.PageId + "_", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "統籌助理 action {ActionId}: post {PostId} is not of the connected page {PageId} (another page connected since); a reminder.",
                request.ActionId, postId, access.PageId);
            return CoordinatorActionResult.Unavailable;
        }

        var result = await _graph.DeleteAsync(postId, access.PageToken, cancellationToken);
        if (result.Succeeded || FacebookGraphErrors.IsMissingObject(result.Error))
        {
            _logger.LogInformation(
                "統籌助理 action {ActionId}: FB post {PostId} {Outcome}.", request.ActionId, postId, result.Succeeded ? "deleted" : "was deleted already");
            return CoordinatorActionResult.Succeeded();
        }

        return Failure(request, access, result, "delete");
    }

    /// <summary>The shop's page and token; otherwise why not: no connection (or <c>Coordinator:Facebook</c> unset) is a
    /// plain reminder, a revoked connection or a token that can no longer be decrypted (the key ring was lost or replaced)
    /// is <see cref="CoordinatorFailureCodes.TokenRevoked"/> — the owner authorizes again.</summary>
    private async Task<(CoordinatorFacebookAccess? Access, CoordinatorActionResult? Unavailable)> AccessAsync(
        Guid organizationId, CancellationToken cancellationToken)
    {
        var connection = await _connections.FindAsync(organizationId, CoordinatorService.Facebook, cancellationToken);
        if (connection is null)
        {
            return (null, CoordinatorActionResult.Unavailable);
        }

        if (connection.Status != CoordinatorConnectionStatus.Active)
        {
            return (null, CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.TokenRevoked));
        }

        try
        {
            var token = _secrets.Unprotect(CoordinatorConnection.PurposeOf(CoordinatorService.Facebook), connection.Secret);
            return (new CoordinatorFacebookAccess(connection, token), null);
        }
        catch (SecretUnprotectException exception)
        {
            _logger.LogError(
                "The FB page token of organization {OrganizationId} cannot be decrypted ({Failure}); the owner must authorize again.",
                organizationId, ExceptionSummary.Of(exception));
            return (null, CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.TokenRevoked));
        }
    }

    private CoordinatorActionResult Failure(CoordinatorActionRequest request, CoordinatorFacebookAccess access, FacebookGraphResult result, string call)
    {
        var posting = call != "delete";
        var photo = call == "photo post";
        CoordinatorActionResult answer;
        if (FacebookGraphErrors.IsTokenRefused(result.Error))
        {
            answer = CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.TokenRevoked);
        }
        else if (FacebookGraphErrors.IsPermissionMissing(result.Error))
        {
            answer = CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.FacebookPermission);
        }
        else if (FacebookGraphErrors.IsRateLimited(result))
        {
            answer = CoordinatorActionResult.Failed(CoordinatorFailureCodes.RateLimited);
        }
        else if (posting && (result.Outcome == FacebookGraphOutcome.TimedOut || result.Succeeded || result.StatusCode is >= 200 and < 300))
        {
            // No answer in time, or one without a post id: Facebook may have posted it.
            answer = CoordinatorActionResult.Failed(CoordinatorFailureCodes.FacebookTimedOut);
        }
        else if (FacebookGraphErrors.IsUnavailable(result))
        {
            answer = CoordinatorActionResult.Failed(CoordinatorFailureCodes.FacebookUnavailable);
        }
        else if (photo && result.Error is { Code: 324 } or { Code: 100, Subcode: not 33 })
        {
            answer = CoordinatorActionResult.Failed(CoordinatorFailureCodes.FacebookImage);
        }
        else
        {
            answer = CoordinatorActionResult.Failed(CoordinatorFailureCodes.Rejected);
        }

        _logger.LogWarning(
            "統籌助理 action {ActionId}: FB {Call} on page {PageId} failed: {Result} (fbtrace_id {FbTraceId}) → {Outcome} {FailureCode}.",
            request.ActionId, call, access.PageId, result, result.Error?.FbTraceId, answer.Outcome, answer.FailureCode);
        return answer;
    }
}
