using System.Text.Json.Nodes;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Infrastructure.Coordinator;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// A bound member's commands (store-coordinator design §3 Q4–Q6, Q13, Q20, Q21, Q23, decision D, §5 Slice 3,
/// #374), carried out for <see cref="CoordinatorEventHandler"/>: each method returns the messages to reply with
/// (at most five), except a confirmation, which hands over to <see cref="ICoordinatorCommandRunner"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A text</b> (not a code, not 「產生店長綁定碼」, not 「改成：」): (1) when the sender has commands waiting for
/// their date or time, it answers them — each with a model call charged to its shop, given the pending
/// scenario and what is known; (2) a member of several shops is asked 「哪一家？」 (with 「全部」 for at most five
/// shops) and the text is held in memory for ten minutes; (3) otherwise the shop's model reads it
/// (<see cref="CoordinatorCommandInterpreter"/>), and the server checks the result against the shop's settings
/// (<see cref="CoordinatorCommandPlanner"/>): not a command → the menu; a missing or refused date or time → an
/// <c>awaiting-details</c> command and a question; a regular day off or a scenario the shop does not offer →
/// said so; otherwise a <c>previewed</c> command and its card. A shop without settings is told so before any
/// model call.
/// </para>
/// <para>
/// <b>Postbacks</b> carry the organization and command ids, and the card version; every one is checked
/// against the sender's current memberships and, again, inside the shop's own context. Only the one who gave a
/// command answers its questions and edits its card; any owner or manager of the shop may confirm or cancel it.
/// </para>
/// </remarks>
public sealed class CoordinatorCommandHandler
{
    private readonly CoordinatorCommands _commands;
    private readonly CoordinatorShops _shops;
    private readonly CoordinatorCommandInterpreter _interpreter;
    private readonly CoordinatorConversationMemory _memory;
    private readonly ICoordinatorActionModes _modes;
    private readonly ICoordinatorCommandRunner _runner;
    private readonly TimeProvider _clock;

    public CoordinatorCommandHandler(
        CoordinatorCommands commands,
        CoordinatorShops shops,
        CoordinatorCommandInterpreter interpreter,
        CoordinatorConversationMemory memory,
        ICoordinatorActionModes modes,
        ICoordinatorCommandRunner runner,
        TimeProvider clock)
    {
        _commands = commands;
        _shops = shops;
        _interpreter = interpreter;
        _memory = memory;
        _modes = modes;
        _runner = runner;
        _clock = clock;
    }

    // --- Text ------------------------------------------------------------------------------------------

    /// <summary>A member's text: an answer to waiting questions, a shop choice to make, or a new command.</summary>
    public async Task<IReadOnlyList<JsonObject>> TextAsync(
        string lineUserId, IReadOnlyList<CoordinatorMembership> memberships, string text, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var waiting = new List<(CoordinatorMembership Membership, CoordinatorCommand Command)>();
        foreach (var membership in memberships)
        {
            foreach (var command in await _commands.AwaitingAsync(membership.OrganizationId, lineUserId, now, cancellationToken))
            {
                waiting.Add((membership, command));
            }
        }

        if (waiting.Count > 0)
        {
            var several = waiting.Count > 1;
            var answers = new List<JsonObject>();
            foreach (var (membership, command) in waiting.OrderByDescending(pair => pair.Command.ExpiresAt).Take(LineMessages.MaxPerRequest))
            {
                answers.Add(await AnswerAsync(membership, command, lineUserId, text, now, several, cancellationToken));
            }

            return answers;
        }

        if (memberships.Count > 1)
        {
            _memory.Hold(lineUserId, text);
            return [CoordinatorCommandReplies.ShopChoice([.. memberships.Select(membership => (membership.OrganizationId, membership.StoreName))])];
        }

        return [await NewCommandAsync(memberships[0], lineUserId, text, now, forShop: false, cancellationToken)];
    }

    /// <summary>The shop (or 「全部」) for the held command.</summary>
    public async Task<IReadOnlyList<JsonObject>> ChooseShopAsync(
        string lineUserId, IReadOnlyList<CoordinatorMembership> memberships, Guid? organizationId, CancellationToken cancellationToken)
    {
        var text = _memory.Take(lineUserId);
        if (text is null)
        {
            return [LineMessages.Text(CoordinatorCommandReplies.RetypeCommand)];
        }

        var now = _clock.GetUtcNow();
        if (organizationId is { } chosen)
        {
            return memberships.FirstOrDefault(membership => membership.OrganizationId == chosen) is { } membership
                ? [await NewCommandAsync(membership, lineUserId, text, now, forShop: false, cancellationToken)]
                : [LineMessages.Text(CoordinatorCommandReplies.NotAMember)];
        }

        if (memberships.Count > CoordinatorCommandReplies.AllShopsMax)
        {
            return [LineMessages.Text(CoordinatorCommandReplies.RetypeCommand)];
        }

        var replies = new List<JsonObject>();
        foreach (var membership in memberships)
        {
            replies.Add(await NewCommandAsync(membership, lineUserId, text, now, forShop: true, cancellationToken));
        }

        return replies;
    }

    /// <summary>「改成：…」: the new announcement of the sender's latest open card, and the card again.</summary>
    public async Task<IReadOnlyList<JsonObject>> EditAsync(
        string lineUserId, IReadOnlyList<CoordinatorMembership> memberships, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [LineMessages.Text(CoordinatorCommandReplies.EmptyEdit)];
        }

        if (text.Length > CoordinatorCommand.AnnouncementMaxLength)
        {
            return [LineMessages.Text($"公告最多 {CoordinatorCommand.AnnouncementMaxLength} 字，請縮短後再傳。")];
        }

        var now = _clock.GetUtcNow();
        (CoordinatorMembership Membership, CoordinatorCommand Command)? latest = null;
        foreach (var membership in memberships)
        {
            if (await _commands.LatestPreviewAsync(membership.OrganizationId, lineUserId, now, cancellationToken) is { } command
                && (latest is null || command.ExpiresAt > latest.Value.Command.ExpiresAt))
            {
                latest = (membership, command);
            }
        }

        if (latest is not { } found)
        {
            return [LineMessages.Text(CoordinatorCommandReplies.NoPreviewToEdit)];
        }

        if (found.Command.AnnouncementText is null || (found.Command.Scenario == ShopScenario.Reopen && !found.Command.CorrectionBroadcast))
        {
            return [LineMessages.Text(CoordinatorCommandReplies.NoAnnouncementToEdit)];
        }

        var organizationId = found.Membership.OrganizationId;
        if (!await _commands.EditAsync(organizationId, found.Command.Id, found.Command.Version, text, now, cancellationToken))
        {
            return [LineMessages.Text(CoordinatorCommandReplies.Outdated)];
        }

        return [await CardAsync(organizationId, found.Command.Id, cancellationToken)];
    }

    // --- Postbacks -------------------------------------------------------------------------------------

    /// <summary>A date for a waiting command (「今天」, 「明天」 or the picker; <see langword="null"/>: the picker sent
    /// nothing usable, ask again).</summary>
    public Task<IReadOnlyList<JsonObject>> FillDateAsync(
        string lineUserId, IReadOnlyList<CoordinatorMembership> memberships, Guid organizationId, Guid commandId, DateOnly? date,
        CancellationToken cancellationToken) =>
        FillAsync(lineUserId, memberships, organizationId, commandId, command => (date, command.Time), cancellationToken);

    /// <summary>A time for a waiting command (the picker).</summary>
    public Task<IReadOnlyList<JsonObject>> FillTimeAsync(
        string lineUserId, IReadOnlyList<CoordinatorMembership> memberships, Guid organizationId, Guid commandId, TimeOnly? time,
        CancellationToken cancellationToken) =>
        FillAsync(lineUserId, memberships, organizationId, commandId, command => (command.Date, time), cancellationToken);

    /// <summary>〔確認〕: confirms the card once and hands over to <see cref="ICoordinatorCommandRunner"/> (which
    /// replies); otherwise the reason it was refused.</summary>
    public async Task<IReadOnlyList<JsonObject>> ConfirmAsync(
        string lineUserId, IReadOnlyList<CoordinatorMembership> memberships, Guid organizationId, Guid commandId, int version,
        string? replyToken, CancellationToken cancellationToken)
    {
        if (memberships.FirstOrDefault(membership => membership.OrganizationId == organizationId) is not { } membership)
        {
            return [LineMessages.Text(CoordinatorCommandReplies.NotAMember)];
        }

        var outcome = await _commands.ConfirmAsync(organizationId, commandId, version, lineUserId, _clock.GetUtcNow(), cancellationToken);
        if (outcome != CoordinatorCardOutcome.Done)
        {
            return [LineMessages.Text(Refusal(outcome))];
        }

        var command = await _commands.FindAsync(organizationId, commandId, cancellationToken)
            ?? throw new InvalidOperationException("A command just confirmed is gone.");
        await _runner.StartAsync(new ConfirmedCoordinatorCommand(command, membership.StoreName), replyToken, cancellationToken);
        return [];
    }

    /// <summary>〔取消〕.</summary>
    public async Task<IReadOnlyList<JsonObject>> CancelAsync(
        string lineUserId, IReadOnlyList<CoordinatorMembership> memberships, Guid organizationId, Guid commandId, int version,
        CancellationToken cancellationToken)
    {
        if (memberships.All(membership => membership.OrganizationId != organizationId))
        {
            return [LineMessages.Text(CoordinatorCommandReplies.NotAMember)];
        }

        var outcome = await _commands.CancelAsync(organizationId, commandId, version, lineUserId, _clock.GetUtcNow(), cancellationToken);
        return [LineMessages.Text(outcome == CoordinatorCardOutcome.Done ? CoordinatorCommandReplies.Cancelled : Refusal(outcome))];
    }

    /// <summary>「加發更正公告」 on a reopen card: flips it and sends the new version of the card.</summary>
    public async Task<IReadOnlyList<JsonObject>> ToggleCorrectionAsync(
        string lineUserId, IReadOnlyList<CoordinatorMembership> memberships, Guid organizationId, Guid commandId, int version,
        CancellationToken cancellationToken)
    {
        if (memberships.All(membership => membership.OrganizationId != organizationId))
        {
            return [LineMessages.Text(CoordinatorCommandReplies.NotAMember)];
        }

        var now = _clock.GetUtcNow();
        if (await _commands.FindAsync(organizationId, commandId, cancellationToken) is { Scenario: ShopScenario.Reopen } reopen
            && await _shops.FindAsync(organizationId, cancellationToken) is { } settings
            && await _commands.FindAsync(organizationId, reopen.ReopensCommandId!.Value, cancellationToken) is { } undone
            && !CorrectionOffered(reopen, settings, undone))
        {
            // Only a card that shows the toggle can be toggled (the postback is the sender's to forge).
            return [LineMessages.Text(CoordinatorCommandReplies.Outdated)];
        }

        if (!await _commands.ToggleCorrectionAsync(organizationId, commandId, version, lineUserId, now, cancellationToken))
        {
            var outcome = await _commands.WhyNotAsync(organizationId, commandId, version, lineUserId, now, cancellationToken);
            return [LineMessages.Text(Refusal(outcome))];
        }

        return [await CardAsync(organizationId, commandId, cancellationToken)];
    }

    /// <summary>The confirmed command to reopen, picked from several.</summary>
    public async Task<IReadOnlyList<JsonObject>> ChooseReopenAsync(
        string lineUserId, IReadOnlyList<CoordinatorMembership> memberships, Guid organizationId, Guid commandId,
        CancellationToken cancellationToken)
    {
        if (memberships.FirstOrDefault(membership => membership.OrganizationId == organizationId) is not { } membership)
        {
            return [LineMessages.Text(CoordinatorCommandReplies.NotAMember)];
        }

        if (await _shops.FindAsync(organizationId, cancellationToken) is not { } settings)
        {
            return [LineMessages.Text(CoordinatorCommandReplies.NotConfigured)];
        }

        var now = _clock.GetUtcNow();
        if (!settings[ShopScenario.Reopen].Enabled)
        {
            return [LineMessages.Text(CoordinatorCommandReplies.ScenarioDisabled(ShopScenario.Reopen))];
        }

        var candidates = await _commands.ReopenCandidatesAsync(organizationId, CoordinatorCommandPlanner.Today(settings, now), cancellationToken);
        return candidates.FirstOrDefault(candidate => candidate.Id == commandId) is { } original
            ? [await CreateReopenAsync(membership, settings, lineUserId, original, reason: null, now, cancellationToken)]
            : [LineMessages.Text(CoordinatorCommandReplies.NothingToReopen)];
    }

    // --- Flow ------------------------------------------------------------------------------------------

    /// <summary>A new command of <paramref name="membership"/>'s shop: its settings, the model, then the plan.</summary>
    private async Task<JsonObject> NewCommandAsync(
        CoordinatorMembership membership, string lineUserId, string text, DateTimeOffset now, bool forShop, CancellationToken cancellationToken)
    {
        if (await _shops.FindAsync(membership.OrganizationId, cancellationToken) is not { } settings)
        {
            return Say(membership, CoordinatorCommandReplies.NotConfigured, forShop);
        }

        var understood = await _interpreter.InterpretAsync(
            membership.OrganizationId, CoordinatorCommandTool.Prompt(settings, now, text), cancellationToken);
        if (Failure(understood) is { } failure)
        {
            return Say(membership, failure, forShop);
        }

        return await ProceedAsync(membership, settings, lineUserId, understood.Call, pending: null, now, forShop, cancellationToken);
    }

    /// <summary>A text answering <paramref name="command"/>'s question: the model again, told what is pending.</summary>
    private async Task<JsonObject> AnswerAsync(
        CoordinatorMembership membership, CoordinatorCommand command, string lineUserId, string text, DateTimeOffset now, bool forShop,
        CancellationToken cancellationToken)
    {
        if (await _shops.FindAsync(membership.OrganizationId, cancellationToken) is not { } settings)
        {
            await DropAsync(command, cancellationToken);
            return Say(membership, CoordinatorCommandReplies.NotConfigured, forShop);
        }

        var pending = new CoordinatorPendingDetails(command.Scenario, command.Date, command.Time, _memory.QuestionOf(command.Id));
        var understood = await _interpreter.InterpretAsync(
            membership.OrganizationId, CoordinatorCommandTool.FollowUpPrompt(settings, now, pending, text), cancellationToken);
        if (Failure(understood) is { } failure)
        {
            return Say(membership, failure, forShop);
        }

        return await ProceedAsync(membership, settings, lineUserId, understood.Call, command, now, forShop, cancellationToken);
    }

    /// <summary>Applies what the model said, merged with <paramref name="pending"/>'s known details when it is
    /// the same scenario (another scenario drops the pending command and starts a new one).</summary>
    private async Task<JsonObject> ProceedAsync(
        CoordinatorMembership membership,
        ShopSettingsDocument settings,
        string lineUserId,
        CoordinatorCommandCall call,
        CoordinatorCommand? pending,
        DateTimeOffset now,
        bool forShop,
        CancellationToken cancellationToken)
    {
        if (call.Scenario is not { } scenario)
        {
            if (pending is not null)
            {
                await DropAsync(pending, cancellationToken);
                return Say(membership, $"{CoordinatorCommandReplies.PendingDropped}\n{CoordinatorReplies.Menu}", forShop);
            }

            return Say(membership, CoordinatorReplies.Menu, forShop);
        }

        if (pending is not null && pending.Scenario != scenario)
        {
            await DropAsync(pending, cancellationToken);
            pending = null;
        }

        if (scenario == ShopScenario.Reopen)
        {
            return await ReopenAsync(membership, settings, lineUserId, call.Date, call.Reason, now, forShop, cancellationToken);
        }

        var date = call.Date ?? pending?.Date;
        var time = call.Time ?? pending?.Time;
        var reason = call.Reason ?? (pending is null ? null : _memory.ReasonOf(pending.Id));
        var plan = CoordinatorCommandPlanner.Plan(settings, now, scenario, date, time, reason, call.Question);
        return await ApplyAsync(membership, settings, lineUserId, plan, reason, pending, now, forShop, cancellationToken);
    }

    /// <summary>A date or time from a question's buttons, checked like any other.</summary>
    private async Task<IReadOnlyList<JsonObject>> FillAsync(
        string lineUserId,
        IReadOnlyList<CoordinatorMembership> memberships,
        Guid organizationId,
        Guid commandId,
        Func<CoordinatorCommand, (DateOnly? Date, TimeOnly? Time)> details,
        CancellationToken cancellationToken)
    {
        if (memberships.FirstOrDefault(membership => membership.OrganizationId == organizationId) is not { } membership)
        {
            return [LineMessages.Text(CoordinatorCommandReplies.NotAMember)];
        }

        var now = _clock.GetUtcNow();
        var command = await _commands.FindAsync(organizationId, commandId, cancellationToken);
        if (command is null || command.Status == CoordinatorCommandStatus.Cancelled || (command.Status == CoordinatorCommandStatus.AwaitingDetails && command.IsExpired(now)))
        {
            return [LineMessages.Text(CoordinatorCommandReplies.Expired)];
        }

        if (command.RequestedByLineUserId != lineUserId)
        {
            return [LineMessages.Text(CoordinatorCommandReplies.NotAMember)];
        }

        if (command.Status != CoordinatorCommandStatus.AwaitingDetails)
        {
            return [LineMessages.Text(CoordinatorCommandReplies.AlreadyAnswered)];
        }

        if (await _shops.FindAsync(organizationId, cancellationToken) is not { } settings)
        {
            await DropAsync(command, cancellationToken);
            return [LineMessages.Text(CoordinatorCommandReplies.NotConfigured)];
        }

        var (date, time) = details(command);
        var reason = _memory.ReasonOf(command.Id);
        var plan = CoordinatorCommandPlanner.Plan(settings, now, command.Scenario, date, time, reason);
        return [await ApplyAsync(membership, settings, lineUserId, plan, reason, command, now, forShop: false, cancellationToken)];
    }

    /// <summary>Carries a plan out: a question (creating or updating the waiting command), a card (creating it,
    /// or turning the waiting command into it), or an explanation (dropping the waiting command).</summary>
    private async Task<JsonObject> ApplyAsync(
        CoordinatorMembership membership,
        ShopSettingsDocument settings,
        string lineUserId,
        CoordinatorPlan plan,
        string? reason,
        CoordinatorCommand? pending,
        DateTimeOffset now,
        bool forShop,
        CancellationToken cancellationToken)
    {
        var organizationId = membership.OrganizationId;
        switch (plan.Kind)
        {
            case CoordinatorPlanKind.AskDate:
            case CoordinatorPlanKind.AskTime:
                Guid commandId;
                if (pending is not null)
                {
                    if (!await _commands.UpdateAwaitingAsync(organizationId, pending.Id, plan.Date, plan.Time, now, cancellationToken))
                    {
                        return Say(membership, CoordinatorCommandReplies.Expired, forShop);
                    }

                    commandId = pending.Id;
                }
                else
                {
                    var command = CoordinatorCommand.AwaitDetails(organizationId, lineUserId, plan.Scenario, plan.Date, plan.Time, now);
                    await _commands.AddAsync(command, cancellationToken);
                    commandId = command.Id;
                }

                _memory.RememberPending(commandId, reason, plan.Message, now + CoordinatorCommand.CardValidity);
                var question = forShop ? CoordinatorCommandReplies.ForShop(membership.StoreName, plan.Message!) : plan.Message!;
                return plan.Kind == CoordinatorPlanKind.AskDate
                    ? CoordinatorCommandReplies.DateQuestion(
                        question, organizationId, commandId, CoordinatorCommandPlanner.Today(settings, now), CoordinatorCommandPlanner.LastDate(settings, now))
                    : CoordinatorCommandReplies.TimeQuestion(question, organizationId, commandId);

            case CoordinatorPlanKind.Preview:
                Guid previewId;
                if (pending is not null)
                {
                    if (!await _commands.PreviewAwaitingAsync(organizationId, pending.Id, plan.Date!.Value, plan.Time, plan.Announcement!, now, cancellationToken))
                    {
                        return Say(membership, CoordinatorCommandReplies.Expired, forShop);
                    }

                    previewId = pending.Id;
                }
                else
                {
                    var command = CoordinatorCommand.Preview(organizationId, lineUserId, plan.Scenario, plan.Date!.Value, plan.Time, plan.Announcement, now);
                    await _commands.AddAsync(command, cancellationToken);
                    previewId = command.Id;
                }

                _memory.Forget(previewId);
                return await CardAsync(organizationId, previewId, cancellationToken);

            default:
                if (pending is not null)
                {
                    await DropAsync(pending, cancellationToken);
                }

                return Say(membership, plan.Message!, forShop);
        }
    }

    /// <summary>「恢復營業」: what it can undo — one candidate (on <paramref name="date"/> when given) is a card,
    /// several a choice, none said so.</summary>
    private async Task<JsonObject> ReopenAsync(
        CoordinatorMembership membership,
        ShopSettingsDocument settings,
        string lineUserId,
        DateOnly? date,
        string? reason,
        DateTimeOffset now,
        bool forShop,
        CancellationToken cancellationToken)
    {
        if (!settings[ShopScenario.Reopen].Enabled)
        {
            return Say(membership, CoordinatorCommandReplies.ScenarioDisabled(ShopScenario.Reopen), forShop);
        }

        var candidates = await _commands.ReopenCandidatesAsync(membership.OrganizationId, CoordinatorCommandPlanner.Today(settings, now), cancellationToken);
        if (date is { } day)
        {
            candidates = [.. candidates.Where(candidate => candidate.Date == day)];
        }

        switch (candidates.Count)
        {
            case 0:
                return Say(membership, CoordinatorCommandReplies.NothingToReopen, forShop);
            case 1:
                return await CreateReopenAsync(membership, settings, lineUserId, candidates[0], reason, now, cancellationToken);
            default:
                var choice = CoordinatorCommandReplies.ReopenChoice(
                    membership.OrganizationId,
                    [.. candidates.Select(candidate => (candidate.Id, candidate.Scenario, candidate.Date!.Value, candidate.Time))]);
                if (forShop)
                {
                    choice["text"] = CoordinatorCommandReplies.ForShop(membership.StoreName, CoordinatorCommandReplies.ChooseReopen);
                }

                return choice;
        }
    }

    private async Task<JsonObject> CreateReopenAsync(
        CoordinatorMembership membership,
        ShopSettingsDocument settings,
        string lineUserId,
        CoordinatorCommand original,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var date = original.Date!.Value;
        var command = CoordinatorCommand.Preview(
            membership.OrganizationId,
            lineUserId,
            ShopScenario.Reopen,
            date,
            time: null,
            CoordinatorCommandPlanner.ReopenAnnouncement(settings, date, reason),
            now,
            reopensCommandId: original.Id);
        await _commands.AddAsync(command, cancellationToken);
        return await CardAsync(membership.OrganizationId, command.Id, cancellationToken);
    }

    /// <summary>The current card of command <paramref name="commandId"/> (its current version), with the shop's
    /// current settings and modes.</summary>
    private async Task<JsonObject> CardAsync(Guid organizationId, Guid commandId, CancellationToken cancellationToken)
    {
        var command = await _commands.FindAsync(organizationId, commandId, cancellationToken)
            ?? throw new InvalidOperationException("A command just written is gone.");
        var settings = await _shops.FindAsync(organizationId, cancellationToken)
            ?? throw new InvalidOperationException("A shop with a command has no settings.");
        var date = command.Date!.Value;
        if (command.Scenario != ShopScenario.Reopen)
        {
            var modes = await ModesAsync(organizationId, settings[command.Scenario].Actions, cancellationToken);
            return CoordinatorPreviewCard.Build(new CoordinatorCardContent(
                organizationId, command.Id, command.Version, settings.StoreName, command.Scenario, date, command.Time,
                CoordinatorPreviewCard.Rows(settings, command.Scenario, date, command.Time, modes),
                command.AnnouncementText));
        }

        var original = await _commands.FindAsync(organizationId, command.ReopensCommandId!.Value, cancellationToken)
            ?? throw new InvalidOperationException("A reopen's command is gone.");
        var undoneActions = settings[original.Scenario].Actions;
        var correctionOffered = CorrectionOffered(command, settings, original);
        var correction = correctionOffered && command.CorrectionBroadcast;
        var undoneModes = await ModesAsync(organizationId, undoneActions, cancellationToken);
        return CoordinatorPreviewCard.Build(new CoordinatorCardContent(
            organizationId, command.Id, command.Version, settings.StoreName, ShopScenario.Reopen, date, null,
            CoordinatorPreviewCard.ReopenRows(settings, original.Scenario, date, undoneModes, correction),
            command.AnnouncementText,
            new CoordinatorReopenDetails(original.Scenario, original.Date!.Value, original.Time, correctionOffered, correction)));
    }

    /// <summary>Whether reopen card <paramref name="reopen"/> offers the correction broadcast (Q21): it has the
    /// shop's reopen announcement and the command it undoes had a LINE 群發.</summary>
    private static bool CorrectionOffered(CoordinatorCommand reopen, ShopSettingsDocument settings, CoordinatorCommand undone) =>
        reopen.AnnouncementText is not null
        && settings[undone.Scenario].Actions.Any(action => action.Kind == ShopActionKind.LineBroadcast);

    private async Task<IReadOnlyList<ShopActionMode>> ModesAsync(Guid organizationId, IReadOnlyList<ShopAction> actions, CancellationToken cancellationToken)
    {
        var modes = new List<ShopActionMode>(actions.Count);
        foreach (var action in actions)
        {
            modes.Add(await _modes.ModeOfAsync(organizationId, action, cancellationToken));
        }

        return modes;
    }

    private async Task DropAsync(CoordinatorCommand command, CancellationToken cancellationToken)
    {
        await _commands.CancelAwaitingAsync(command.OrganizationId, command.Id, cancellationToken);
        _memory.Forget(command.Id);
    }

    private static string? Failure(CoordinatorInterpretation understood) => understood.Outcome switch
    {
        CoordinatorInterpretationOutcome.QuotaExceeded => CoordinatorCommandReplies.QuotaExceeded,
        CoordinatorInterpretationOutcome.Failed => CoordinatorCommandReplies.ModelUnavailable,
        _ => null,
    };

    private static string Refusal(CoordinatorCardOutcome outcome) => outcome switch
    {
        CoordinatorCardOutcome.NotAMember => CoordinatorCommandReplies.NotAMember,
        CoordinatorCardOutcome.AlreadyConfirmed => CoordinatorCommandReplies.AlreadyConfirmed,
        CoordinatorCardOutcome.AlreadyCancelled => CoordinatorCommandReplies.AlreadyCancelled,
        CoordinatorCardOutcome.Outdated => CoordinatorCommandReplies.Outdated,
        CoordinatorCardOutcome.AlreadyReopened => CoordinatorCommandReplies.AlreadyReopened,
        _ => CoordinatorCommandReplies.Expired,
    };

    private static JsonObject Say(CoordinatorMembership membership, string text, bool forShop) =>
        LineMessages.Text(forShop ? CoordinatorCommandReplies.ForShop(membership.StoreName, text) : text);
}
