using System.Globalization;

namespace SmartAgri.Domain.Coordinator;

/// <summary>
/// A shop's settings for the 統籌助理 (store-coordinator design §3 Q6/Q7/Q13/Q14/Q18/Q22, §5 Slice 2,
/// #373): the store name shown to people, the time zone its days and times are in, its weekly business
/// hours and, per <see cref="ShopScenario"/>, whether it is offered, its announcement and its actions.
/// Read from and written as the settings file by <see cref="ShopSettingsJson"/>, which also validates
/// it; stored as <see cref="ShopSettings"/>.
/// </summary>
/// <param name="TimeZoneId">An IANA time zone id (<see cref="ShopSettings.DefaultTimeZoneId"/> unless set).</param>
/// <param name="Scenarios">Exactly the four scenarios.</param>
public sealed record ShopSettingsDocument(
    string StoreName,
    string TimeZoneId,
    WeeklyHours WeeklyHours,
    IReadOnlyDictionary<ShopScenario, ShopScenarioSettings> Scenarios)
{
    /// <summary>The time zone of <see cref="TimeZoneId"/>.</summary>
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>The business-hours calculations over <see cref="WeeklyHours"/> in <see cref="TimeZone"/>.</summary>
    public BusinessHours Hours => new(WeeklyHours, TimeZone);

    /// <summary>The settings of <paramref name="scenario"/>.</summary>
    public ShopScenarioSettings this[ShopScenario scenario] => Scenarios[scenario];
}

/// <summary>One scenario's settings.</summary>
/// <param name="Enabled">Whether the shop offers the scenario.</param>
/// <param name="Announcement">The announcement; always set for an enabled scenario other than
/// <see cref="ShopScenario.Reopen"/>, whose announcement is the optional correction broadcast.</param>
/// <param name="Actions">What the scenario does, in order; empty for <see cref="ShopScenario.Reopen"/>
/// (its actions follow from the command it undoes) and for a disabled scenario that lists none.</param>
public sealed record ShopScenarioSettings(bool Enabled, ShopAnnouncement? Announcement, IReadOnlyList<ShopAction> Actions);

/// <summary>A scenario's announcement: a template (<see cref="ShopAnnouncements"/>) and an optional image.</summary>
/// <param name="ImageUrl">An absolute <c>https</c> URL of a public image (decision B); without one the
/// announcement is text only.</param>
public sealed record ShopAnnouncement(string Template, string? ImageUrl);

/// <summary>
/// One action of a scenario. <see cref="ShopActionKind.LineBroadcast"/>,
/// <see cref="ShopActionKind.GoogleSpecialHours"/> and <see cref="ShopActionKind.FacebookPost"/> have
/// <see cref="Steps"/> — what the owner does by hand when the action is a reminder or cannot run
/// automatically (Q18). <see cref="ShopActionKind.Ordering"/> is always a reminder and has a
/// <see cref="Name"/> (e.g. Xurf), <see cref="PauseSteps"/> and <see cref="ResumeSteps"/>.
/// </summary>
public sealed record ShopAction(
    ShopActionKind Kind,
    ShopActionMode Mode,
    string? Steps = null,
    string? Name = null,
    string? PauseSteps = null,
    string? ResumeSteps = null);

/// <summary>A business period of a weekday, in the shop's local time: opens at <see cref="Open"/> and
/// closes at <see cref="Close"/> — the next day when <see cref="Close"/> is earlier (an overnight
/// period, e.g. 18:00–02:00).</summary>
public sealed record BusinessPeriod(TimeOnly Open, TimeOnly Close)
{
    /// <summary>Closes the next day.</summary>
    public bool IsOvernight => Close < Open;

    /// <summary>How long it is open by the clock on the wall (DST aside); zero only when
    /// <see cref="Open"/> equals <see cref="Close"/>, which the settings refuse.</summary>
    public TimeSpan Length => Close - Open;

    /// <summary>「11:00–14:00」.</summary>
    public override string ToString() =>
        $"{Open.ToString("HH:mm", CultureInfo.InvariantCulture)}–{Close.ToString("HH:mm", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// A shop's regular week (Q22): the business periods of each weekday, in the order the settings list
/// them. A day without periods is a regular day off.
/// </summary>
public sealed class WeeklyHours
{
    /// <summary>The days in the settings file's order, Monday first.</summary>
    public static readonly IReadOnlyList<DayOfWeek> Days =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
        DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private readonly IReadOnlyList<BusinessPeriod>[] _periods;

    /// <param name="periods">The periods of each day; a day that is missing has none.</param>
    public WeeklyHours(IReadOnlyDictionary<DayOfWeek, IReadOnlyList<BusinessPeriod>> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);
        _periods = new IReadOnlyList<BusinessPeriod>[7];
        foreach (var day in Days)
        {
            _periods[(int)day] = periods.TryGetValue(day, out var list) ? [.. list] : [];
        }
    }

    /// <summary>The periods of <paramref name="day"/>, in the settings' order.</summary>
    public IReadOnlyList<BusinessPeriod> this[DayOfWeek day] => _periods[(int)day];

    /// <summary>No business period on any day.</summary>
    public bool IsEmpty => _periods.All(periods => periods.Count == 0);

    /// <summary>The settings file's key of <paramref name="day"/>: <c>mon</c> … <c>sun</c>.</summary>
    public static string WireName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "mon",
        DayOfWeek.Tuesday => "tue",
        DayOfWeek.Wednesday => "wed",
        DayOfWeek.Thursday => "thu",
        DayOfWeek.Friday => "fri",
        DayOfWeek.Saturday => "sat",
        DayOfWeek.Sunday => "sun",
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, "Not a day of the week."),
    };
}
