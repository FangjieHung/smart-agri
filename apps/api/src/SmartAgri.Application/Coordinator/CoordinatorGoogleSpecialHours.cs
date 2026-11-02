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
/// <param name="Replaced">The date's periods that were dropped, each as the raw JSON Google sent.</param>
public sealed record GoogleSpecialHoursPatch(string Body, int Removed, int Kept, IReadOnlyList<string> Replaced);

/// <summary>What an applied Google action keeps to be taken back (<see cref="CoordinatorGoogleSpecialHours.ExternalRef"/>):
/// the date, the location written and the date's periods it replaced (raw JSON; empty when there were none, or when they
/// did not fit — the undo then removes the day).</summary>
public sealed record GoogleAppliedRef(DateOnly Date, string Location, IReadOnlyList<string> Previous);

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
/// new ones follow; the date's replaced periods are returned so the apply can keep them (<see cref="ExternalRef"/>). Taking
/// it back (恢復營業, Q21) puts those periods back in place of ours — or, when there were none, just removes the date's;
/// when nothing is left the body is <c>{}</c>, which resets the masked field.
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
        return ReplaceRaw(specialHours, date, [.. periods.Select(Json)]);
    }

    /// <summary>As <see cref="Replace"/>, with the new periods given as raw JSON (the ones an undo puts back).</summary>
    public static GoogleSpecialHoursPatch ReplaceRaw(JsonElement? specialHours, DateOnly date, IReadOnlyList<string> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);
        var kept = new List<string>();
        var replaced = new List<string>();
        foreach (var period in AllPeriods(specialHours))
        {
            (StartDateOf(period) == date ? replaced : kept).Add(period.GetRawText());
        }

        if (kept.Count == 0 && periods.Count == 0)
        {
            // Nothing left: the masked field absent from the body is reset (AIP-134's field mask rule).
            return new GoogleSpecialHoursPatch("{}", replaced.Count, 0, replaced);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("specialHours");
            writer.WriteStartArray("specialHourPeriods");
            foreach (var raw in kept.Concat(periods))
            {
                writer.WriteRawValue(raw, skipInputValidation: true);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new GoogleSpecialHoursPatch(Encoding.UTF8.GetString(stream.ToArray()), replaced.Count, kept.Count, replaced);
    }

    /// <summary>The periods of <paramref name="specialHours"/> starting on <paramref name="date"/>, as raw JSON.</summary>
    public static IReadOnlyList<string> PeriodsOn(JsonElement? specialHours, DateOnly date) =>
        [.. AllPeriods(specialHours).Where(period => StartDateOf(period) == date).Select(period => period.GetRawText())];

    /// <summary>Whether <paramref name="specialHours"/> has a period starting on <paramref name="date"/>.</summary>
    public static bool HasPeriodOn(JsonElement? specialHours, DateOnly date) => PeriodsOn(specialHours, date).Count > 0;

    /// <summary>Whether the raw periods <paramref name="raw"/> say the same as <paramref name="periods"/> (Google may write
    /// them differently: <c>00:00</c> as <c>{}</c>, no <c>endDate</c> for the same day, times ignored when closed).</summary>
    public static bool SameAs(IReadOnlyList<string> raw, IReadOnlyList<GoogleSpecialHourPeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(periods);
        var parsed = raw.Select(Parse).ToList();
        return parsed.All(period => period is not null)
            && Canonical(parsed.OfType<GoogleSpecialHourPeriod>()).SequenceEqual(Canonical(periods), StringComparer.Ordinal);
    }

    /// <summary>Whether two lists of raw periods say the same (as <see cref="SameAs(IReadOnlyList{string}, IReadOnlyList{GoogleSpecialHourPeriod})"/>;
    /// unreadable ones must match character for character).</summary>
    public static bool SameAs(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var parsed = right.Select(Parse).ToList();
        return parsed.All(period => period is not null)
            ? SameAs(left, [.. parsed.OfType<GoogleSpecialHourPeriod>()])
            : left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

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

    /// <summary>
    /// What an applied Google action keeps to be taken back (at most <see cref="CoordinatorCommandAction.ExternalRefMaxLength"/>):
    /// 「{yyyy-MM-dd}|{location}」 (the location written, which a later authorization may have changed), followed by
    /// 「|[…]」 — the date's periods it replaced — when there were some: as Google sent them, else without white space, else
    /// (still too long) left out, which <paramref name="keptPrevious"/> says.
    /// </summary>
    public static string ExternalRef(DateOnly date, string location, IReadOnlyList<string> previous, out bool keptPrevious)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var head = string.Create(CultureInfo.InvariantCulture, $"{date:yyyy-MM-dd}|{location}");
        keptPrevious = previous.Count == 0;
        if (previous.Count == 0 || location.Contains('|', StringComparison.Ordinal))
        {
            return head;
        }

        foreach (var list in new Func<IEnumerable<string>>[] { () => previous, () => previous.Select(Compact) })
        {
            var candidate = $"{head}|[{string.Join(",", list())}]";
            if (candidate.Length <= CoordinatorCommandAction.ExternalRefMaxLength)
            {
                keptPrevious = true;
                return candidate;
            }
        }

        return head;
    }

    /// <summary>An apply's <c>ExternalRef</c> (<see cref="ExternalRef"/>) read back; <see langword="false"/> when it is not
    /// one (e.g. written by something else).</summary>
    public static bool TryReadExternalRef(string? externalRef, out GoogleAppliedRef applied)
    {
        applied = null!;
        var parts = externalRef?.Split('|', 3);
        if (parts is not { Length: 2 or 3 }
            || !DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || !CoordinatorConnection.IsValidExternalId(parts[1]))
        {
            return false;
        }

        var name = parts[1];

        var previous = new List<string>();
        if (parts.Length == 3)
        {
            try
            {
                using var list = JsonDocument.Parse(parts[2]);
                if (list.RootElement.ValueKind != JsonValueKind.Array
                    || list.RootElement.EnumerateArray().Any(period => StartDateOf(period) != date))
                {
                    return false;
                }

                previous.AddRange(list.RootElement.EnumerateArray().Select(period => period.GetRawText()));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        applied = new GoogleAppliedRef(date, name, previous);
        return true;
    }

    /// <summary>A period as the executor writes it.</summary>
    public static string Json(GoogleSpecialHourPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, period);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A raw period read the way Google means it, or <see langword="null"/> when it cannot be.</summary>
    private static GoogleSpecialHourPeriod? Parse(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var period = document.RootElement;
            if (StartDateOf(period) is not { } start)
            {
                return null;
            }

            if (period.TryGetProperty("closed", out var closed) && closed.ValueKind == JsonValueKind.True)
            {
                return GoogleSpecialHourPeriod.ClosedOn(start);
            }

            var end = period.TryGetProperty("endDate", out var endDate) ? DateOf(endDate) : start;
            return end is null || TimeOf(period, "openTime") is not { } opens || TimeOf(period, "closeTime") is not { } closes
                ? null
                : new GoogleSpecialHourPeriod(start, false, opens, end, closes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GoogleTimeOfDay? TimeOf(JsonElement period, string name)
    {
        if (!period.TryGetProperty(name, out var time) || time.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Proto3 JSON leaves zero fields out: 00:00 is {}.
        Int(time, "hours", out var hours);
        Int(time, "minutes", out var minutes);
        return new GoogleTimeOfDay(hours, minutes);
    }

    private static IEnumerable<string> Canonical(IEnumerable<GoogleSpecialHourPeriod> periods) =>
        periods.Select(period => period.Closed ? GoogleSpecialHourPeriod.ClosedOn(period.StartDate) : period with { EndDate = period.EndDate ?? period.StartDate })
            .Select(period => period.ToString())
            .Order(StringComparer.Ordinal);

    private static string Compact(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static IEnumerable<JsonElement> AllPeriods(JsonElement? specialHours) =>
        specialHours is { ValueKind: JsonValueKind.Object } hours
        && hours.TryGetProperty("specialHourPeriods", out var list)
        && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : [];

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
