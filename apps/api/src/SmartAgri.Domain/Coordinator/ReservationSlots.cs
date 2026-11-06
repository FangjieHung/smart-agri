using System.Globalization;

namespace SmartAgri.Domain.Coordinator;

/// <summary>
/// A 店休 command as the reservation times need it (#389): what <see cref="ReservationSlots"/> reads of a
/// <see cref="CoordinatorCommand"/>.
/// </summary>
public sealed record ShopCommandFact(
    Guid Id, ShopScenario Scenario, CoordinatorCommandStatus Status, DateOnly? Date, TimeOnly? Time, Guid? ReopensCommandId)
{
    public static ShopCommandFact Of(CoordinatorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new ShopCommandFact(command.Id, command.Scenario, command.Status, command.Date, command.Time, command.ReopensCommandId);
    }
}

/// <summary>One time a customer can book: the business day it belongs to, the instant and the shop's clock then.</summary>
/// <param name="Date">The shop's business day: the date its period opens on (Saturday's 00:30 of an 18:00–01:00 night
/// is Saturday's).</param>
/// <param name="At">The instant, at the shop's UTC offset then.</param>
/// <param name="Local">The shop's wall clock at <see cref="At"/>.</param>
public sealed record ReservationSlot(DateOnly Date, DateTimeOffset At, DateTime Local)
{
    /// <summary>The form's value: the wall clock, <c>2026-11-08T00:30</c> — unique within a business day.</summary>
    public string Value => Local.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    /// <summary>After midnight of the business day (the small hours of an overnight period).</summary>
    public bool IsNextDay => DateOnly.FromDateTime(Local) != Date;

    /// <summary>「18:30」, or 「00:30（隔天）」 after midnight.</summary>
    public string Label => ShopAnnouncements.FormatTime(TimeOnly.FromDateTime(Local)) + (IsNextDay ? "（隔天）" : string.Empty);
}

/// <summary>
/// Which times a shop takes reservations for (store-reservations design decision R4, §3.1 point 3, Slice R1, #389) —
/// pure, so the page and the server's check of a submitted form compute exactly the same thing.
/// </summary>
/// <remarks>
/// <para>
/// A business day's periods (<see cref="BusinessHours.PeriodsOn"/>, in the shop's time zone) are cut into times every
/// <see cref="ShopReservationSettings.SlotMinutes"/> by the wall clock from each period's regular opening; a time the
/// clock skips (daylight saving) is the instant after the gap, and the same instant is offered once. Then each time
/// must be:
/// </para>
/// <list type="bullet">
/// <item>inside its period as the shop's confirmed 店休 commands of that day leave it — a 臨時店休 removes the day,
/// a 提早打烊 at T closes every period at T (dropping those opening at or after it), a 延後開店 at T opens every period at
/// T (dropping those closing at or before it), T read as the commands read it (<see cref="BusinessHours.OnBusinessDay"/>).
/// A command a confirmed 恢復營業 undid, and every command not confirmed, changes nothing;</item>
/// <item>at least <see cref="ShopReservationSettings.LastSeatingMinutes"/> before the (new) closing — 21:00 and 60
/// minutes: 20:00 is the last;</item>
/// <item>at least <see cref="ShopReservationSettings.MinLeadMinutes"/> after now;</item>
/// <item>on a business day from yesterday (the small hours of last night's period) to today plus
/// <see cref="ShopReservationSettings.MaxDaysAhead"/>, in the shop's time zone.</item>
/// </list>
/// <para>No limit on how many reservations a time takes (R4): the shop decides each one.</para>
/// </remarks>
public static class ReservationSlots
{
    /// <summary>
    /// The 店休 commands that change the shop's hours: confirmed 臨時店休, 提早打烊 and 延後開店 with a date that no
    /// confirmed 恢復營業 among <paramref name="commands"/> undid.
    /// </summary>
    public static IReadOnlyList<ShopCommandFact> InEffect(IEnumerable<ShopCommandFact> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var all = commands.ToList();
        var undone = all
            .Where(command => command.Scenario == ShopScenario.Reopen && command.Status == CoordinatorCommandStatus.Confirmed)
            .Select(command => command.ReopensCommandId)
            .OfType<Guid>()
            .ToHashSet();
        return
        [
            .. all.Where(command => command.Status == CoordinatorCommandStatus.Confirmed
                && command.Scenario != ShopScenario.Reopen
                && command.Date is not null
                && (command.Time is not null || !CoordinatorCommand.NeedsTime(command.Scenario))
                && !undone.Contains(command.Id)),
        ];
    }

    /// <summary>The business days a customer can choose at <paramref name="now"/>: those of the range with at least one
    /// time, earliest first.</summary>
    public static IReadOnlyList<DateOnly> Dates(ShopSettingsDocument settings, DateTimeOffset now, IEnumerable<ShopCommandFact> commands)
    {
        var (reservations, hours) = Open(settings);
        var inEffect = InEffect(commands);
        var today = hours.DateAt(now);
        var dates = new List<DateOnly>();
        for (var date = today.AddDays(-1); date <= today.AddDays(reservations.MaxDaysAhead); date = date.AddDays(1))
        {
            if (On(settings, reservations, hours, date, now, inEffect).Count > 0)
            {
                dates.Add(date);
            }
        }

        return dates;
    }

    /// <summary>The times of <paramref name="date"/> a customer can book at <paramref name="now"/>, earliest first;
    /// empty for a date outside the range.</summary>
    public static IReadOnlyList<ReservationSlot> On(
        ShopSettingsDocument settings, DateOnly date, DateTimeOffset now, IEnumerable<ShopCommandFact> commands)
    {
        var (reservations, hours) = Open(settings);
        return On(settings, reservations, hours, date, now, InEffect(commands));
    }

    /// <summary>The time of <paramref name="date"/> whose <see cref="ReservationSlot.Value"/> is
    /// <paramref name="value"/>, if it can be booked at <paramref name="now"/>.</summary>
    public static ReservationSlot? Find(
        ShopSettingsDocument settings, DateOnly date, string? value, DateTimeOffset now, IEnumerable<ShopCommandFact> commands) =>
        value is null ? null : On(settings, date, now, commands).FirstOrDefault(slot => slot.Value == value);

    private static (ShopReservationSettings Reservations, BusinessHours Hours) Open(ShopSettingsDocument settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Reservations is { } reservations
            ? (reservations, settings.Hours)
            : throw new ArgumentException("The shop has no reservation settings.", nameof(settings));
    }

    private static List<ReservationSlot> On(
        ShopSettingsDocument settings,
        ShopReservationSettings reservations,
        BusinessHours hours,
        DateOnly date,
        DateTimeOffset now,
        IReadOnlyList<ShopCommandFact> inEffect)
    {
        var today = hours.DateAt(now);
        if (date < today.AddDays(-1) || date > today.AddDays(reservations.MaxDaysAhead))
        {
            return [];
        }

        var changes = inEffect.Where(command => command.Date == date).ToList();
        if (changes.Any(command => command.Scenario == ShopScenario.TemporaryClosure))
        {
            return [];
        }

        var earliest = now.AddMinutes(reservations.MinLeadMinutes);
        var lastSeating = TimeSpan.FromMinutes(reservations.LastSeatingMinutes);
        var slots = new List<ReservationSlot>();
        foreach (var period in hours.PeriodsOn(date))
        {
            if (Changed(hours, date, period, changes) is not { } open)
            {
                continue;
            }

            // The wall clock from the period's regular opening, so a late opening keeps the usual times.
            var wall = TimeZoneInfo.ConvertTime(period.Opens, hours.Zone).DateTime;
            for (var at = period.Opens; at < period.Closes; wall = wall.AddMinutes(reservations.SlotMinutes), at = hours.At(DateOnly.FromDateTime(wall), TimeOnly.FromDateTime(wall)))
            {
                if (at >= open.Opens && at < open.Closes && at + lastSeating <= open.Closes && at >= earliest
                    && !slots.Exists(slot => slot.At == at))
                {
                    slots.Add(new ReservationSlot(date, at, TimeZoneInfo.ConvertTime(at, hours.Zone).DateTime));
                }
            }
        }

        return [.. slots.OrderBy(slot => slot.At)];
    }

    /// <summary><paramref name="period"/> as the day's 提早打烊 and 延後開店 leave it; <see langword="null"/> when nothing is
    /// left of it.</summary>
    private static BusinessInterval? Changed(BusinessHours hours, DateOnly date, BusinessInterval period, IReadOnlyList<ShopCommandFact> changes)
    {
        var (opens, closes) = (period.Opens, period.Closes);
        foreach (var change in changes)
        {
            var at = hours.OnBusinessDay(date, change.Time!.Value);
            if (change.Scenario == ShopScenario.CloseEarly && at < closes)
            {
                closes = at;
            }
            else if (change.Scenario == ShopScenario.OpenLate && at > opens)
            {
                opens = at;
            }
        }

        return opens < closes ? new BusinessInterval(opens, closes) : null;
    }
}
