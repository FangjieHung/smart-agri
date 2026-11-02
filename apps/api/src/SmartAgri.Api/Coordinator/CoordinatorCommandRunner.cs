using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>A command just confirmed (<see cref="CoordinatorCommand.Status"/> <c>confirmed</c>), and the name of
/// its shop as the member sees it.</summary>
public sealed record ConfirmedCoordinatorCommand(CoordinatorCommand Command, string StoreName);

/// <summary>
/// What happens once a preview card is confirmed (store-coordinator design §5 Slices 3–4, #374/#375): the seam
/// where Slice 4 starts carrying the command out (one result row and background job per action, and the
/// report). Called exactly once per command — after the compare-and-set that confirmed it — with the reply token
/// of the 〔確認〕 press (already used or expired by the time a background job runs; Slice 4 pushes then).
/// </summary>
public interface ICoordinatorCommandRunner
{
    Task StartAsync(ConfirmedCoordinatorCommand confirmed, string? replyToken, CancellationToken cancellationToken);
}

/// <summary>This slice's <see cref="ICoordinatorCommandRunner"/>: only says so —
/// 「已確認：{店名} {日期} {情境}。」 — as a reply.</summary>
public sealed class AcknowledgingCommandRunner : ICoordinatorCommandRunner
{
    private readonly CoordinatorReplySender _replies;

    public AcknowledgingCommandRunner(CoordinatorReplySender replies)
    {
        _replies = replies;
    }

    public Task StartAsync(ConfirmedCoordinatorCommand confirmed, string? replyToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        var command = confirmed.Command;
        var text = CoordinatorCommandReplies.Confirmed(confirmed.StoreName, command.Scenario, command.Date!.Value, command.Time);
        return _replies.ReplyAsync(replyToken, LineWebhookEvent.Types.Postback, [LineMessages.Text(text)], cancellationToken);
    }
}

/// <summary>
/// Sends the 統籌助理's replies (free) with its access token (#372, #374): 1 to 5 message objects per reply
/// token, used once. A LINE failure is logged (event type, outcome, status, <c>x-line-request-id</c> — never a
/// token, a LINE id or a message) and dropped.
/// </summary>
public sealed class CoordinatorReplySender
{
    private readonly ILineMessagingClient _line;
    private readonly CoordinatorLineOptions _options;
    private readonly ILogger<CoordinatorReplySender> _logger;

    public CoordinatorReplySender(ILineMessagingClient line, IOptions<CoordinatorLineOptions> options, ILogger<CoordinatorReplySender> logger)
    {
        _line = line;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ReplyAsync(string? replyToken, string? eventType, IReadOnlyList<JsonObject> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (replyToken is not { Length: > 0 } || messages.Count == 0)
        {
            return;
        }

        var result = await _line.ReplyAsync(_options.AccessToken, replyToken, [.. messages.Take(LineMessages.MaxPerRequest)], cancellationToken);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "LINE refused the 統籌助理's reply to a {EventType} event: {Outcome} (HTTP {StatusCode}, x-line-request-id {RequestId}); dropped.",
                eventType, result.Outcome, result.StatusCode, result.RequestId);
        }
    }
}
