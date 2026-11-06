using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>What <see cref="CoordinatorCommandPlanner.Plan"/> decided.</summary>
public enum CoordinatorPlanKind
{
    /// <summary>Everything checks out: show the preview card (<see cref="CoordinatorPlan.Announcement"/>).</summary>
    Preview,

    /// <summary>Ask for the date (missing, or not allowed: <see cref="CoordinatorPlan.Message"/> says why).</summary>
    AskDate,

    /// <summary>Ask for the time (missing, outside the day's hours, or already past).</summary>
    AskTime,

    /// <summary>The date is a regular day off: nothing to close or change; no card.</summary>
    DayOff,

    /// <summary>The shop does not offer the scenario.</summary>
    Disabled,
}

/// <summary>
/// One decision: the scenario and what is known of the date and time (a value found not allowed is dropped,
/// so the command asks for it again), the text to send (a question or an explanation; none for a preview),
/// and, for a preview, the rendered announcement.
/// </summary>
public sealed record CoordinatorPlan(
    CoordinatorPlanKind Kind,
    ShopScenario Scenario,
    DateOnly? Date,
    TimeOnly? Time,
    string? Message,
    string? Announcement = null)
{
    /// <summary>No text: the announcement may hold a reason a person wrote.</summary>
    public override string ToString() => $"{nameof(CoordinatorPlan)} {{ Kind = {Kind}, Scenario = {Scenario}, Date = {Date}, Time = {Time} }}";
}

/// <summary>
/// The server's checks of a command against the shop's settings (store-coordinator design §3 Q6/Q20, §5
/// Slice 3, #374) — pure, so the model's reading of a message is never trusted. In order: the scenario is
/// offered; there is a date, from today to today + <see cref="CoordinatorCommand.MaxDaysAhead"/> in the shop's
/// zone; it is not a regular day off; for 提早打烊／延後開店 there is a time after the day's first opening and
/// before its last closing (an overnight period's small hours count) that has not passed. Then the
/// announcement is rendered from the template (<see cref="ShopAnnouncements.Render"/>).
/// </summary>
/// <remarks>Reopen is not planned here: what it undoes comes from the shop's confirmed commands.</remarks>
public static class CoordinatorCommandPlanner
{
    /// <summary>The shop's local date at <paramref name="now"/>.</summary>
    public static DateOnly Today(ShopSettingsDocument settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Hours.DateAt(now);
    }

    /// <summary>The latest date a command may be for, seen at <paramref name="now"/>.</summary>
    public static DateOnly LastDate(ShopSettingsDocument settings, DateTimeOffset now) =>
        Today(settings, now).AddDays(CoordinatorCommand.MaxDaysAhead);

    /// <param name="question">The model's question, used when it asks for exactly what is missing.</param>
    public static CoordinatorPlan Plan(
        ShopSettingsDocument settings,
        DateTimeOffset now,
        ShopScenario scenario,
        DateOnly? date,
        TimeOnly? time,
        string? reason,
        string? question = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (scenario == ShopScenario.Reopen)
        {
            throw new ArgumentException("A reopen is about a confirmed command, not planned from a date.", nameof(scenario));
        }

        time = CoordinatorCommand.NeedsTime(scenario) ? time : null;
        if (!settings[scenario].Enabled)
        {
            return new CoordinatorPlan(CoordinatorPlanKind.Disabled, scenario, date, time, CoordinatorCommandReplies.ScenarioDisabled(scenario));
        }

        var hours = settings.Hours;
        var today = hours.DateAt(now);
        var last = today.AddDays(CoordinatorCommand.MaxDaysAhead);
        if (date is not { } day)
        {
            return new CoordinatorPlan(CoordinatorPlanKind.AskDate, scenario, null, time, question ?? CoordinatorCommandReplies.AskDate(scenario));
        }

        if (day < today)
        {
            return new CoordinatorPlan(CoordinatorPlanKind.AskDate, scenario, null, time, CoordinatorCommandReplies.DatePassed(day, scenario));
        }

        if (day > last)
        {
            return new CoordinatorPlan(CoordinatorPlanKind.AskDate, scenario, null, time, CoordinatorCommandReplies.DateTooFar(last, scenario));
        }

        var periods = hours.PeriodsOn(day);
        if (periods.Count == 0)
        {
            return new CoordinatorPlan(CoordinatorPlanKind.DayOff, scenario, day, time, CoordinatorCommandReplies.DayOff(day));
        }

        if (CoordinatorCommand.NeedsTime(scenario))
        {
            if (time is not { } clock)
            {
                return new CoordinatorPlan(CoordinatorPlanKind.AskTime, scenario, day, null, question ?? CoordinatorCommandReplies.AskTime(scenario));
            }

            var firstOpening = periods[0].Opens;
            var lastClosing = periods.Max(period => period.Closes);
            var instant = InstantOn(hours, day, clock, firstOpening, lastClosing);
            if (instant <= firstOpening || instant >= lastClosing)
            {
                return new CoordinatorPlan(
                    CoordinatorPlanKind.AskTime, scenario, day, null,
                    CoordinatorCommandReplies.TimeOutsideHours(scenario, day, settings.WeeklyHours[day.DayOfWeek]));
            }

            if (instant <= now)
            {
                return new CoordinatorPlan(CoordinatorPlanKind.AskTime, scenario, day, null, CoordinatorCommandReplies.TimePassed(scenario, day, clock));
            }
        }

        var values = new AnnouncementValues(
            day,
            time,
            scenario == ShopScenario.TemporaryClosure ? hours.FirstOpeningAfter(day) : null,
            reason);
        return new CoordinatorPlan(
            CoordinatorPlanKind.Preview, scenario, day, time, null, Fit(ShopAnnouncements.Render(settings, scenario, values)));
    }

    /// <summary>The correction announcement of a reopen of a command for <paramref name="date"/>, or
    /// <see langword="null"/> when the shop has no reopen announcement.</summary>
    public static string? ReopenAnnouncement(ShopSettingsDocument settings, DateOnly date, string? reason)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings[ShopScenario.Reopen].Announcement is null
            ? null
            : Fit(ShopAnnouncements.Render(settings, ShopScenario.Reopen, new AnnouncementValues(date, Reason: reason)));
    }

    /// <summary>When the shop opens again after a command for <paramref name="date"/> (<paramref name="time"/>
    /// for 延後開店): the next day's first opening after a closure or an early closing, the new opening time
    /// on the day itself after a late opening.</summary>
    public static DateTimeOffset? ResumesAt(ShopSettingsDocument settings, ShopScenario scenario, DateOnly date, TimeOnly? time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return scenario == ShopScenario.OpenLate && time is { } opens ? InstantOf(settings, date, opens) : settings.Hours.FirstOpeningAfter(date);
    }

    /// <summary>The instant of a command's <paramref name="time"/> on <paramref name="date"/> (提早打烊's closing,
    /// 延後開店's opening): on the day itself, or on the next day when that is the small hours of the day's
    /// overnight period — as the checks of <see cref="Plan"/> read it.</summary>
    public static DateTimeOffset InstantOf(ShopSettingsDocument settings, DateOnly date, TimeOnly time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var hours = settings.Hours;
        var periods = hours.PeriodsOn(date);
        return periods.Count == 0
            ? hours.At(date, time)
            : InstantOn(hours, date, time, periods[0].Opens, periods.Max(period => period.Closes));
    }

    /// <summary><paramref name="clock"/> on <paramref name="day"/>, or on the next day when that is the small
    /// hours of the day's overnight period.</summary>
    private static DateTimeOffset InstantOn(BusinessHours hours, DateOnly day, TimeOnly clock, DateTimeOffset firstOpening, DateTimeOffset lastClosing)
    {
        var instant = hours.At(day, clock);
        if (instant <= firstOpening && hours.At(day.AddDays(1), clock) is var nextDay && nextDay < lastClosing)
        {
            return nextDay;
        }

        return instant;
    }

    /// <summary>A rendered announcement cut to <see cref="CoordinatorCommand.AnnouncementMaxLength"/> (only a
    /// long template with long values can exceed it).</summary>
    private static string Fit(string text)
    {
        var max = CoordinatorCommand.AnnouncementMaxLength;
        return text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];
    }
}
