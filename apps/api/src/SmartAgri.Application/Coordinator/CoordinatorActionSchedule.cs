using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>One <see cref="CoordinatorCommandAction"/> to write: what, how, when, the manual steps and the image.</summary>
public sealed record CoordinatorActionPlan(
    ShopActionKind Kind,
    string? Name,
    CoordinatorActionStep Step,
    ShopActionMode Mode,
    DateTimeOffset DueAt,
    string StepsText,
    string? ImageUrl = null,
    Guid? UndoesActionId = null);

/// <summary>What a confirmed reopen does: the rows of the undone command it drops, and its own actions.</summary>
public sealed record CoordinatorReopenPlan(IReadOnlyList<Guid> Cancel, IReadOnlyList<CoordinatorActionPlan> Actions);

/// <summary>
/// When and how each action of a confirmed command runs (store-coordinator design §3 Q7, Q8, Q20, Q21, §5 Slice 4,
/// #375) — pure, over the shop's settings at confirmation and a given now.
/// </summary>
/// <remarks>
/// <para>
/// LINE 群發, Google 特殊營業時間 and FB 貼文 are applied now (Q20). An ordering system is paused and resumed by
/// reminders <see cref="OrderingLead"/> before the shop opens (Q7): 臨時店休 pauses before the day's first opening and
/// resumes before the first opening of a later day (<see cref="BusinessHours.FirstOpeningAfter"/>, so a regular day
/// off after it and an overnight period of the day itself are skipped); 提早打烊 pauses before the new closing time and
/// resumes like a closure; 延後開店 pauses before the day's first opening and resumes before the new opening time. No
/// time is earlier than now.
/// </para>
/// <para>
/// A reopen (Q21) drops the undone command's rows still waiting (and the failed ones, so they can no longer be
/// retried); takes back what was applied — automatically when it succeeded automatically and its executor is
/// available, else by a reminder with a fixed undo text (a LINE 群發 cannot be taken back); reminds now to resume each
/// ordering system whose pause happened (done, or due already) and whose resume had not come yet; and, when chosen,
/// broadcasts the correction announcement.
/// </para>
/// </remarks>
public static class CoordinatorActionSchedule
{
    /// <summary>How long before the shop opens (or closes early) an ordering system is reminded.</summary>
    public static readonly TimeSpan OrderingLead = TimeSpan.FromMinutes(30);

    /// <summary>When the ordering systems of a command for <paramref name="date"/> are reminded to pause and to
    /// resume, never before <paramref name="now"/>.</summary>
    public static (DateTimeOffset Pause, DateTimeOffset Resume) OrderingTimes(
        ShopSettingsDocument settings, ShopScenario scenario, DateOnly date, TimeOnly? time, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (scenario == ShopScenario.Reopen || CoordinatorCommand.NeedsTime(scenario) != (time is not null))
        {
            throw new ArgumentException("A closure has no time; an early closing or a late opening has one.", nameof(time));
        }

        var hours = settings.Hours;
        // A day the settings no longer open (changed after the card) is paused from its start.
        var opening = hours.FirstOpeningOn(date) ?? hours.At(date, TimeOnly.MinValue);
        var pause = scenario == ShopScenario.CloseEarly ? CoordinatorCommandPlanner.InstantOf(settings, date, time!.Value) : opening;
        var resume = CoordinatorCommandPlanner.ResumesAt(settings, scenario, date, time) ?? hours.At(date.AddDays(1), TimeOnly.MinValue);
        return (Latest(pause - OrderingLead, now), Latest(resume - OrderingLead, now));
    }

    /// <summary>
    /// The actions of a confirmed 臨時店休／提早打烊／延後開店: one per action of the scenario's settings, in their
    /// order (an ordering system twice: pause, resume), with <paramref name="modes"/> (one per action, from
    /// <see cref="ICoordinatorActionModes"/>).
    /// </summary>
    public static IReadOnlyList<CoordinatorActionPlan> Plan(
        ShopSettingsDocument settings, ShopScenario scenario, DateOnly date, TimeOnly? time, IReadOnlyList<ShopActionMode> modes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modes);
        var actions = settings[scenario].Actions;
        if (modes.Count != actions.Count)
        {
            throw new ArgumentException("One mode per action.", nameof(modes));
        }

        var imageUrl = settings[scenario].Announcement?.ImageUrl;
        var (pause, resume) = OrderingTimes(settings, scenario, date, time, now);
        var plans = new List<CoordinatorActionPlan>();
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            if (action.Kind == ShopActionKind.Ordering)
            {
                plans.Add(new CoordinatorActionPlan(
                    ShopActionKind.Ordering, action.Name, CoordinatorActionStep.Pause, ShopActionMode.Reminder, pause, Steps(action.PauseSteps, action)));
                plans.Add(new CoordinatorActionPlan(
                    ShopActionKind.Ordering, action.Name, CoordinatorActionStep.Resume, ShopActionMode.Reminder, resume, Steps(action.ResumeSteps, action)));
            }
            else
            {
                plans.Add(new CoordinatorActionPlan(
                    action.Kind,
                    null,
                    CoordinatorActionStep.Apply,
                    modes[index],
                    now,
                    Steps(action.Steps, action),
                    action.Kind is ShopActionKind.LineBroadcast or ShopActionKind.FacebookPost ? imageUrl : null));
            }
        }

        return plans;
    }

    /// <summary>
    /// The actions <paramref name="rows"/> (a confirmed command's, in position order) were made from, as the settings
    /// held them at its confirmation: each with its effective mode then — so a reopen card describes what the command
    /// really did, whatever the settings say now.
    /// </summary>
    public static IReadOnlyList<ShopAction> ActionsOf(IReadOnlyList<CoordinatorCommandAction> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var actions = new List<ShopAction>();
        foreach (var group in Groups(rows))
        {
            if (group.Kind == ShopActionKind.Ordering)
            {
                actions.Add(new ShopAction(
                    ShopActionKind.Ordering,
                    ShopActionMode.Reminder,
                    Name: group.Name,
                    PauseSteps: group.Rows.FirstOrDefault(row => row.Step == CoordinatorActionStep.Pause)?.StepsText,
                    ResumeSteps: group.Rows.FirstOrDefault(row => row.Step == CoordinatorActionStep.Resume)?.StepsText));
            }
            else if (group.Rows.FirstOrDefault(row => row.Step == CoordinatorActionStep.Apply) is { } applied)
            {
                actions.Add(new ShopAction(group.Kind, applied.Mode, Steps: applied.StepsText));
            }
        }

        return actions;
    }

    /// <summary>
    /// A confirmed reopen of a command for <paramref name="date"/> whose rows are <paramref name="undone"/>.
    /// <paramref name="autoModes"/> is the effective mode now of an automatic action of each kind (its executor's
    /// availability); <paramref name="correction"/> is the toggled correction broadcast, with the reopen
    /// announcement's image.
    /// </summary>
    public static CoordinatorReopenPlan PlanReopen(
        IReadOnlyList<CoordinatorCommandAction> undone,
        ShopScenario undoneScenario,
        DateOnly date,
        TimeOnly? time,
        bool correction,
        string? correctionImageUrl,
        IReadOnlyDictionary<ShopActionKind, ShopActionMode> autoModes,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(undone);
        ArgumentNullException.ThrowIfNull(autoModes);
        var cancel = undone
            .Where(row => row.Status is CoordinatorActionStatus.Scheduled or CoordinatorActionStatus.Failed)
            .Select(row => row.Id)
            .ToList();
        var plans = new List<CoordinatorActionPlan>();
        var corrected = false;
        foreach (var group in Groups(undone))
        {
            switch (group.Kind)
            {
                case ShopActionKind.LineBroadcast:
                    // Sent is sent: only a correction (Q21).
                    if (correction && !corrected && group.Rows.FirstOrDefault(row => row.Step == CoordinatorActionStep.Apply) is { } broadcast)
                    {
                        plans.Add(Correction(broadcast.Mode, broadcast.StepsText));
                        corrected = true;
                    }

                    break;

                case ShopActionKind.GoogleSpecialHours or ShopActionKind.FacebookPost:
                    if (group.Rows.FirstOrDefault(row => row.Step == CoordinatorActionStep.Apply) is not { } applied)
                    {
                        break;
                    }

                    var steps = UndoSteps(group.Kind, undoneScenario, date, time);
                    if (applied.Status == CoordinatorActionStatus.Succeeded)
                    {
                        plans.Add(new CoordinatorActionPlan(
                            group.Kind, null, CoordinatorActionStep.Undo, AutoMode(group.Kind), now, steps, UndoesActionId: applied.Id));
                    }
                    else if (applied.Status is CoordinatorActionStatus.AwaitingManual or CoordinatorActionStatus.DoneManually)
                    {
                        plans.Add(new CoordinatorActionPlan(
                            group.Kind, null, CoordinatorActionStep.Undo, ShopActionMode.Reminder, now, steps, UndoesActionId: applied.Id));
                    }

                    break;

                case ShopActionKind.Ordering:
                    var pause = group.Rows.FirstOrDefault(row => row.Step == CoordinatorActionStep.Pause);
                    var resume = group.Rows.FirstOrDefault(row => row.Step == CoordinatorActionStep.Resume);
                    var paused = pause is not null
                        && (pause.Status is CoordinatorActionStatus.AwaitingManual or CoordinatorActionStatus.DoneManually or CoordinatorActionStatus.Succeeded
                            || (pause.Status == CoordinatorActionStatus.Scheduled && pause.DueAt <= now));
                    var resumeToCome = resume is null || resume.Status == CoordinatorActionStatus.Scheduled;
                    if (paused && resumeToCome)
                    {
                        plans.Add(new CoordinatorActionPlan(
                            ShopActionKind.Ordering,
                            group.Name,
                            CoordinatorActionStep.Resume,
                            ShopActionMode.Reminder,
                            now,
                            resume?.StepsText ?? $"打開 {group.Name} 的接單。",
                            UndoesActionId: pause!.Id));
                    }

                    break;
            }
        }

        if (correction && !corrected)
        {
            plans.Add(Correction(ShopActionMode.Reminder, DefaultCorrectionSteps));
        }

        return new CoordinatorReopenPlan(cancel, plans);

        ShopActionMode AutoMode(ShopActionKind kind) => autoModes.GetValueOrDefault(kind, ShopActionMode.Reminder);

        CoordinatorActionPlan Correction(ShopActionMode broadcastMode, string steps) => new(
            ShopActionKind.LineBroadcast,
            null,
            CoordinatorActionStep.Apply,
            broadcastMode == ShopActionMode.Auto ? AutoMode(ShopActionKind.LineBroadcast) : ShopActionMode.Reminder,
            now,
            steps,
            correctionImageUrl);
    }

    /// <summary>The fixed manual steps of taking back <paramref name="kind"/> (the settings have none, Q21).</summary>
    public static string UndoSteps(ShopActionKind kind, ShopScenario undoneScenario, DateOnly date, TimeOnly? time) => kind switch
    {
        ShopActionKind.GoogleSpecialHours => $"到 Google 商家檔案刪除 {ShopAnnouncements.FormatDate(date)} 的特殊營業時間。",
        ShopActionKind.FacebookPost =>
            $"到 FB 粉專刪除 {ShopAnnouncements.FormatDate(date)} {CoordinatorCommandReplies.Title(undoneScenario, time)}的公告貼文。",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Google and FB are taken back."),
    };

    /// <summary>A correction broadcast's steps when the undone command's LINE 群發 is not known.</summary>
    public const string DefaultCorrectionSteps = "到 LINE 官方帳號管理後台群發這則更正公告。";

    private static DateTimeOffset Latest(DateTimeOffset at, DateTimeOffset now) => at < now ? now : at;

    private static string Steps(string? steps, ShopAction action) =>
        !string.IsNullOrWhiteSpace(steps) ? steps : throw new InvalidOperationException($"The settings' {action.Kind} action has no steps.");

    /// <summary>The rows of each action, in the order of their first position: an action is its kind and name.</summary>
    private static IEnumerable<(ShopActionKind Kind, string? Name, IReadOnlyList<CoordinatorCommandAction> Rows)> Groups(
        IReadOnlyList<CoordinatorCommandAction> rows) =>
        rows.OrderBy(row => row.Position)
            .GroupBy(row => (row.Kind, row.Name))
            .Select(group => (group.Key.Kind, group.Key.Name, (IReadOnlyList<CoordinatorCommandAction>)[.. group]));
}
