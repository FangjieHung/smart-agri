using System.Collections.Concurrent;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Application.Observability;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>A shop's usable LINE official account: its active connection and the decrypted token. The token is only
/// passed to the LINE client; <see cref="ToString"/> shows none of it.</summary>
public sealed record CoordinatorLineAccess(CoordinatorConnection Connection, string AccessToken)
{
    public override string ToString() => $"{nameof(CoordinatorLineAccess)} {{ {Connection} }}";
}

/// <summary>An estimate, or the LINE call that failed while making it.</summary>
public sealed record CoordinatorLineEstimateResult(LineBroadcastEstimate? Estimate, LineApiResult? Failure);

/// <summary>
/// The last estimate of each shop (#376), for ten minutes: LINE's friend-count insight allows 60 calls an hour, and a
/// member editing a card would otherwise ask again each time. An entry is for one credential (the connection's
/// <see cref="CoordinatorConnection.UpdatedAt"/>): a token set again is asked afresh. A broadcast that was sent drops
/// its shop's entry (what is left of the quota changed).
/// </summary>
public sealed class CoordinatorLineEstimateCache
{
    /// <summary>How long an estimate is used.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly TimeProvider _clock;

    public CoordinatorLineEstimateCache(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>The cached estimate of <paramref name="connection"/>, if still fresh.</summary>
    public LineBroadcastEstimate? Find(CoordinatorConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _entries.TryGetValue(connection.OrganizationId, out var entry)
            && entry.Version == connection.UpdatedAt
            && _clock.GetUtcNow() - entry.At < Lifetime
                ? entry.Estimate
                : null;
    }

    public void Store(CoordinatorConnection connection, LineBroadcastEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(estimate);
        _entries[connection.OrganizationId] = new Entry(connection.UpdatedAt, _clock.GetUtcNow(), estimate);
    }

    public void Forget(Guid organizationId) => _entries.TryRemove(organizationId, out _);

    private sealed record Entry(DateTimeOffset Version, DateTimeOffset At, LineBroadcastEstimate Estimate);
}

/// <summary>
/// Reaching a shop's own LINE official account (store-coordinator design §3 Q10, Q14, §5 Slice 5, #376): the token of
/// its active connection, the estimate before a broadcast (quota, consumption and the friend count of yesterday in
/// UTC+9, or the day before when LINE has not counted yet), and revoking a token LINE refused. Logs carry the
/// organization and what LINE answered — never the token.
/// </summary>
public sealed class CoordinatorLineBroadcasting
{
    private readonly CoordinatorConnections _connections;
    private readonly ISecretProtector _secrets;
    private readonly ILineMessagingClient _line;
    private readonly CoordinatorLineEstimateCache _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger<CoordinatorLineBroadcasting> _logger;

    public CoordinatorLineBroadcasting(
        CoordinatorConnections connections,
        ISecretProtector secrets,
        ILineMessagingClient line,
        CoordinatorLineEstimateCache cache,
        TimeProvider clock,
        ILogger<CoordinatorLineBroadcasting> logger)
    {
        _connections = connections;
        _secrets = secrets;
        _line = line;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    public ILineMessagingClient Line => _line;

    public CoordinatorLineEstimateCache Cache => _cache;

    /// <summary>Whether the shop has an active LINE connection (no external call).</summary>
    public Task<bool> IsConnectedAsync(Guid organizationId, CancellationToken cancellationToken) =>
        _connections.IsActiveAsync(organizationId, CoordinatorService.Line, cancellationToken);

    /// <summary>Whether the shop's LINE connection exists but was revoked (LINE refused its token).</summary>
    public async Task<bool> IsRevokedAsync(Guid organizationId, CancellationToken cancellationToken) =>
        (await _connections.FindAsync(organizationId, CoordinatorService.Line, cancellationToken))?.Status == CoordinatorConnectionStatus.Revoked;

    /// <summary>The shop's token, or <see langword="null"/> when it has no active connection — or its token can no
    /// longer be decrypted (the key ring was lost or replaced), which revokes it: it must be set again.</summary>
    public async Task<CoordinatorLineAccess?> AccessAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (await _connections.ActiveAsync(organizationId, CoordinatorService.Line, cancellationToken) is not { } connection)
        {
            return null;
        }

        try
        {
            return new CoordinatorLineAccess(connection, _secrets.Unprotect(CoordinatorConnection.PurposeOf(CoordinatorService.Line), connection.Secret));
        }
        catch (SecretUnprotectException exception)
        {
            _logger.LogError(
                "The LINE token of organization {OrganizationId} cannot be decrypted ({Failure}); it is revoked until set again.",
                organizationId, ExceptionSummary.Of(exception));
            await _connections.RevokeAsync(connection, cancellationToken);
            return null;
        }
    }

    /// <summary>LINE refused <paramref name="access"/>'s token (<c>401</c>/<c>403</c>): the connection is revoked —
    /// LINE 群發 becomes a reminder until the operator sets a token again.</summary>
    public async Task RevokeAsync(CoordinatorLineAccess access, LineApiResult refusal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(refusal);
        var revoked = await _connections.RevokeAsync(access.Connection, cancellationToken);
        _cache.Forget(access.Connection.OrganizationId);
        _logger.LogWarning(
            "LINE refused the token of organization {OrganizationId} (HTTP {StatusCode}, x-line-request-id {RequestId}){Revoked}.",
            access.Connection.OrganizationId, refusal.StatusCode, refusal.RequestId,
            revoked ? "; the connection is revoked until the operator sets the token again" : string.Empty);
    }

    /// <summary>
    /// The estimate for <paramref name="access"/>: the cached one when <paramref name="fresh"/> is off and it is
    /// fresh; otherwise the quota and consumption asked now, with the friend count from the cache when fresh (else
    /// asked now). A failing quota or consumption call is the result's <see cref="CoordinatorLineEstimateResult.Failure"/>;
    /// a missing friend count only makes the estimate unknown.
    /// </summary>
    public async Task<CoordinatorLineEstimateResult> EstimateAsync(CoordinatorLineAccess access, bool fresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var cached = _cache.Find(access.Connection);
        if (!fresh && cached is not null)
        {
            return new CoordinatorLineEstimateResult(cached, null);
        }

        var quota = await _line.GetMessageQuotaAsync(access.AccessToken, cancellationToken);
        if (!quota.Succeeded || quota.Value is null)
        {
            return new CoordinatorLineEstimateResult(null, quota);
        }

        LineQuotaConsumption? consumption = null;
        if (quota.Value.Limited)
        {
            var used = await _line.GetMessageQuotaConsumptionAsync(access.AccessToken, cancellationToken);
            if (!used.Succeeded || used.Value is null)
            {
                return new CoordinatorLineEstimateResult(null, used);
            }

            consumption = used.Value;
        }

        var followers = cached is { Followers: { } count, Blocks: { } blocks }
            ? new LineFollowersInsight(LineFollowersInsight.Ready, count, null, blocks)
            : await FollowersAsync(access, cancellationToken);
        var estimate = LineBroadcastEstimate.Of(quota.Value, consumption, followers);
        _cache.Store(access.Connection, estimate);
        return new CoordinatorLineEstimateResult(estimate, null);
    }

    private async Task<LineFollowersInsight?> FollowersAsync(CoordinatorLineAccess access, CancellationToken cancellationToken)
    {
        var date = CoordinatorLineBroadcast.FollowersDate(_clock.GetUtcNow());
        for (var day = 0; day < 2; day++)
        {
            var insight = await _line.GetFollowersInsightAsync(access.AccessToken, date.AddDays(-day), cancellationToken);
            if (!insight.Succeeded || insight.Value is null)
            {
                return null;
            }

            if (insight.Value.IsReady)
            {
                return insight.Value;
            }

            if (insight.Value.Status != LineFollowersInsight.Unready)
            {
                return null;
            }
        }

        return null;
    }
}

/// <summary>
/// The LINE 群發 line of the preview card (Q14, #376): 「約 N 則（好友 F－封鎖 B），本月剩 M 則」 and the like
/// (<see cref="CoordinatorLineBroadcast.Note"/>), from the shop's cached estimate. A shop without an active connection
/// has none (its LINE 群發 is a reminder); a token LINE refuses is revoked here, before the card decides the mode.
/// </summary>
public sealed class CoordinatorLineBroadcastNotes : ICoordinatorActionNotes
{
    private readonly CoordinatorLineBroadcasting _broadcasting;

    public CoordinatorLineBroadcastNotes(CoordinatorLineBroadcasting broadcasting)
    {
        _broadcasting = broadcasting;
    }

    public async ValueTask<string?> NoteAsync(Guid organizationId, ShopActionKind kind, CancellationToken cancellationToken)
    {
        if (kind != ShopActionKind.LineBroadcast || await _broadcasting.AccessAsync(organizationId, cancellationToken) is not { } access)
        {
            return null;
        }

        var result = await _broadcasting.EstimateAsync(access, fresh: false, cancellationToken);
        if (result.Estimate is { } estimate)
        {
            return CoordinatorLineBroadcast.Note(estimate);
        }

        if (result.Failure is { Outcome: LineApiOutcome.Unauthorized } refusal)
        {
            await _broadcasting.RevokeAsync(access, refusal, cancellationToken);
            return null;
        }

        return CoordinatorLineBroadcast.QuotaUnknownNote;
    }
}

/// <summary>
/// LINE 群發 carried out (store-coordinator design §3 Q14, Q18, Q21, §5 Slice 5, #376): the command's announcement —
/// a reopen's correction too — and its image, broadcast to the friends of the shop's own LINE official account.
/// Available exactly while the shop has an active LINE connection.
/// </summary>
/// <remarks>
/// <para>
/// Before sending, the quota is asked again: an estimate above what is left fails
/// (<see cref="CoordinatorFailureCodes.QuotaInsufficient"/>) without calling broadcast. The retry key is the action's
/// id, so a job run again or 〔重試〕 within LINE's 24 hours never sends twice: LINE answers <c>409</c>, recorded as a
/// success. LINE refusing the token revokes the connection and the action becomes a reminder
/// (<see cref="CoordinatorFailureCodes.TokenRevoked"/>); the monthly limit, a rate limit and LINE being unavailable
/// fail with their reason (<see cref="CoordinatorLineBroadcast.FailureCodeOf"/>), for 〔重試〕.
/// </para>
/// <para>
/// A sent broadcast cannot be taken back (Q21): <see cref="UndoAsync"/> is unavailable — the reopen card offers the
/// correction broadcast instead, which is an apply of this executor.
/// </para>
/// </remarks>
public sealed class CoordinatorLineBroadcastExecutor : ICoordinatorActionExecutor
{
    private readonly CoordinatorLineBroadcasting _broadcasting;
    private readonly ILogger<CoordinatorLineBroadcastExecutor> _logger;

    public CoordinatorLineBroadcastExecutor(CoordinatorLineBroadcasting broadcasting, ILogger<CoordinatorLineBroadcastExecutor> logger)
    {
        _broadcasting = broadcasting;
        _logger = logger;
    }

    public ShopActionKind Kind => ShopActionKind.LineBroadcast;

    public async ValueTask<bool> IsAvailableAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await _broadcasting.IsConnectedAsync(organizationId, cancellationToken);

    public async Task<CoordinatorActionResult> ExecuteAsync(CoordinatorActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Step != CoordinatorActionStep.Apply)
        {
            return CoordinatorActionResult.Unavailable;
        }

        if (string.IsNullOrWhiteSpace(request.Announcement))
        {
            _logger.LogError("統籌助理 action {ActionId}: a LINE 群發 without an announcement.", request.ActionId);
            return CoordinatorActionResult.Failed(CoordinatorFailureCodes.Error);
        }

        if (await _broadcasting.AccessAsync(request.OrganizationId, cancellationToken) is not { } access)
        {
            // Revoked since the command was confirmed (or cleared by the operator): a reminder.
            return await _broadcasting.IsRevokedAsync(request.OrganizationId, cancellationToken)
                ? CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.TokenRevoked)
                : CoordinatorActionResult.Unavailable;
        }

        var estimated = await _broadcasting.EstimateAsync(access, fresh: true, cancellationToken);
        if (estimated.Failure is { } failure)
        {
            return await FailedAsync(request, access, failure, "quota", cancellationToken);
        }

        var estimate = estimated.Estimate!;
        if (estimate.Insufficient)
        {
            _logger.LogWarning(
                "統籌助理 action {ActionId}: LINE 群發 not sent, about {Recipients} recipients and {Remaining} left this month.",
                request.ActionId, estimate.Recipients?.ToString() ?? "unknown", estimate.Remaining);
            return CoordinatorActionResult.Failed(CoordinatorFailureCodes.QuotaInsufficient);
        }

        var messages = CoordinatorLineBroadcast.Messages(request.Announcement, request.ImageUrl);
        var sent = await _broadcasting.Line.BroadcastAsync(access.AccessToken, messages, request.ActionId, cancellationToken);
        if (sent.Succeeded || sent.IsRetryKeyAccepted)
        {
            _broadcasting.Cache.Forget(request.OrganizationId);
            _logger.LogInformation(
                "統籌助理 action {ActionId}: LINE 群發 {Outcome} (x-line-request-id {RequestId}).",
                request.ActionId, sent.Succeeded ? "sent" : "already sent with this retry key", sent.RequestId);
            return CoordinatorActionResult.Succeeded();
        }

        return await FailedAsync(request, access, sent, "broadcast", cancellationToken);
    }

    /// <summary>A sent broadcast cannot be taken back (Q21).</summary>
    public Task<CoordinatorActionResult> UndoAsync(CoordinatorActionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(CoordinatorActionResult.Unavailable);

    private async Task<CoordinatorActionResult> FailedAsync(
        CoordinatorActionRequest request, CoordinatorLineAccess access, LineApiResult failure, string call, CancellationToken cancellationToken)
    {
        if (failure.Outcome == LineApiOutcome.Unauthorized)
        {
            await _broadcasting.RevokeAsync(access, failure, cancellationToken);
            return CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.TokenRevoked);
        }

        var code = CoordinatorLineBroadcast.FailureCodeOf(failure);
        _logger.LogWarning(
            "統籌助理 action {ActionId}: LINE {Call} failed: {Outcome} (HTTP {StatusCode}, x-line-request-id {RequestId}) → {FailureCode}.",
            request.ActionId, call, failure.Outcome, failure.StatusCode, failure.RequestId, code);
        return CoordinatorActionResult.Failed(code);
    }
}
