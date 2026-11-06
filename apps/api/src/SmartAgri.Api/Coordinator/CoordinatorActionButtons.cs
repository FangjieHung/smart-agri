using System.Text.Json.Nodes;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// 〔已完成〕 and 〔重試〕 on a running command's actions (store-coordinator design §3 Q8, Q18, Q25, §5 Slice 4, #375),
/// for <see cref="CoordinatorEventHandler"/>. The postback's organization is checked against the sender's memberships
/// and, in the compare-and-set itself, again inside the shop's own context: any owner or manager of the shop may press.
/// </summary>
/// <remarks>
/// 〔已完成〕 records who and when, once (「已記錄：…」; later presses say it is recorded already). 〔重試〕 re-runs only a
/// failed action — never one that succeeded or waits for a hand (Q8: nothing else is rolled back or repeated) — and
/// says nothing at once: its reply token is kept (<see cref="CoordinatorReplyTokens"/>) for the follow-up with the
/// result, pushed instead when the job takes longer than the token stays fresh.
/// </remarks>
public sealed class CoordinatorActionButtons
{
    private readonly CoordinatorCommandActions _actions;
    private readonly CoordinatorCommands _commands;
    private readonly CoordinatorReplyTokens _tokens;
    private readonly TimeProvider _clock;

    public CoordinatorActionButtons(
        CoordinatorCommandActions actions, CoordinatorCommands commands, CoordinatorReplyTokens tokens, TimeProvider clock)
    {
        _actions = actions;
        _commands = commands;
        _tokens = tokens;
        _clock = clock;
    }

    /// <summary>〔已完成〕.</summary>
    public async Task<IReadOnlyList<JsonObject>> CompleteAsync(
        string lineUserId, IReadOnlyList<CoordinatorMembership> memberships, Guid organizationId, Guid actionId, CancellationToken cancellationToken)
    {
        if (memberships.All(membership => membership.OrganizationId != organizationId))
        {
            return Say(CoordinatorExecutionReport.NotAMember);
        }

        var press = await _actions.CompleteAsync(organizationId, actionId, lineUserId, _clock.GetUtcNow(), cancellationToken);
        switch (press)
        {
            case { Outcome: CoordinatorActionPressOutcome.Done, Action: { } action }:
                var command = await _commands.FindAsync(organizationId, action.CommandId, cancellationToken)
                    ?? throw new InvalidOperationException("An action's command is gone.");
                return Say(CoordinatorExecutionReport.Recorded(CoordinatorReportLine.Of(action, command).Label));
            case { Outcome: CoordinatorActionPressOutcome.NotApplicable, Action: { } other }:
                return Say(other.Status switch
                {
                    CoordinatorActionStatus.DoneManually => CoordinatorExecutionReport.AlreadyRecorded,
                    CoordinatorActionStatus.Cancelled => CoordinatorExecutionReport.ActionCancelled,
                    _ => CoordinatorExecutionReport.NotManual,
                });
            case { Outcome: CoordinatorActionPressOutcome.NotAMember }:
                return Say(CoordinatorExecutionReport.NotAMember);
            default:
                return Say(CoordinatorExecutionReport.UnknownAction);
        }
    }

    /// <summary>〔重試〕: nothing at once when it retries (the follow-up answers, with <paramref name="replyToken"/>
    /// while fresh); otherwise why not.</summary>
    public async Task<IReadOnlyList<JsonObject>> RetryAsync(
        string lineUserId,
        IReadOnlyList<CoordinatorMembership> memberships,
        Guid organizationId,
        Guid actionId,
        string? replyToken,
        CancellationToken cancellationToken)
    {
        if (memberships.All(membership => membership.OrganizationId != organizationId))
        {
            return Say(CoordinatorExecutionReport.NotAMember);
        }

        var press = await _actions.RetryAsync(organizationId, actionId, lineUserId, _clock.GetUtcNow(), cancellationToken);
        switch (press)
        {
            case { Outcome: CoordinatorActionPressOutcome.Done, Action: { } action }:
                if (replyToken is { Length: > 0 })
                {
                    _tokens.Keep(action.CommandId, replyToken);
                }

                return [];
            case { Outcome: CoordinatorActionPressOutcome.NotApplicable, Action: { } other }:
                return Say(other.Status switch
                {
                    CoordinatorActionStatus.Succeeded => CoordinatorExecutionReport.AlreadySucceeded,
                    CoordinatorActionStatus.Scheduled => CoordinatorExecutionReport.RetryRunning,
                    CoordinatorActionStatus.Cancelled => CoordinatorExecutionReport.RetryCancelled,
                    _ => CoordinatorExecutionReport.RetryManual,
                });
            case { Outcome: CoordinatorActionPressOutcome.NotAMember }:
                return Say(CoordinatorExecutionReport.NotAMember);
            default:
                return Say(CoordinatorExecutionReport.UnknownAction);
        }
    }

    private static IReadOnlyList<JsonObject> Say(string text) => [LineMessages.Text(text)];
}
