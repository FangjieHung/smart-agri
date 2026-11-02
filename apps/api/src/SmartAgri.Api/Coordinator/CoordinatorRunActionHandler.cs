using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Observability;
using SmartAgri.Domain;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// Handles <see cref="CoordinatorRunActionJob.Kind"/> (store-coordinator design §3 Q8, Q18, §5 Slice 4, #375): runs one
/// action of a confirmed command at its due time — nothing unless its row is still <c>scheduled</c> (a reopen
/// cancelled it, or another run did it). A reminder becomes <c>awaiting-manual</c> and is pushed to the member who
/// confirmed (steps, 〔已完成〕; for LINE 群發 and FB 貼文 also the announcement). An automatic action goes to its kind's
/// <see cref="ICoordinatorActionExecutor"/> (<see cref="ICoordinatorActionExecutor.UndoAsync"/> for an undo): its
/// result is recorded — an unavailable integration or none registered makes it a reminder (Q18) — and the follow-up
/// is sent once the command's tries due by now have all run.
/// </summary>
/// <remarks>
/// An executor's exception is a failure (<see cref="CoordinatorFailureCodes.Error"/>), never retried by the queue:
/// the member presses 〔重試〕. Only failing to read or write the database is retried by the queue; when it gives up,
/// a row still scheduled becomes failed so 〔重試〕 can run it. Logs carry ids, kinds and exception types — never a
/// LINE id, an announcement or what an integration said.
/// </remarks>
internal sealed class CoordinatorRunActionHandler : IJobHandler
{
    private readonly CoordinatorCommandActions _actions;
    private readonly CoordinatorCommands _commands;
    private readonly CoordinatorShops _shops;
    private readonly CoordinatorActionExecutors _executors;
    private readonly CoordinatorReports _reports;
    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<CoordinatorRunActionHandler> _logger;

    public CoordinatorRunActionHandler(
        CoordinatorCommandActions actions,
        CoordinatorCommands commands,
        CoordinatorShops shops,
        CoordinatorActionExecutors executors,
        CoordinatorReports reports,
        AppDbContext dbContext,
        TimeProvider clock,
        ILogger<CoordinatorRunActionHandler> logger)
    {
        _actions = actions;
        _commands = commands;
        _shops = shops;
        _executors = executors;
        _reports = reports;
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<CoordinatorRunActionJob>();
        var organizationId = job.OrganizationId;
        var action = await _actions.FindAsync(organizationId, payload.ActionId, cancellationToken);
        if (action is not { Status: CoordinatorActionStatus.Scheduled })
        {
            _logger.LogInformation(
                "統籌助理 action {ActionId} not run: it is {Status}.",
                payload.ActionId, action is null ? "gone" : WireNames<CoordinatorActionStatus>.ToWire(action.Status));
            return;
        }

        var command = await _commands.FindAsync(organizationId, action.CommandId, cancellationToken)
            ?? throw new PermanentJobFailure("The action's command is gone.");
        var settings = await _shops.FindAsync(organizationId, cancellationToken)
            ?? throw new PermanentJobFailure("The shop has no 統籌助理 settings.");

        if (action.Mode == ShopActionMode.Reminder)
        {
            if (await _actions.BeginReminderAsync(organizationId, action.Id, cancellationToken))
            {
                await _reports.PushReminderAsync(settings, command, action, cancellationToken);
            }

            return;
        }

        var result = await ExecuteAsync(action, command, settings, cancellationToken);
        if (await _actions.RecordAsync(organizationId, action.Id, result, _clock.GetUtcNow()))
        {
            _logger.LogInformation(
                "統籌助理 action {ActionId} ({Kind} {Step}) ran: {Outcome}{Failure}.",
                action.Id, WireNames<ShopActionKind>.ToWire(action.Kind), WireNames<CoordinatorActionStep>.ToWire(action.Step), result.Outcome,
                result.FailureCode is { } code ? $" ({code})" : string.Empty);
        }
        else
        {
            _logger.LogWarning(
                "統籌助理 action {ActionId} ({Kind} {Step}) ran ({Outcome}) after a reopen had dropped it; a success is recorded but not undone.",
                action.Id, WireNames<ShopActionKind>.ToWire(action.Kind), WireNames<CoordinatorActionStep>.ToWire(action.Step), result.Outcome);
        }

        await _reports.SendFollowUpAsync(organizationId, command.Id, cancellationToken);
    }

    public async Task OnFinalFailureAsync(JobContext job, string error, CancellationToken cancellationToken)
    {
        var payload = job.ReadPayload<CoordinatorRunActionJob>();
        var failed = await CoordinatorCommandActions.GiveUpAsync(_dbContext, payload.ActionId, _clock.GetUtcNow(), cancellationToken);
        _logger.LogError(
            "The job of 統籌助理 action {ActionId} gave up after {Attempts} attempts{Marked}.",
            payload.ActionId, job.Attempt, failed == 1 ? "; the action is marked failed for 〔重試〕" : string.Empty);
    }

    private async Task<CoordinatorActionResult> ExecuteAsync(
        CoordinatorCommandAction action, CoordinatorCommand command, ShopSettingsDocument settings, CancellationToken cancellationToken)
    {
        if (_executors.For(action.Kind) is not { } executor)
        {
            return CoordinatorActionResult.Unavailable;
        }

        // An undo is about the command it undoes: its scenario, date and time, and what its action returned.
        var about = command;
        string? undoneReference = null;
        if (action.Step == CoordinatorActionStep.Undo)
        {
            var undone = await _actions.FindAsync(action.OrganizationId, action.UndoesActionId!.Value, cancellationToken)
                ?? throw new PermanentJobFailure("The undone action is gone.");
            undoneReference = undone.ExternalRef;
            about = await _commands.FindAsync(action.OrganizationId, undone.CommandId, cancellationToken)
                ?? throw new PermanentJobFailure("The undone command is gone.");
        }

        var request = new CoordinatorActionRequest(
            action.OrganizationId,
            action.Id,
            action.Kind,
            action.Step,
            about.Scenario,
            about.Date!.Value,
            about.Time,
            action.Step == CoordinatorActionStep.Apply ? command.AnnouncementText : null,
            action.ImageUrl,
            undoneReference,
            settings);
        try
        {
            return action.Step == CoordinatorActionStep.Undo
                ? await executor.UndoAsync(request, cancellationToken)
                : await executor.ExecuteAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                "The {Kind} executor failed on 統籌助理 action {ActionId}: {Failure}",
                WireNames<ShopActionKind>.ToWire(action.Kind), action.Id, ExceptionSummary.Of(exception));
            return CoordinatorActionResult.Failed(CoordinatorFailureCodes.Error);
        }
    }
}
