using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Application.Observability;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// Handles one 統籌助理 delivery's events (store-coordinator design §5 Slice 1, #372): finds who the
/// sender is bound to (<see cref="CoordinatorLookup"/>), lets <see cref="CoordinatorMessageRouter"/>
/// decide, and carries the decision out — binding with a code (<see cref="CoordinatorBindings"/>,
/// throttled by <see cref="CoordinatorBindingThrottle"/>), issuing a 店長 code for a 老闆, or replying
/// with a fixed text. Every answer is a reply (free) with the coordinator's access token; no push, no
/// model call.
/// </summary>
/// <remarks>
/// Each reply token is used at most once; a LINE failure is logged (event type, outcome, status,
/// <c>x-line-request-id</c> — never a token, a LINE id, a code or a message) and dropped. A failing event
/// is logged with its exception type and the next event is handled.
/// </remarks>
internal sealed class CoordinatorEventHandler
{
    private readonly CoordinatorLookup _lookup;
    private readonly CoordinatorBindings _bindings;
    private readonly CoordinatorBindingThrottle _throttle;
    private readonly ILineMessagingClient _line;
    private readonly CoordinatorLineOptions _options;
    private readonly ILogger<CoordinatorEventHandler> _logger;

    public CoordinatorEventHandler(
        CoordinatorLookup lookup,
        CoordinatorBindings bindings,
        CoordinatorBindingThrottle throttle,
        ILineMessagingClient line,
        IOptions<CoordinatorLineOptions> options,
        ILogger<CoordinatorEventHandler> logger)
    {
        _lookup = lookup;
        _bindings = bindings;
        _throttle = throttle;
        _line = line;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleAsync(CoordinatorWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return;
        }

        foreach (var lineEvent in delivery.Events)
        {
            try
            {
                await HandleEventAsync(lineEvent, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Only types: the event is a LINE user's message, and an exception message can quote it.
                _logger.LogError(
                    "A 統籌助理 {EventType} event failed and was dropped: {Failure}", lineEvent.Type, ExceptionSummary.Of(exception));
            }
        }
    }

    private async Task HandleEventAsync(LineWebhookEvent lineEvent, CancellationToken cancellationToken)
    {
        if (lineEvent.Type is not (LineWebhookEvent.Types.Follow or LineWebhookEvent.Types.Message or LineWebhookEvent.Types.Postback)
            || !lineEvent.Source.IsOneToOne
            || lineEvent.IsStandby
            || lineEvent.Source.UserId is not { } userId
            || !CoordinatorMember.IsValidLineUserId(userId))
        {
            return;
        }

        var memberships = await _lookup.FindMembershipsAsync(userId, cancellationToken);
        var intent = CoordinatorMessageRouter.Route(lineEvent, isBound: memberships.Count > 0);
        switch (intent.Kind)
        {
            case CoordinatorIntentKind.Welcome:
                await ReplyAsync(lineEvent, CoordinatorReplies.Welcome, cancellationToken);
                return;

            case CoordinatorIntentKind.Menu:
                await ReplyAsync(lineEvent, CoordinatorReplies.Menu, cancellationToken);
                return;

            case CoordinatorIntentKind.AskForCode:
                await ReplyAsync(lineEvent, CoordinatorReplies.AskForCode, cancellationToken);
                return;

            case CoordinatorIntentKind.Bind:
                await ReplyAsync(lineEvent, await BindAsync(userId, intent.Code!, cancellationToken), cancellationToken);
                return;

            case CoordinatorIntentKind.IssueManagerCode:
                await IssueManagerCodeAsync(lineEvent, userId, memberships, chosen: null, cancellationToken);
                return;

            case CoordinatorIntentKind.IssueManagerCodeFor:
                await IssueManagerCodeAsync(lineEvent, userId, memberships, intent.OrganizationId, cancellationToken);
                return;

            default:
                return;
        }
    }

    /// <summary>Tries <paramref name="code"/> for <paramref name="userId"/> and says how it went. Unknown,
    /// used and expired codes count against the sender (<see cref="CoordinatorBindingThrottle"/>); once
    /// they have used up their attempts the code is not even looked at.</summary>
    private async Task<string> BindAsync(string userId, string code, CancellationToken cancellationToken)
    {
        if (_throttle.IsBlocked(userId))
        {
            return CoordinatorReplies.TooManyAttempts;
        }

        var match = await _lookup.FindCodeAsync(BindingCodes.Hash(code), cancellationToken);
        var result = match is null
            ? new CoordinatorBindResult(CoordinatorBindOutcome.Invalid)
            : await _bindings.BindAsync(match, userId, cancellationToken);
        switch (result.Outcome)
        {
            case CoordinatorBindOutcome.Bound:
                return CoordinatorReplies.Bound(result.StoreName!, result.Role!.Value);
            case CoordinatorBindOutcome.AlreadyBound:
                return CoordinatorReplies.AlreadyBound(result.StoreName!, result.Role!.Value);
            default:
                _throttle.RecordFailure(userId);
                return CoordinatorReplies.InvalidCode;
        }
    }

    /// <summary>
    /// 「產生店長綁定碼」 (<paramref name="chosen"/> <see langword="null"/>) or the store chosen from the
    /// quick reply: only a 老闆 gets a code — of exactly one shop at once, of several after choosing one
    /// (checked again for the chosen one: postback data is the sender's to forge).
    /// </summary>
    private async Task IssueManagerCodeAsync(
        LineWebhookEvent lineEvent,
        string userId,
        IReadOnlyList<CoordinatorMembership> memberships,
        Guid? chosen,
        CancellationToken cancellationToken)
    {
        if (lineEvent.ReplyToken is not { Length: > 0 })
        {
            // A code nobody would see is not issued.
            return;
        }

        var owned = memberships.Where(membership => membership.Role == CoordinatorRole.Owner).ToList();
        CoordinatorMembership? target;
        if (chosen is { } organizationId)
        {
            target = owned.FirstOrDefault(membership => membership.OrganizationId == organizationId);
        }
        else if (owned.Count > 1)
        {
            await ReplyAsync(
                lineEvent,
                CoordinatorReplies.StoreChoice([.. owned.Select(membership => (membership.OrganizationId, membership.StoreName))]),
                cancellationToken);
            return;
        }
        else
        {
            target = owned.SingleOrDefault();
        }

        var issued = target is null
            ? null
            : await _bindings.IssueAsync(
                target.OrganizationId, CoordinatorRole.Manager, CoordinatorBindingCode.ManagerValidity, userId, cancellationToken);
        if (issued is null)
        {
            await ReplyAsync(lineEvent, CoordinatorReplies.OwnersOnly, cancellationToken);
            return;
        }

        var text = CoordinatorReplies.ManagerCode(
            issued.Code, issued.StoreName, issued.ExpiresAt, CoordinatorBindings.TimeZoneOf(target!.OrganizationId));
        await ReplyAsync(lineEvent, text, cancellationToken);
    }

    private Task ReplyAsync(LineWebhookEvent lineEvent, string text, CancellationToken cancellationToken) =>
        ReplyAsync(lineEvent, LineMessages.Text(text), cancellationToken);

    /// <summary>Replies once with <paramref name="message"/>; a failure is logged and dropped.</summary>
    private async Task ReplyAsync(LineWebhookEvent lineEvent, JsonObject message, CancellationToken cancellationToken)
    {
        if (lineEvent.ReplyToken is not { Length: > 0 } replyToken)
        {
            return;
        }

        var result = await _line.ReplyAsync(_options.AccessToken, replyToken, [message], cancellationToken);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "LINE refused the 統籌助理's reply to a {EventType} event: {Outcome} (HTTP {StatusCode}, x-line-request-id {RequestId}); dropped.",
                lineEvent.Type, result.Outcome, result.StatusCode, result.RequestId);
        }
    }
}
