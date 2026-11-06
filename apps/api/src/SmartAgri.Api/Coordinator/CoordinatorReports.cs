using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// The messages a running command sends after its confirmation report (store-coordinator design §3 Q8, Q18, decision
/// E, §5 Slice 4, #375): a reminder pushed when it is due, and one follow-up with the results of the automatic actions
/// once all of a command's tries due by now have run — a reply when a 〔重試〕 press's token is still fresh
/// (<see cref="CoordinatorReplyTokens"/>), otherwise a push. Both go to the member who confirmed the command, only
/// while they are still a member of the shop; pushes use the coordinator account's quota (decision E). A failed push
/// is logged and dropped: the row stays as it is.
/// </summary>
public sealed class CoordinatorReports
{
    private readonly CoordinatorCommands _commands;
    private readonly CoordinatorCommandActions _actions;
    private readonly CoordinatorShops _shops;
    private readonly CoordinatorReplySender _sender;
    private readonly CoordinatorReplyTokens _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<CoordinatorReports> _logger;

    public CoordinatorReports(
        CoordinatorCommands commands,
        CoordinatorCommandActions actions,
        CoordinatorShops shops,
        CoordinatorReplySender sender,
        CoordinatorReplyTokens tokens,
        TimeProvider clock,
        ILogger<CoordinatorReports> logger)
    {
        _commands = commands;
        _actions = actions;
        _shops = shops;
        _sender = sender;
        _tokens = tokens;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>What a report of <paramref name="command"/> shows: its <paramref name="lines"/> (rows of it, and for a
    /// reopen the dropped rows of <paramref name="undone"/>), its announcement, the shop's name and zone.</summary>
    public static CoordinatorReportContent Content(
        ShopSettingsDocument settings,
        CoordinatorCommand command,
        IReadOnlyList<CoordinatorReportLine> lines,
        DateTimeOffset now,
        CoordinatorCommand? undone = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(command);
        return new CoordinatorReportContent(
            command.OrganizationId,
            settings.StoreName,
            settings.TimeZone,
            command.Scenario,
            command.Date!.Value,
            command.Time,
            lines,
            command.AnnouncementText,
            now,
            undone is null ? null : (undone.Scenario, undone.Date!.Value, undone.Time));
    }

    /// <summary>Pushes the reminder of <paramref name="action"/> (just turned <c>awaiting-manual</c>) of
    /// <paramref name="command"/>.</summary>
    public async Task PushReminderAsync(
        ShopSettingsDocument settings, CoordinatorCommand command, CoordinatorCommandAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(action);
        if (!await _commands.IsMemberAsync(action.OrganizationId, action.RecipientLineUserId, cancellationToken))
        {
            _logger.LogWarning("The reminder of 統籌助理 action {ActionId} was not pushed: its recipient is no longer a member of the shop.", action.Id);
            return;
        }

        var messages = CoordinatorExecutionReport.Reminder(
            action.OrganizationId, settings.StoreName, CoordinatorReportLine.Of(action, command), command.AnnouncementText);
        var pushed = await _sender.PushAsync(action.RecipientLineUserId, messages, action.Id, "reminder", cancellationToken);
        _logger.LogInformation(
            "Reminder of 統籌助理 action {ActionId} ({Kind} {Step}) {Outcome}.",
            action.Id, Domain.WireNames<ShopActionKind>.ToWire(action.Kind), Domain.WireNames<CoordinatorActionStep>.ToWire(action.Step),
            pushed ? "pushed" : "not pushed");
    }

    /// <summary>The follow-up of <paramref name="commandId"/>, when its automatic tries due by now have all run and
    /// some outcome is untold (<see cref="CoordinatorCommandActions.TakeUnreportedAsync"/>); otherwise nothing.</summary>
    public async Task SendFollowUpAsync(Guid organizationId, Guid commandId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var rows = await _actions.TakeUnreportedAsync(organizationId, commandId, now, cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        var command = await _commands.FindAsync(organizationId, commandId, cancellationToken)
            ?? throw new InvalidOperationException("An action's command is gone.");
        var settings = await _shops.FindAsync(organizationId, cancellationToken)
            ?? throw new InvalidOperationException("A shop with a command has no settings.");
        var undone = command.ReopensCommandId is { } undoneId ? await _commands.FindAsync(organizationId, undoneId, cancellationToken) : null;
        var content = Content(settings, command, [.. rows.Select(row => CoordinatorReportLine.Of(row, command))], now, undone);
        var messages = CoordinatorExecutionReport.FollowUp(content);

        if (_tokens.Take(commandId) is { } replyToken
            && await _sender.TryReplyAsync(replyToken, Application.Line.LineWebhookEvent.Types.Postback, messages, cancellationToken))
        {
            _logger.LogInformation("Follow-up of 統籌助理 command {CommandId} ({Actions} actions) replied.", commandId, rows.Count);
            return;
        }

        var recipient = rows[0].RecipientLineUserId;
        if (!await _commands.IsMemberAsync(organizationId, recipient, cancellationToken))
        {
            _logger.LogWarning("The follow-up of 統籌助理 command {CommandId} was not pushed: its recipient is no longer a member of the shop.", commandId);
            return;
        }

        var pushed = await _sender.PushAsync(recipient, messages, Guid.CreateVersion7(), "follow-up", cancellationToken);
        _logger.LogInformation(
            "Follow-up of 統籌助理 command {CommandId} ({Actions} actions) {Outcome}.", commandId, rows.Count, pushed ? "pushed" : "not pushed");
    }
}
