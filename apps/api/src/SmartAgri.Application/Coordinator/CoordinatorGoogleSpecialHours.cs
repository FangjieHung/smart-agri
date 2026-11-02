using System.Globalization;
using System.Text;
using System.Text.Json;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>A time of day as Google writes it (<c>google.type.TimeOfDay</c>): <see cref="Hours"/> 0–23, or 24 with
/// <see cref="Minutes"/> 0 for the midnight at the end of the day (a close time only).</summary>
public readonly record struct GoogleTimeOfDay(int Hours, int Minutes)
{
    /// <summary>The midnight that ends a day (<c>24:00</c>).</summary>
    public static readonly GoogleTimeOfDay EndOfDay = new(24, 0);

    public static GoogleTimeOfDay Of(TimeOnly time) => new(time.Hour, time.Minute);

    /// <summary>「17:30」, 「24:00」.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Hours:00}:{Minutes:00}");
}

/// <summary>
/// One <c>SpecialHourPeriod</c> of a Google 商家 location (Business Information API v1): closed all of
/// <see cref="StartDate"/> (<see cref="Closed"/>, no times), or open from <see cref="OpenTime"/> on
/// <see cref="StartDate"/> to <see cref="CloseTime"/> on <see cref="EndDate"/> — the same day, or the next one for a
/// period past midnight.
/// </summary>
public sealed record GoogleSpecialHourPeriod(
    DateOnly StartDate,
    bool Closed,
    GoogleTimeOfDay? OpenTime = null,
    DateOnly? EndDate = null,
    GoogleTimeOfDay? CloseTime = null)
{
    /// <summary>Closed all of <paramref name="date"/>.</summary>
    public static GoogleSpecialHourPeriod ClosedOn(DateOnly date) => new(date, Closed: true);

    /// <summary>「11/2 17:00–11/3 01:00」 or 「11/2 休息」, for logs and tests.</summary>
    public override string ToString() => Closed
        ? $"{StartDate:yyyy-MM-dd} closed"
        : $"{StartDate:yyyy-MM-dd} {OpenTime}–{(EndDate is { } end && end != StartDate ? $"{end:yyyy-MM-dd} " : string.Empty)}{CloseTime}";
}

/// <summary>What a write of a location's special hours does to it.</summary>
/// <param name="Body">The <c>locations.patch</c> body (<c>updateMask=specialHours</c>).</param>
/// <param name="Removed">How many periods of the date were dropped.</param>
/// <param name="Kept">How many periods of other dates are written back unchanged.</param>
public sealed record GoogleSpecialHoursPatch(string Body, int Removed, int Kept);

/// <summary>
/// Google 特殊營業時間 (store-coordinator design §3 Q3, Q21, Q22, §5 Slice 7, #378), pure: the periods a command sets on
/// its date — from the shop's own weekly hours (Q22), never from what Google holds as regular hours — and merging them
/// into the location's special hours, so that every other date stays exactly as Google returned it.
/// </summary>
/// <remarks>
/// <para>
/// 臨時店休 is one period <c>{ startDate: D, closed: true }</c>. 提早打烊 at T keeps the day's periods that open before T,
/// the one running at T cut at T (a lunch before it stays); 延後開店 at T keeps those that close after T, the one running
/// at T starting at T. T is read as the command's checks read it (<see cref="CoordinatorCommandPlanner.InstantOf"/>): the
/// small hours of the day's overnight period are the next day. A period past midnight ends on the next day
/// (<c>endDate</c> D+1); one ending exactly at midnight ends at <c>24:00</c> on its own day. Only periods opening on D
/// are written (special hours of a date replace its regular hours, so a late opening in the small hours of D+1 is left
/// out rather than closing D+1); a date left with none — or that the settings no longer open — is written closed.
/// </para>
/// <para>
/// A write replaces the location's whole list (<c>updateMask=specialHours</c>), so the list is read first: each period
/// whose <c>startDate</c> is the date is dropped, every other one is written back as the very JSON Google sent, and the
/// new ones follow. Removing (恢復營業, Q21) is the same without new periods; when nothing is left the body is <c>{}</c>, which
/// resets the masked field.
/// </para>
/// </remarks>
public static class CoordinatorGoogleSpecialHours
{
    /// <summary>The periods <paramref name="scenario"/> sets on <paramref name="date"/> (<paramref name="time"/> for
    /// 提早打烊 and 延後開店), in local wall-clock time, earliest first.</summary>
    public static IReadOnlyList<GoogleSpecialHourPeriod> PeriodsFor(ShopSettingsDocument settings, ShopScenario scenario, DateOnly date, TimeOnly? time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (scenario)
        {
            case ShopScenario.TemporaryClosure:
                return [GoogleSpecialHourPeriod.ClosedOn(date)];

            case ShopScenario.CloseEarly or ShopScenario.OpenLate:
                if (time is not { } clock)
                {
                    throw new ArgumentException("An early closing or a late opening has a time.", nameof(time));
                }

                var hours = settings.Hours;
                var at = CoordinatorCommandPlanner.InstantOf(settings, date, clock);
                var periods = hours.PeriodsOn(date)
                    .Select(period => scenario == ShopScenario.CloseEarly
                        ? period.Opens < at ? new BusinessInterval(period.Opens, period.Closes < at ? period.Closes : at) : null
                        : period.Closes > at ? new BusinessInterval(period.Opens > at ? period.Opens : at, period.Closes) : null)
                    .OfType<BusinessInterval>()
                    .Select(interval => PeriodOf(hours, interval))
                    // Special hours of a date replace that date's regular hours: a late opening in the small hours of
                    // the next day cannot be written on this date, and writing it on the next would close that day.
                    .Where(period => period.StartDate == date)
                    .ToList();
                return periods.Count == 0 ? [GoogleSpecialHourPeriod.ClosedOn(date)] : periods;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "A reopen takes the date's special hours away; it sets none.");
        }
    }

    /// <summary>The <c>locations.patch</c> body that replaces <paramref name="date"/>'s periods in
    /// <paramref name="specialHours"/> (the location's <c>specialHours</c> as read, or <see langword="null"/> when it
    /// has none) by <paramref name="periods"/> — none to remove them.</summary>
    public static GoogleSpecialHoursPatch Replace(JsonElement? specialHours, DateOnly date, IReadOnlyList<GoogleSpecialHourPeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);
        var kept = new List<string>();
        var removed = 0;
        if (specialHours is { ValueKind: JsonValueKind.Object } hours
            && hours.TryGetProperty("specialHourPeriods", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var period in list.EnumerateArray())
            {
                if (StartDateOf(period) == date)
                {
                    removed++;
                }
                else
                {
                    kept.Add(period.GetRawText());
                }
            }
        }

        if (kept.Count == 0 && periods.Count == 0)
        {
            // Nothing left: the masked field absent from the body is reset (AIP-134's field mask rule).
            return new GoogleSpecialHoursPatch("{}", removed, 0);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("specialHours");
            writer.WriteStartArray("specialHourPeriods");
            foreach (var raw in kept)
            {
                writer.WriteRawValue(raw, skipInputValidation: true);
            }

            foreach (var period in periods)
            {
                Write(writer, period);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new GoogleSpecialHoursPatch(Encoding.UTF8.GetString(stream.ToArray()), removed, kept.Count);
    }

    /// <summary>Whether <paramref name="specialHours"/> has a period starting on <paramref name="date"/>.</summary>
    public static bool HasPeriodOn(JsonElement? specialHours, DateOnly date) =>
        specialHours is { ValueKind: JsonValueKind.Object } hours
        && hours.TryGetProperty("specialHourPeriods", out var list)
        && list.ValueKind == JsonValueKind.Array
        && list.EnumerateArray().Any(period => StartDateOf(period) == date);

    /// <summary>Whether a location's <c>regularHours</c> (as read) has at least one period: Google sets special hours
    /// only for a location with regular hours.</summary>
    public static bool HasRegularHours(JsonElement? regularHours) =>
        regularHours is { ValueKind: JsonValueKind.Object } hours
        && hours.TryGetProperty("periods", out var periods)
        && periods.ValueKind == JsonValueKind.Array
        && periods.GetArrayLength() > 0;

    /// <summary>The <c>startDate</c> of a period as read, or <see langword="null"/> when it has no readable one.</summary>
    public static DateOnly? StartDateOf(JsonElement period) =>
        period.ValueKind == JsonValueKind.Object && period.TryGetProperty("startDate", out var start) ? DateOf(start) : null;

    /// <summary>A <c>google.type.Date</c> (<c>{ year, month, day }</c>) as read; <see langword="null"/> unless it is a
    /// full, valid date.</summary>
    public static DateOnly? DateOf(JsonElement date)
    {
        if (date.ValueKind != JsonValueKind.Object
            || !Int(date, "year", out var year) || !Int(date, "month", out var month) || !Int(date, "day", out var day)
            || year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        return new DateOnly(year, month, day);
    }

    /// <summary>The date of an apply's <c>ExternalRef</c> (<see cref="ExternalRef"/>) and its location.</summary>
    public static bool TryReadExternalRef(string? externalRef, out DateOnly date, out string location)
    {
        date = default;
        location = string.Empty;
        if (externalRef?.Split('|') is not [var day, var name]
            || !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || !CoordinatorConnection.IsValidExternalId(name))
        {
            return false;
        }

        location = name;
        return true;
    }

    /// <summary>What an applied Google action keeps to be taken back: 「{yyyy-MM-dd}|{location}」 (the location written,
    /// which a later authorization may have changed).</summary>
    public static string ExternalRef(DateOnly date, string location) =>
        string.Create(CultureInfo.InvariantCulture, $"{date:yyyy-MM-dd}|{location}");

    private static GoogleSpecialHourPeriod PeriodOf(BusinessHours hours, BusinessInterval interval)
    {
        var opens = TimeZoneInfo.ConvertTime(interval.Opens, hours.Zone).DateTime;
        var closes = TimeZoneInfo.ConvertTime(interval.Closes, hours.Zone).DateTime;
        var startDate = DateOnly.FromDateTime(opens);
        var endDate = DateOnly.FromDateTime(closes);
        var closeTime = GoogleTimeOfDay.Of(TimeOnly.FromDateTime(closes));
        if (endDate > startDate && closeTime == default)
        {
            // Closing exactly at midnight: 24:00 of the day it opens.
            (endDate, closeTime) = (endDate.AddDays(-1), GoogleTimeOfDay.EndOfDay);
        }

        return new GoogleSpecialHourPeriod(startDate, false, GoogleTimeOfDay.Of(TimeOnly.FromDateTime(opens)), endDate, closeTime);
    }

    private static void Write(Utf8JsonWriter writer, GoogleSpecialHourPeriod period)
    {
        writer.WriteStartObject();
        WriteDate(writer, "startDate", period.StartDate);
        if (period.Closed)
        {
            writer.WriteBoolean("closed", true);
        }
        else
        {
            WriteTime(writer, "openTime", period.OpenTime!.Value);
            WriteDate(writer, "endDate", period.EndDate ?? period.StartDate);
            WriteTime(writer, "closeTime", period.CloseTime!.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteDate(Utf8JsonWriter writer, string name, DateOnly date)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("year", date.Year);
        writer.WriteNumber("month", date.Month);
        writer.WriteNumber("day", date.Day);
        writer.WriteEndObject();
    }

    private static void WriteTime(Utf8JsonWriter writer, string name, GoogleTimeOfDay time)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("hours", time.Hours);
        writer.WriteNumber("minutes", time.Minutes);
        writer.WriteEndObject();
    }

    private static bool Int(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }
}
