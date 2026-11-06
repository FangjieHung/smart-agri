using System.Text.Json;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Observability;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// Google 特殊營業時間 carried out (store-coordinator design §3 Q3, Q18, Q20–Q22, §5 Slice 7, #378) on the shop's
/// authorized location: available exactly while the platform's Google OAuth client is configured and the shop has an
/// active Google connection (a location chosen) — so the preview card and the execution agree
/// (<see cref="ExecutorActionModes"/>).
/// </summary>
/// <remarks>
/// <para>
/// Each run gets a fresh access token from the stored refresh token (never stored itself), reads the location's
/// <c>regularHours</c> and <c>specialHours</c>, replaces the command date's periods by the ones the shop's own weekly
/// hours give (<see cref="CoordinatorGoogleSpecialHours.PeriodsFor"/>, Q22) and writes the list back
/// (<c>updateMask=specialHours</c>): every other date is written back exactly as read. Running it again writes the
/// same, so a repeated job or 〔重試〕 is harmless. The result keeps the date, the location and the date's periods it
/// replaced — what the owner had set for that day (<see cref="CoordinatorGoogleSpecialHours.ExternalRef"/>; left out, and
/// logged, when they do not fit, and not kept when they were ours already); a reopen's undo (Q21) puts those back on that
/// location, or removes the day's when there were none — already as it should be is a success without a write.
/// </para>
/// <para>
/// A location without regular hours becomes a reminder (<see cref="CoordinatorFailureCodes.GoogleNoRegularHours"/>:
/// Google sets special hours only on top of regular ones; also when Google answers
/// <c>SPECIAL_HOURS_SET_WITHOUT_REGULAR_HOURS</c>). The platform's project without Basic API Access (quota 0, API not
/// enabled) makes it a reminder (<see cref="CoordinatorFailureCodes.GoogleAccessPending"/>). Google refusing the
/// authorization — <c>invalid_grant</c> on refresh, a <c>401</c>, a <c>403</c> on the location, or a refresh token that can
/// no longer be decrypted — makes it a reminder with <see cref="CoordinatorFailureCodes.TokenRevoked"/>, for which the
/// job asks the owners to authorize again (<see cref="ICoordinatorReauthorization"/>). A <c>429</c> fails as
/// <see cref="CoordinatorFailureCodes.RateLimited"/>; a server error, a timeout or no connection as
/// <see cref="CoordinatorFailureCodes.GoogleUnavailable"/>; anything else Google refuses as
/// <see cref="CoordinatorFailureCodes.Rejected"/> — each for 〔重試〕. Logs carry the action, the outcome and Google's
/// status and reason codes — never a token or what the location holds.
/// </para>
/// </remarks>
public sealed class CoordinatorGoogleSpecialHoursExecutor : ICoordinatorActionExecutor
{
    private readonly CoordinatorConnections _connections;
    private readonly ISecretProtector _secrets;
    private readonly CoordinatorGoogleClient _google;
    private readonly ILogger<CoordinatorGoogleSpecialHoursExecutor> _logger;

    public CoordinatorGoogleSpecialHoursExecutor(
        CoordinatorConnections connections,
        ISecretProtector secrets,
        CoordinatorGoogleClient google,
        ILogger<CoordinatorGoogleSpecialHoursExecutor> logger)
    {
        _connections = connections;
        _secrets = secrets;
        _google = google;
        _logger = logger;
    }

    public ShopActionKind Kind => ShopActionKind.GoogleSpecialHours;

    public async ValueTask<bool> IsAvailableAsync(Guid organizationId, CancellationToken cancellationToken) =>
        _google.IsConfigured && await _connections.IsActiveAsync(organizationId, CoordinatorService.Google, cancellationToken);

    public async Task<CoordinatorActionResult> ExecuteAsync(CoordinatorActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Step != CoordinatorActionStep.Apply)
        {
            return CoordinatorActionResult.Unavailable;
        }

        IReadOnlyList<GoogleSpecialHourPeriod> periods;
        try
        {
            periods = CoordinatorGoogleSpecialHours.PeriodsFor(request.Settings, request.Scenario, request.Date, request.Time);
        }
        catch (ArgumentException exception)
        {
            _logger.LogError("統籌助理 action {ActionId}: no special hours for it: {Failure}", request.ActionId, ExceptionSummary.Of(exception));
            return CoordinatorActionResult.Failed(CoordinatorFailureCodes.Error);
        }

        var access = await AccessAsync(request, cancellationToken);
        if (access.Refusal is { } refusal)
        {
            return refusal;
        }

        var location = access.Connection!.ExternalId;
        var read = await _google.GetHoursAsync(access.AccessToken!, location, cancellationToken);
        if (!read.Succeeded)
        {
            return ResultOf(request, "locations.get", read.Outcome, read.ErrorReason);
        }

        using var document = read.Value!;
        var root = document.RootElement;
        if (!CoordinatorGoogleSpecialHours.HasRegularHours(Property(root, "regularHours")))
        {
            _logger.LogWarning("統籌助理 action {ActionId}: the Google location has no regular hours; special hours cannot be set.", request.ActionId);
            return CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.GoogleNoRegularHours);
        }

        var patch = CoordinatorGoogleSpecialHours.Replace(Property(root, "specialHours"), request.Date, periods);
        var written = await _google.PatchSpecialHoursAsync(access.AccessToken!, location, patch.Body, cancellationToken);
        if (!written.Succeeded)
        {
            return ResultOf(request, "locations.patch", written.Outcome, written.ErrorReason);
        }

        written.Value!.Dispose();

        // What the owner had on that day, for the undo — unless it was ours already (this action run again).
        IReadOnlyList<string> previous = CoordinatorGoogleSpecialHours.SameAs(patch.Replaced, periods) ? [] : patch.Replaced;
        var reference = CoordinatorGoogleSpecialHours.ExternalRef(request.Date, location, previous, out var keptPrevious);
        if (!keptPrevious)
        {
            _logger.LogWarning(
                "統籌助理 action {ActionId}: the {Count} special hour period(s) Google had on {Date} are too long to keep; a reopen will remove the day instead of restoring them.",
                request.ActionId, previous.Count, request.Date);
        }

        _logger.LogInformation(
            "統籌助理 action {ActionId}: Google special hours of {Date} set ({Periods} period(s), {Removed} replaced, {Kept} other kept).",
            request.ActionId, request.Date, periods.Count, patch.Removed, patch.Kept);
        return CoordinatorActionResult.Succeeded(reference);
    }

    public async Task<CoordinatorActionResult> UndoAsync(CoordinatorActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Step != CoordinatorActionStep.Undo)
        {
            return CoordinatorActionResult.Unavailable;
        }

        var access = await AccessAsync(request, cancellationToken);
        if (access.Refusal is { } refusal)
        {
            return refusal;
        }

        // The location written then (the owner may have chosen another since) and what it had on that day; the command's
        // date otherwise (the day is removed).
        var applied = CoordinatorGoogleSpecialHours.TryReadExternalRef(request.UndoneExternalRef, out var reference)
            ? reference
            : new GoogleAppliedRef(request.Date, access.Connection!.ExternalId, []);
        var read = await _google.GetHoursAsync(access.AccessToken!, applied.Location, cancellationToken);
        if (!read.Succeeded)
        {
            return ResultOf(request, "locations.get", read.Outcome, read.ErrorReason);
        }

        using var document = read.Value!;
        var specialHours = Property(document.RootElement, "specialHours");
        if (CoordinatorGoogleSpecialHours.SameAs(CoordinatorGoogleSpecialHours.PeriodsOn(specialHours, applied.Date), applied.Previous))
        {
            _logger.LogInformation(
                "統籌助理 action {ActionId}: Google's special hours of {Date} are already as before; nothing to take back.", request.ActionId, applied.Date);
            return CoordinatorActionResult.Succeeded();
        }

        var patch = CoordinatorGoogleSpecialHours.ReplaceRaw(specialHours, applied.Date, applied.Previous);
        var written = await _google.PatchSpecialHoursAsync(access.AccessToken!, applied.Location, patch.Body, cancellationToken);
        if (!written.Succeeded)
        {
            return ResultOf(request, "locations.patch", written.Outcome, written.ErrorReason);
        }

        written.Value!.Dispose();
        _logger.LogInformation(
            "統籌助理 action {ActionId}: Google special hours of {Date} taken back ({Removed} removed, {Restored} restored, {Kept} other kept).",
            request.ActionId, applied.Date, patch.Removed, applied.Previous.Count, patch.Kept);
        return CoordinatorActionResult.Succeeded();
    }

    /// <summary>The shop's active connection and a fresh access token, or why the action cannot run.</summary>
    private async Task<Access> AccessAsync(CoordinatorActionRequest request, CancellationToken cancellationToken)
    {
        if (!_google.IsConfigured)
        {
            return new Access(null, null, CoordinatorActionResult.Unavailable);
        }

        var organizationId = request.OrganizationId;
        if (await _connections.ActiveAsync(organizationId, CoordinatorService.Google, cancellationToken) is not { } connection)
        {
            // Revoked since the command was confirmed (or removed): a reminder.
            var revoked = (await _connections.FindAsync(organizationId, CoordinatorService.Google, cancellationToken))?.Status == CoordinatorConnectionStatus.Revoked;
            return new Access(null, null, revoked ? CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.TokenRevoked) : CoordinatorActionResult.Unavailable);
        }

        string refreshToken;
        try
        {
            refreshToken = _secrets.Unprotect(CoordinatorConnection.PurposeOf(CoordinatorService.Google), connection.Secret);
        }
        catch (SecretUnprotectException exception)
        {
            _logger.LogError(
                "統籌助理 action {ActionId}: the Google authorization of organization {OrganizationId} cannot be decrypted ({Failure}); the owner authorizes again.",
                request.ActionId, organizationId, ExceptionSummary.Of(exception));
            return new Access(connection, null, CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.TokenRevoked));
        }

        var refreshed = await _google.RefreshAsync(refreshToken, cancellationToken);
        if (refreshed.Succeeded)
        {
            return new Access(connection, refreshed.Value!.AccessToken, null);
        }

        if (refreshed.Outcome == GoogleApiOutcome.ClientRejected)
        {
            _logger.LogError(
                "統籌助理 action {ActionId}: Google refused the platform's OAuth client (Coordinator:Google:ClientId／ClientSecret).", request.ActionId);
        }

        return new Access(connection, null, ResultOf(request, "token", refreshed.Outcome, refreshed.ErrorReason));
    }

    private CoordinatorActionResult ResultOf(CoordinatorActionRequest request, string call, GoogleApiOutcome outcome, string? reason)
    {
        var result = (outcome, reason) switch
        {
            (_, "SPECIAL_HOURS_SET_WITHOUT_REGULAR_HOURS") => CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.GoogleNoRegularHours),
            _ => ResultOf(outcome),
        };
        _logger.LogWarning(
            "統籌助理 action {ActionId}: Google {Call} → {Outcome} ({Reason}) → {Result} ({Code}).",
            request.ActionId, call, outcome, reason ?? "no reason", result.Outcome, result.FailureCode);
        return result;
    }

    private static CoordinatorActionResult ResultOf(GoogleApiOutcome outcome) =>
        outcome switch
        {
            GoogleApiOutcome.InvalidGrant or GoogleApiOutcome.Unauthorized or GoogleApiOutcome.Forbidden
                => CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.TokenRevoked),
            GoogleApiOutcome.AccessPending => CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.GoogleAccessPending),
            GoogleApiOutcome.RateLimited => CoordinatorActionResult.Failed(CoordinatorFailureCodes.RateLimited),
            GoogleApiOutcome.ServerError or GoogleApiOutcome.TimedOut or GoogleApiOutcome.NetworkError
                => CoordinatorActionResult.Failed(CoordinatorFailureCodes.GoogleUnavailable),
            _ => CoordinatorActionResult.Failed(CoordinatorFailureCodes.Rejected),
        };

    private static JsonElement? Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : null;

    /// <summary>The connection used and a fresh access token (never stored), or the result to answer instead.</summary>
    private sealed record Access(CoordinatorConnection? Connection, string? AccessToken, CoordinatorActionResult? Refusal)
    {
        public override string ToString() => $"{nameof(Access)} {{ {Connection} }}";
    }
}
