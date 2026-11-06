using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Application.Observability;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Coordinator;

/// <summary>A command just confirmed (<see cref="CoordinatorCommand.Status"/> <c>confirmed</c>), and the name of
/// its shop as the member sees it.</summary>
public sealed record ConfirmedCoordinatorCommand(CoordinatorCommand Command, string StoreName);

/// <summary>
/// What happens once a preview card is confirmed (store-coordinator design §5 Slices 3–4, #374/#375). Called exactly
/// once per command — after the compare-and-set that confirmed it — with the reply token of the 〔確認〕 press.
/// </summary>
public interface ICoordinatorCommandRunner
{
    Task StartAsync(ConfirmedCoordinatorCommand confirmed, string? replyToken, CancellationToken cancellationToken);
}

/// <summary>
/// Carries a confirmed command out (store-coordinator design §3 Q7, Q8, Q18, Q20, Q21, Q25, decision F, §5 Slice 4,
/// #375): writes one <see cref="CoordinatorCommandAction"/> per action from the shop's settings at this moment
/// (<see cref="CoordinatorActionSchedule"/>; a reopen from the rows of the command it undoes, dropping those still
/// waiting) with one background job per row still to run, then replies with the report
/// (<see cref="CoordinatorExecutionReport.Confirmation"/>): reminders due now with their steps and 〔已完成〕, later
/// reminders with their time, automatic actions 「執行中…」 — whose results follow once their jobs have run
/// (<see cref="CoordinatorReports.SendFollowUpAsync"/>).
/// </summary>
public sealed class ExecutingCommandRunner : ICoordinatorCommandRunner
{
    private static readonly ShopActionKind[] AutomaticKinds =
        [ShopActionKind.LineBroadcast, ShopActionKind.GoogleSpecialHours, ShopActionKind.FacebookPost];

    private readonly CoordinatorCommands _commands;
    private readonly CoordinatorCommandActions _actions;
    private readonly CoordinatorShops _shops;
    private readonly ICoordinatorActionModes _modes;
    private readonly CoordinatorReplySender _replies;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExecutingCommandRunner> _logger;

    public ExecutingCommandRunner(
        CoordinatorCommands commands,
        CoordinatorCommandActions actions,
        CoordinatorShops shops,
        ICoordinatorActionModes modes,
        CoordinatorReplySender replies,
        TimeProvider clock,
        ILogger<ExecutingCommandRunner> logger)
    {
        _commands = commands;
        _actions = actions;
        _shops = shops;
        _modes = modes;
        _replies = replies;
        _clock = clock;
        _logger = logger;
    }

    public async Task StartAsync(ConfirmedCoordinatorCommand confirmed, string? replyToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        var command = confirmed.Command;
        var organizationId = command.OrganizationId;
        var settings = await _shops.FindAsync(organizationId, cancellationToken)
            ?? throw new InvalidOperationException("A shop with a command has no settings.");
        var now = _clock.GetUtcNow();

        IReadOnlyList<CoordinatorActionPlan> plans;
        IReadOnlyList<Guid> cancel = [];
        CoordinatorCommand? undone = null;
        if (command.Scenario != ShopScenario.Reopen)
        {
            var modes = new List<ShopActionMode>();
            foreach (var action in settings[command.Scenario].Actions)
            {
                modes.Add(await _modes.ModeOfAsync(organizationId, action, cancellationToken));
            }

            plans = CoordinatorActionSchedule.Plan(settings, command.Scenario, command.Date!.Value, command.Time, modes, now);
        }
        else
        {
            undone = await _commands.FindAsync(organizationId, command.ReopensCommandId!.Value, cancellationToken)
                ?? throw new InvalidOperationException("A reopen's command is gone.");
            var autoModes = new Dictionary<ShopActionKind, ShopActionMode>();
            foreach (var kind in AutomaticKinds)
            {
                autoModes[kind] = await _modes.ModeOfAsync(organizationId, new ShopAction(kind, ShopActionMode.Auto), cancellationToken);
            }

            var plan = CoordinatorActionSchedule.PlanReopen(
                await _actions.ListAsync(organizationId, undone.Id, cancellationToken),
                undone.Scenario,
                undone.Date!.Value,
                undone.Time,
                correction: command.CorrectionBroadcast && command.AnnouncementText is not null,
                settings[ShopScenario.Reopen].Announcement?.ImageUrl,
                autoModes,
                now);
            plans = plan.Actions;
            cancel = plan.Cancel;
        }

        var recipient = command.ConfirmedByLineUserId ?? throw new InvalidOperationException("A confirmed command has who confirmed it.");
        var rows = plans
            .Select((plan, position) => CoordinatorCommandAction.Create(
                organizationId, command.Id, position, plan.Kind, plan.Name, plan.Step, plan.Mode, plan.DueAt, plan.StepsText, plan.ImageUrl,
                recipient, plan.UndoesActionId, now))
            .ToList();
        await _actions.StartAsync(organizationId, rows, cancel, now);
        _logger.LogInformation(
            "統籌助理 command {CommandId} ({Scenario}) started: {Actions} actions ({Scheduled} scheduled), {Cancelled} of the undone command's dropped.",
            command.Id, Domain.WireNames<ShopScenario>.ToWire(command.Scenario), rows.Count,
            rows.Count(row => row.Status == CoordinatorActionStatus.Scheduled), cancel.Count);

        var lines = new List<CoordinatorReportLine>();
        if (undone is not null && cancel.Count > 0)
        {
            lines.AddRange((await _actions.ListAsync(organizationId, undone.Id, cancellationToken))
                .Where(row => cancel.Contains(row.Id) && row.Status == CoordinatorActionStatus.Cancelled)
                .Select(row => CoordinatorReportLine.Of(row, undone)));
        }

        lines.AddRange(rows.Select(row => CoordinatorReportLine.Of(row, command)));
        var content = CoordinatorReports.Content(settings, command, lines, now, undone);
        await _replies.ReplyAsync(replyToken, LineWebhookEvent.Types.Postback, CoordinatorExecutionReport.Confirmation(content), cancellationToken);
    }
}

/// <summary>
/// The reply tokens of 〔重試〕 presses, kept in memory for the follow-up that answers them (#375): a reply is free, but
/// LINE accepts one only about a minute after the event, so a token counts as fresh for <see cref="Freshness"/> after
/// it was received; after that the follow-up is pushed. One per command (the latest press), used once.
/// </summary>
public sealed class CoordinatorReplyTokens
{
    /// <summary>How long a kept reply token is used.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(50);

    private readonly ConcurrentDictionary<Guid, (string Token, DateTimeOffset ReceivedAt)> _tokens = new();
    private readonly TimeProvider _clock;

    public CoordinatorReplyTokens(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Keeps <paramref name="replyToken"/>, received now, for command <paramref name="commandId"/>'s next follow-up.</summary>
    public void Keep(Guid commandId, string replyToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(replyToken);
        var now = _clock.GetUtcNow();
        foreach (var (key, kept) in _tokens)
        {
            if (now - kept.ReceivedAt >= Freshness)
            {
                _tokens.TryRemove(new KeyValuePair<Guid, (string, DateTimeOffset)>(key, kept));
            }
        }

        _tokens[commandId] = (replyToken, now);
    }

    /// <summary>The token kept for <paramref name="commandId"/> while it is fresh (removed either way).</summary>
    public string? Take(Guid commandId) =>
        _tokens.TryRemove(commandId, out var kept) && _clock.GetUtcNow() - kept.ReceivedAt < Freshness ? kept.Token : null;
}

/// <summary>How a push of the 統籌助理 went (<see cref="CoordinatorReplySender.PushAsync"/>, #390).</summary>
public enum CoordinatorPushOutcome
{
    /// <summary>LINE accepted it now (and it is in the push log).</summary>
    Sent,

    /// <summary>LINE had accepted a push with the same retry key before (<c>409</c>): sent then, not again.</summary>
    AlreadySent,

    /// <summary>The coordinator account's monthly message limit is reached (<c>429</c> 「You have reached your monthly
    /// limit.」): LINE refuses every push until next month or a bigger plan.</summary>
    MonthlyLimit,

    /// <summary>A failure a later try may get past: a rate limit, a timeout, the network, LINE's own error (5xx).</summary>
    Failed,

    /// <summary>A refusal trying again will not change (the token refused, a <c>4xx</c> such as an unknown user).</summary>
    Refused,

    /// <summary>The 統籌助理 is not configured: nothing was sent.</summary>
    NotConfigured,
}

/// <summary>A push's <see cref="Outcome"/>, with LINE's answer when there was one (never a message or a LINE id).</summary>
public sealed record CoordinatorPushResult(CoordinatorPushOutcome Outcome, LineApiResult? Line = null)
{
    /// <summary>The recipient has it: sent now or before.</summary>
    public bool Delivered => Outcome is CoordinatorPushOutcome.Sent or CoordinatorPushOutcome.AlreadySent;

    /// <summary><paramref name="result"/> of a push with a retry key, classified.</summary>
    public static CoordinatorPushResult Of(LineApiResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var outcome = result switch
        {
            { Succeeded: true } => CoordinatorPushOutcome.Sent,
            { IsRetryKeyAccepted: true } => CoordinatorPushOutcome.AlreadySent,
            { IsMonthlyLimit: true } => CoordinatorPushOutcome.MonthlyLimit,
            { Outcome: LineApiOutcome.RateLimited or LineApiOutcome.TimedOut or LineApiOutcome.NetworkError } => CoordinatorPushOutcome.Failed,
            { Outcome: LineApiOutcome.HttpError, StatusCode: null or >= 500 } => CoordinatorPushOutcome.Failed,
            _ => CoordinatorPushOutcome.Refused,
        };
        return new CoordinatorPushResult(outcome, result);
    }
}

/// <summary>
/// Sends the 統籌助理's messages with its access token (#372, #374, #375, #390): replies (free; 1 to 5 message objects per
/// reply token, used once) and pushes — reservation cards, reminders, follow-ups, re-authorization links — counted
/// against the coordinator account's monthly quota, which the platform bears (decision E). Every push LINE accepts is
/// written to the shop's push log (<see cref="CoordinatorPushLog"/>, for <c>coordinator-quota</c>). A LINE failure is
/// logged (event type or what was pushed, outcome, status, <c>x-line-request-id</c> — never a token, a LINE id or a
/// message) and dropped; the monthly limit is logged as such.
/// </summary>
public sealed class CoordinatorReplySender
{
    private readonly ILineMessagingClient _line;
    private readonly CoordinatorLineOptions _options;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<CoordinatorReplySender> _logger;

    public CoordinatorReplySender(
        ILineMessagingClient line,
        IOptions<CoordinatorLineOptions> options,
        DbContextOptions<AppDbContext> dbOptions,
        TimeProvider clock,
        ILogger<CoordinatorReplySender> logger)
    {
        _line = line;
        _options = options.Value;
        _dbOptions = dbOptions;
        _clock = clock;
        _logger = logger;
    }

    public async Task ReplyAsync(string? replyToken, string? eventType, IReadOnlyList<JsonObject> messages, CancellationToken cancellationToken) =>
        await TryReplyAsync(replyToken, eventType, messages, cancellationToken);

    /// <summary>Replies; <see langword="false"/> when there was no token, nothing to say, or LINE refused.</summary>
    public async Task<bool> TryReplyAsync(string? replyToken, string? eventType, IReadOnlyList<JsonObject> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (replyToken is not { Length: > 0 } || messages.Count == 0 || !_options.IsConfigured)
        {
            return false;
        }

        var result = await _line.ReplyAsync(_options.AccessToken, replyToken, [.. messages.Take(LineMessages.MaxPerRequest)], cancellationToken);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "LINE refused the 統籌助理's reply to a {EventType} event: {Outcome} (HTTP {StatusCode}, x-line-request-id {RequestId}); dropped.",
                eventType, result.Outcome, result.StatusCode, result.RequestId);
        }

        return result.Succeeded;
    }

    /// <summary>
    /// Pushes <paramref name="messages"/> for <paramref name="purpose"/> of shop <paramref name="organizationId"/> to
    /// <paramref name="to"/>; <paramref name="retryKey"/> (<c>X-Line-Retry-Key</c>, kept by LINE for 24 hours) makes a
    /// repeated push harmless (<see cref="CoordinatorPushOutcome.AlreadySent"/>). A push LINE accepts gets a row in the
    /// shop's push log; failing to write it is logged and does not change the outcome.
    /// </summary>
    public async Task<CoordinatorPushResult> PushAsync(
        Guid organizationId,
        string to,
        IReadOnlyList<JsonObject> messages,
        Guid retryKey,
        CoordinatorPushPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(to);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            throw new ArgumentException("A push carries at least one message.", nameof(messages));
        }

        var what = WireNames<CoordinatorPushPurpose>.ToWire(purpose);
        if (!_options.IsConfigured)
        {
            _logger.LogWarning("The 統籌助理 is not configured: a {What} was not pushed.", what);
            return new CoordinatorPushResult(CoordinatorPushOutcome.NotConfigured);
        }

        var result = CoordinatorPushResult.Of(
            await _line.PushAsync(_options.AccessToken, to, [.. messages.Take(LineMessages.MaxPerRequest)], retryKey, cancellationToken));
        var line = result.Line!;
        switch (result.Outcome)
        {
            case CoordinatorPushOutcome.Sent:
                await LogPushAsync(organizationId, purpose, cancellationToken);
                break;

            case CoordinatorPushOutcome.AlreadySent:
                _logger.LogInformation(
                    "The 統籌助理's push of a {What} was accepted before with the same retry key (x-line-request-id {RequestId}); not sent again.",
                    what, line.RequestId);
                break;

            case CoordinatorPushOutcome.MonthlyLimit:
                _logger.LogWarning(
                    "The 統籌助理's monthly message limit is reached: LINE refused the push of a {What} of organization {OrganizationId} "
                    + "(HTTP {StatusCode}, x-line-request-id {RequestId}). Check coordinator-quota and the account's plan.",
                    what, organizationId, line.StatusCode, line.RequestId);
                break;

            default:
                _logger.LogWarning(
                    "LINE refused the 統籌助理's push of a {What}: {Outcome} (HTTP {StatusCode}, x-line-request-id {RequestId}); dropped.",
                    what, line.Outcome, line.StatusCode, line.RequestId);
                break;
        }

        return result;
    }

    /// <summary>The LINE display name of <paramref name="lineUserId"/> as the coordinator account sees them (free, not a
    /// message); <see langword="null"/> when LINE does not give it (blocked, not a friend, any failure). Never logged.</summary>
    public async Task<string?> DisplayNameAsync(string lineUserId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(lineUserId);
        if (!_options.IsConfigured)
        {
            return null;
        }

        var profile = await _line.GetProfileAsync(_options.AccessToken, lineUserId, cancellationToken);
        return profile is { Succeeded: true, Value.DisplayName: { } name } && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
    }

    private async Task LogPushAsync(Guid organizationId, CoordinatorPushPurpose purpose, CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = new AppDbContext(_dbOptions, new FixedOrganizationContext(organizationId));
            dbContext.CoordinatorPushLogs.Add(CoordinatorPushLog.Record(organizationId, purpose, recipients: 1, _clock.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "A {Purpose} push of organization {OrganizationId} was sent but not written to the push log: {Failure}",
                WireNames<CoordinatorPushPurpose>.ToWire(purpose), organizationId, ExceptionSummary.Of(exception));
        }
    }
}
