using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartAgri.Domain.Coordinator;

/// <summary>One thing wrong with a settings file: where (a JSON path such as
/// <c>$.weeklyHours.sat[0].close</c>) and what, in Chinese for the operator.</summary>
public sealed record ShopSettingsError(string Path, string Message)
{
    /// <summary>「<c>$.storeName</c>：不能是空白。」</summary>
    public override string ToString() => $"{Path}：{Message}";
}

/// <summary>The outcome of <see cref="ShopSettingsJson.Parse"/>: the settings when the file is valid,
/// else every error found.</summary>
public sealed record ShopSettingsParseResult(ShopSettingsDocument? Document, IReadOnlyList<ShopSettingsError> Errors)
{
    public bool IsValid => Document is not null && Errors.Count == 0;
}

/// <summary>
/// The shop-settings file (store-coordinator design §5 Slice 2, decision A, #373): what the operator
/// imports with <c>coordinator-shop import</c> and what <c>coordinator-shop show</c> prints, so one
/// round-trips through the other. <see cref="Parse"/> validates everything and lists every error with
/// its JSON path; <see cref="Write"/> writes the same format. The two <c>jsonb</c> columns of
/// <see cref="ShopSettings"/> hold the <c>weeklyHours</c> and <c>scenarios</c> sections in it too.
/// </summary>
/// <remarks>
/// <para>
/// Shape: <c>storeName</c> (at most 50 characters, one line); <c>timeZone</c> (an IANA id, optional,
/// <see cref="ShopSettings.DefaultTimeZoneId"/>); <c>weeklyHours</c> with all of <c>mon</c> … <c>sun</c>,
/// each a list of at most four <c>{ "open": "HH:mm", "close": "HH:mm" }</c> (<c>[]</c> = regular day
/// off; a close earlier than the open is the next day; open equal to close, overlaps — also of an
/// overnight period with the next day's — and a week without any period are refused); <c>scenarios</c>
/// with exactly <c>temporary-closure</c>, <c>close-early</c>, <c>open-late</c> and <c>reopen</c>.
/// </para>
/// <para>
/// A scenario: <c>enabled</c>; <c>announcement</c> <c>{ "template", "imageUrl"? }</c> (a template of at
/// most 1,000 characters checked by <see cref="ShopAnnouncements.Problems"/>; an absolute <c>https</c>
/// URL of at most 2,000 characters) — required when enabled, except for <c>reopen</c>; <c>actions</c> —
/// at least one when enabled, never under <c>reopen</c>. An action is <c>{ "kind", "mode", "steps" }</c>
/// for <c>line-broadcast</c>, <c>google-special-hours</c> and <c>facebook-post</c> (at most one of each
/// per scenario) and <c>{ "kind": "ordering", "name", "mode": "reminder", "pauseSteps", "resumeSteps" }</c>
/// (any number, distinct names of at most 20 characters); steps at most 500 characters. Unknown fields
/// are errors everywhere.
/// </para>
/// <para>
/// Optional <c>reservations</c> (store-reservations design §3.1 point 2, #389): <c>enabled</c>; <c>slug</c> and
/// <c>phone</c> (required when enabled, <see cref="ShopReservationSettings.IsValidSlug"/> and
/// <see cref="ShopReservationSettings.IsValidPhone"/>); integers <c>slotMinutes</c>, <c>minLeadMinutes</c>,
/// <c>maxDaysAhead</c>, <c>lastSeatingMinutes</c>, <c>maxPartySize</c> and <c>pendingExpiryMinutes</c>, each optional with
/// its default and range, <c>pendingExpiryMinutes</c> less than <c>minLeadMinutes</c>. Without the block the shop takes
/// no reservations. That a slug is not another shop's is checked by the import, which can see every shop.
/// </para>
/// </remarks>
public static partial class ShopSettingsJson
{
    /// <summary>Most business periods a day may have.</summary>
    public const int MaxPeriodsPerDay = 4;

    /// <summary>Longest <c>steps</c>, <c>pauseSteps</c> and <c>resumeSteps</c> (characters).</summary>
    public const int StepsMaxLength = 500;

    /// <summary>Longest name of an ordering system (characters; fits a LINE button).</summary>
    public const int OrderingNameMaxLength = 20;

    /// <summary>Longest image URL (characters).</summary>
    public const int ImageUrlMaxLength = 2000;

    private const string RootPath = "$";

    private static readonly string[] DocumentFields = ["storeName", "timeZone", "weeklyHours", "scenarios", "reservations"];

    private static readonly string[] PeriodFields = ["open", "close"];

    private static readonly string[] AnnouncementFields = ["template", "imageUrl"];

    private static readonly string[] ScenarioFields = ["enabled", "announcement", "actions"];

    private static readonly string[] ReopenFields = ["enabled", "announcement"];

    private static readonly string[] StepsActionFields = ["kind", "mode", "steps"];

    private static readonly string[] OrderingActionFields = ["kind", "name", "mode", "pauseSteps", "resumeSteps"];

    private static readonly string[] ReservationFields =
    [
        "enabled", "slug", "phone", "slotMinutes", "minLeadMinutes", "maxDaysAhead", "lastSeatingMinutes", "maxPartySize",
        "pendingExpiryMinutes",
    ];

    private static readonly JsonWriterOptions Indented = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Reads and validates a settings file.</summary>
    public static ShopSettingsParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            return new ShopSettingsParseResult(
                null,
                [
                    new ShopSettingsError(
                        RootPath,
                        $"不是有效的 JSON：第 {exception.LineNumber + 1} 行第 {exception.BytePositionInLine + 1} 個位元組附近有錯。"),
                ]);
        }

        using (document)
        {
            var reader = new Reader(strict: true);
            var settings = reader.Document(document.RootElement);
            return reader.Errors.Count == 0 && settings is not null
                ? new ShopSettingsParseResult(settings, [])
                : new ShopSettingsParseResult(null, reader.Errors);
        }
    }

    /// <summary><paramref name="settings"/> as a settings file (indented, <c>\n</c> line ends, no final
    /// line end).</summary>
    public static string Write(ShopSettingsDocument settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Written(Indented, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("storeName", settings.StoreName);
            writer.WriteString("timeZone", settings.TimeZoneId);
            writer.WritePropertyName("weeklyHours");
            Write(writer, settings.WeeklyHours);
            writer.WritePropertyName("scenarios");
            Write(writer, settings.Scenarios);
            if (settings.Reservations is { } reservations)
            {
                writer.WritePropertyName("reservations");
                Write(writer, reservations);
            }

            writer.WriteEndObject();
        });
    }

    /// <summary>The <c>weeklyHours</c> section, compact (the stored <c>jsonb</c>).</summary>
    public static string WriteWeeklyHours(WeeklyHours weeklyHours)
    {
        ArgumentNullException.ThrowIfNull(weeklyHours);
        return Written(Compact, writer => Write(writer, weeklyHours));
    }

    /// <summary>The <c>scenarios</c> section, compact (the stored <c>jsonb</c>).</summary>
    public static string WriteScenarios(IReadOnlyDictionary<ShopScenario, ShopScenarioSettings> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        return Written(Compact, writer => Write(writer, scenarios));
    }

    /// <summary>The <c>reservations</c> block, compact (the stored <c>jsonb</c>, #389).</summary>
    public static string WriteReservations(ShopReservationSettings reservations)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        return Written(Compact, writer => Write(writer, reservations));
    }

    /// <summary>One scenario's settings, compact (to tell which scenarios an import changed).</summary>
    public static string WriteScenario(ShopScenario scenario, ShopScenarioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Written(Compact, writer => Write(writer, scenario, settings));
    }

    /// <summary>
    /// Reads a stored <c>weeklyHours</c> section. Only its shape is checked, not the rules (limits,
    /// overlaps): those were checked when it was imported, and settings stay readable if a rule
    /// tightens later. Throws <see cref="FormatException"/> when the shape is wrong.
    /// </summary>
    public static WeeklyHours ReadWeeklyHours(string json) =>
        ReadStored(json, "weeklyHours", (reader, element) => reader.WeeklyHours(element, Child(RootPath, "weeklyHours")));

    /// <summary>Reads a stored <c>scenarios</c> section, like <see cref="ReadWeeklyHours"/>.</summary>
    public static IReadOnlyDictionary<ShopScenario, ShopScenarioSettings> ReadScenarios(string json) =>
        ReadStored(json, "scenarios", (reader, element) => reader.Scenarios(element, Child(RootPath, "scenarios")));

    /// <summary>Reads a stored <c>reservations</c> block, like <see cref="ReadWeeklyHours"/> (#389).</summary>
    public static ShopReservationSettings ReadReservations(string json) =>
        ReadStored(json, "reservations", (reader, element) => reader.Reservations(element, Child(RootPath, "reservations")));

    private static T ReadStored<T>(string json, string section, Func<Reader, JsonElement, T?> read)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var reader = new Reader(strict: false);
        var value = read(reader, document.RootElement);
        return reader.Errors.Count == 0 && value is not null
            ? value
            : throw new FormatException(
                $"The stored {section} are not shop settings: {string.Join("; ", reader.Errors.Select(error => error.Path))}");
    }

    private static string Written(JsonWriterOptions options, Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, WeeklyHours weeklyHours)
    {
        writer.WriteStartObject();
        foreach (var day in WeeklyHours.Days)
        {
            writer.WriteStartArray(WeeklyHours.WireName(day));
            foreach (var period in weeklyHours[day])
            {
                writer.WriteStartObject();
                writer.WriteString("open", ShopAnnouncements.FormatTime(period.Open));
                writer.WriteString("close", ShopAnnouncements.FormatTime(period.Close));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter writer, ShopReservationSettings reservations)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", reservations.Enabled);
        if (reservations.Slug is { } slug)
        {
            writer.WriteString("slug", slug);
        }

        if (reservations.Phone is { } phone)
        {
            writer.WriteString("phone", phone);
        }

        writer.WriteNumber("slotMinutes", reservations.SlotMinutes);
        writer.WriteNumber("minLeadMinutes", reservations.MinLeadMinutes);
        writer.WriteNumber("maxDaysAhead", reservations.MaxDaysAhead);
        writer.WriteNumber("lastSeatingMinutes", reservations.LastSeatingMinutes);
        writer.WriteNumber("maxPartySize", reservations.MaxPartySize);
        writer.WriteNumber("pendingExpiryMinutes", reservations.PendingExpiryMinutes);
        writer.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter writer, IReadOnlyDictionary<ShopScenario, ShopScenarioSettings> scenarios)
    {
        writer.WriteStartObject();
        foreach (var scenario in Enum.GetValues<ShopScenario>())
        {
            if (scenarios.TryGetValue(scenario, out var settings))
            {
                writer.WritePropertyName(WireNames<ShopScenario>.ToWire(scenario));
                Write(writer, scenario, settings);
            }
        }

        writer.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter writer, ShopScenario scenario, ShopScenarioSettings settings)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", settings.Enabled);
        if (settings.Announcement is { } announcement)
        {
            writer.WriteStartObject("announcement");
            writer.WriteString("template", announcement.Template);
            if (announcement.ImageUrl is { } imageUrl)
            {
                writer.WriteString("imageUrl", imageUrl);
            }

            writer.WriteEndObject();
        }

        if (scenario != ShopScenario.Reopen)
        {
            writer.WriteStartArray("actions");
            foreach (var action in settings.Actions)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", WireNames<ShopActionKind>.ToWire(action.Kind));
                if (action.Kind == ShopActionKind.Ordering)
                {
                    writer.WriteString("name", action.Name);
                    writer.WriteString("mode", WireNames<ShopActionMode>.ToWire(action.Mode));
                    writer.WriteString("pauseSteps", action.PauseSteps);
                    writer.WriteString("resumeSteps", action.ResumeSteps);
                }
                else
                {
                    writer.WriteString("mode", WireNames<ShopActionMode>.ToWire(action.Mode));
                    writer.WriteString("steps", action.Steps);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary><paramref name="name"/> under <paramref name="path"/>: <c>.name</c>, or <c>['name']</c>
    /// when it is not an identifier (<c>$.scenarios['temporary-closure']</c>).</summary>
    private static string Child(string path, string name) =>
        Identifier().IsMatch(name) ? $"{path}.{name}" : $"{path}['{name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}']";

    private static string Item(string path, int index) => $"{path}[{index}]";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    [GeneratedRegex("^(?:[01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex ClockTime();

    /// <summary>
    /// One read of a settings file or stored section, best effort so that every error is found:
    /// structural errors (types, unknown or missing fields, unknown names, malformed times) always;
    /// the rules (limits, overlaps, templates, URLs, time zone, combinations) only when
    /// <c>strict</c>. A part with a structural error comes back <see langword="null"/>.
    /// </summary>
    private sealed class Reader(bool strict)
    {
        public List<ShopSettingsError> Errors { get; } = [];

        public ShopSettingsDocument? Document(JsonElement element)
        {
            var properties = Properties(element, RootPath, DocumentFields);
            if (properties is null)
            {
                return null;
            }

            var storeName = RequiredString(properties, RootPath, "storeName");
            if (storeName is not null)
            {
                SingleLine(storeName, Child(RootPath, "storeName"), ShopSettings.StoreNameMaxLength);
            }

            var timeZoneId = ShopSettings.DefaultTimeZoneId;
            var timeZoneRead = true;
            if (properties.TryGetValue("timeZone", out var timeZone))
            {
                var path = Child(RootPath, "timeZone");
                if (TextOf(timeZone, path) is { } id)
                {
                    timeZoneId = id;
                    TimeZoneRule(id, path);
                }
                else
                {
                    timeZoneRead = false;
                }
            }

            var weeklyHours = properties.TryGetValue("weeklyHours", out var hours)
                ? WeeklyHours(hours, Child(RootPath, "weeklyHours"))
                : Missing<WeeklyHours>(Child(RootPath, "weeklyHours"));
            var scenarios = properties.TryGetValue("scenarios", out var scenarioElement)
                ? Scenarios(scenarioElement, Child(RootPath, "scenarios"))
                : Missing<IReadOnlyDictionary<ShopScenario, ShopScenarioSettings>>(Child(RootPath, "scenarios"));

            ShopReservationSettings? reservations = null;
            var reservationsRead = true;
            if (properties.TryGetValue("reservations", out var reservationsElement))
            {
                reservations = Reservations(reservationsElement, Child(RootPath, "reservations"));
                reservationsRead = reservations is not null;
            }

            return storeName is not null && timeZoneRead && weeklyHours is not null && scenarios is not null && reservationsRead
                ? new ShopSettingsDocument(storeName, timeZoneId, weeklyHours, scenarios, reservations)
                : null;
        }

        /// <summary>The <c>reservations</c> block (#389).</summary>
        public ShopReservationSettings? Reservations(JsonElement element, string path)
        {
            var properties = Properties(element, path, ReservationFields);
            if (properties is null)
            {
                return null;
            }

            var enabled = RequiredBoolean(properties, path, "enabled");
            var complete = enabled is not null;

            string? slug = null;
            var slugPath = Child(path, "slug");
            if (properties.TryGetValue("slug", out var slugElement) && slugElement.ValueKind != JsonValueKind.Null)
            {
                slug = TextOf(slugElement, slugPath);
                complete &= slug is not null;
                if (slug is not null && !ShopReservationSettings.IsValidSlug(slug))
                {
                    Rule(
                        slugPath,
                        $"必須是 {ShopReservationSettings.SlugMinLength}–{ShopReservationSettings.SlugMaxLength} 個小寫英文字母、數字或 -，"
                        + "不能以 - 開頭或結尾，例如 wangji；它會出現在訂位網址 /reserve/{代號} 裡。");
                }
            }
            else if (enabled == true)
            {
                Rule(slugPath, "開放訂位時必須設定訂位網址的代號（slug），例如 \"wangji\"。");
            }

            string? phone = null;
            var phonePath = Child(path, "phone");
            if (properties.TryGetValue("phone", out var phoneElement) && phoneElement.ValueKind != JsonValueKind.Null)
            {
                phone = TextOf(phoneElement, phonePath);
                complete &= phone is not null;
                if (phone is not null && SingleLine(phone, phonePath, ShopReservationSettings.PhoneMaxLength)
                    && phone.Length <= ShopReservationSettings.PhoneMaxLength && !ShopReservationSettings.IsValidPhone(phone))
                {
                    Rule(phonePath, "必須是店家的電話號碼（8 到 15 個數字，可以有空白、-、( )，開頭可以是 +），例如 02-2345-6789。");
                }
            }
            else if (enabled == true)
            {
                Rule(phonePath, "開放訂位時必須設定店家電話（phone）：會顯示在訂位頁與通知信，讓顧客來電更改或取消。");
            }

            var slotMinutes = Integer(properties, path, "slotMinutes", ShopReservationSettings.DefaultSlotMinutes, 1, 60);
            if (slotMinutes is { } slot && slot is >= 1 and <= 60 && !ShopReservationSettings.SlotMinuteChoices.Contains(slot))
            {
                Rule(Child(path, "slotMinutes"), $"必須是 {string.Join('、', ShopReservationSettings.SlotMinuteChoices)} 其中之一（分鐘）。");
            }

            var minLead = Integer(properties, path, "minLeadMinutes", ShopReservationSettings.DefaultMinLeadMinutes, 1, ShopReservationSettings.MaxMinLeadMinutes);
            var maxDays = Integer(properties, path, "maxDaysAhead", ShopReservationSettings.DefaultMaxDaysAhead, 1, ShopReservationSettings.MaxMaxDaysAhead);
            var lastSeating = Integer(properties, path, "lastSeatingMinutes", ShopReservationSettings.DefaultLastSeatingMinutes, 0, ShopReservationSettings.MaxLastSeatingMinutes);
            var maxParty = Integer(properties, path, "maxPartySize", ShopReservationSettings.DefaultMaxPartySize, 1, ShopReservationSettings.MaxMaxPartySize);
            var expiry = Integer(properties, path, "pendingExpiryMinutes", ShopReservationSettings.DefaultPendingExpiryMinutes, 0, ShopReservationSettings.MaxPendingExpiryMinutes);
            if (expiry is { } pendingExpiry && minLead is { } lead && pendingExpiry >= lead)
            {
                Rule(
                    Child(path, "pendingExpiryMinutes"),
                    $"必須小於 minLeadMinutes（目前 {pendingExpiry} ≥ {lead}）：沒人處理的訂位在訂位時間前 pendingExpiryMinutes 分鐘過期，"
                    + "最晚送出的訂位也要留時間給店家接受或婉拒。");
            }

            complete &= slotMinutes is not null && minLead is not null && maxDays is not null && lastSeating is not null
                && maxParty is not null && expiry is not null;
            return complete
                ? new ShopReservationSettings(
                    enabled!.Value, slug, phone, slotMinutes!.Value, minLead!.Value, maxDays!.Value, lastSeating!.Value, maxParty!.Value, expiry!.Value)
                : null;
        }

        public WeeklyHours? WeeklyHours(JsonElement element, string path)
        {
            var days = Properties(element, path, [.. Coordinator.WeeklyHours.Days.Select(Coordinator.WeeklyHours.WireName)]);
            if (days is null)
            {
                return null;
            }

            var complete = true;
            var periods = new Dictionary<DayOfWeek, IReadOnlyList<BusinessPeriod>>();
            var located = new List<LocatedPeriod>();
            foreach (var day in Coordinator.WeeklyHours.Days)
            {
                var dayPath = Child(path, Coordinator.WeeklyHours.WireName(day));
                if (!days.TryGetValue(Coordinator.WeeklyHours.WireName(day), out var list))
                {
                    Error(dayPath, "缺少這一天；公休請寫 []。");
                    complete = false;
                    continue;
                }

                if (list.ValueKind != JsonValueKind.Array)
                {
                    Error(dayPath, "必須是時段的陣列，例如 [{ \"open\": \"11:00\", \"close\": \"14:00\" }]；公休請寫 []。");
                    complete = false;
                    continue;
                }

                if (list.GetArrayLength() > MaxPeriodsPerDay)
                {
                    Rule(dayPath, $"一天最多 {MaxPeriodsPerDay} 個時段（目前 {list.GetArrayLength()} 個）。");
                }

                var dayPeriods = new List<BusinessPeriod>();
                var index = 0;
                foreach (var item in list.EnumerateArray())
                {
                    var itemPath = Item(dayPath, index);
                    if (Period(item, itemPath) is { } period)
                    {
                        dayPeriods.Add(period);
                        located.Add(new LocatedPeriod(day, index, period, itemPath));
                    }
                    else
                    {
                        complete = false;
                    }

                    index++;
                }

                periods[day] = dayPeriods;
            }

            Overlaps(located);
            if (complete && located.Count == 0)
            {
                Rule(path, "每週至少要有一個營業時段。");
            }

            return complete ? new Coordinator.WeeklyHours(periods) : null;
        }

        public IReadOnlyDictionary<ShopScenario, ShopScenarioSettings>? Scenarios(JsonElement element, string path)
        {
            var names = WireNames<ShopScenario>.All;
            var properties = Properties(element, path, names, _ => $"不認得的情境；可用：{string.Join('、', names)}。");
            if (properties is null)
            {
                return null;
            }

            var complete = true;
            var scenarios = new Dictionary<ShopScenario, ShopScenarioSettings>();
            foreach (var scenario in Enum.GetValues<ShopScenario>())
            {
                var name = WireNames<ShopScenario>.ToWire(scenario);
                var scenarioPath = Child(path, name);
                if (!properties.TryGetValue(name, out var scenarioElement))
                {
                    Error(scenarioPath, "缺少這個情境；不提供請寫 { \"enabled\": false }。");
                    complete = false;
                }
                else if (Scenario(scenario, scenarioElement, scenarioPath) is { } settings)
                {
                    scenarios[scenario] = settings;
                }
                else
                {
                    complete = false;
                }
            }

            return complete ? scenarios : null;
        }

        private ShopScenarioSettings? Scenario(ShopScenario scenario, JsonElement element, string path)
        {
            var reopen = scenario == ShopScenario.Reopen;
            var properties = Properties(
                element,
                path,
                reopen ? ReopenFields : ScenarioFields,
                name => reopen && name == "actions"
                    ? "恢復營業沒有自己的動作：它做的事由要恢復的那次指令決定，所以不能設定 actions。"
                    : null);
            if (properties is null)
            {
                return null;
            }

            var complete = true;
            var enabled = RequiredBoolean(properties, path, "enabled");
            complete &= enabled is not null;

            ShopAnnouncement? announcement = null;
            var announcementPath = Child(path, "announcement");
            if (properties.TryGetValue("announcement", out var announcementElement))
            {
                announcement = Announcement(scenario, announcementElement, announcementPath);
                complete &= announcement is not null;
            }
            else if (enabled == true && !reopen)
            {
                Rule(announcementPath, "啟用的情境必須設定公告。");
            }

            IReadOnlyList<ShopAction> actions = [];
            if (!reopen)
            {
                var actionsPath = Child(path, "actions");
                if (properties.TryGetValue("actions", out var actionsElement))
                {
                    if (Actions(actionsElement, actionsPath) is { } list)
                    {
                        actions = list;
                        if (enabled == true && list.Count == 0)
                        {
                            Rule(actionsPath, "啟用的情境至少要有一個動作。");
                        }
                    }
                    else
                    {
                        complete = false;
                    }
                }
                else if (enabled == true)
                {
                    Rule(actionsPath, "啟用的情境必須列出動作（actions）。");
                }
            }

            return complete ? new ShopScenarioSettings(enabled!.Value, announcement, actions) : null;
        }

        private ShopAnnouncement? Announcement(ShopScenario scenario, JsonElement element, string path)
        {
            var properties = Properties(element, path, AnnouncementFields);
            if (properties is null)
            {
                return null;
            }

            var templatePath = Child(path, "template");
            var template = RequiredString(properties, path, "template");
            if (template is not null && MultiLine(template, templatePath, ShopAnnouncements.TemplateMaxLength))
            {
                foreach (var problem in ShopAnnouncements.Problems(scenario, template))
                {
                    Rule(templatePath, problem);
                }
            }

            string? imageUrl = null;
            var imageRead = true;
            if (properties.TryGetValue("imageUrl", out var image) && image.ValueKind != JsonValueKind.Null)
            {
                var imagePath = Child(path, "imageUrl");
                imageUrl = TextOf(image, imagePath);
                imageRead = imageUrl is not null;
                if (imageUrl is not null)
                {
                    ImageUrlRule(imageUrl, imagePath);
                }
            }

            return template is not null && imageRead ? new ShopAnnouncement(template, imageUrl) : null;
        }

        private IReadOnlyList<ShopAction>? Actions(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                Error(path, "必須是動作的陣列。");
                return null;
            }

            var complete = true;
            var actions = new List<ShopAction>();
            var firstOfKind = new Dictionary<ShopActionKind, int>();
            var orderingNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var itemPath = Item(path, index);
                if (ReadAction(item, itemPath) is not { } action)
                {
                    complete = false;
                }
                else
                {
                    if (action.Kind != ShopActionKind.Ordering && !firstOfKind.TryAdd(action.Kind, index))
                    {
                        Rule(
                            Child(itemPath, "kind"),
                            $"這個情境已經有 {WireNames<ShopActionKind>.ToWire(action.Kind)}（actions[{firstOfKind[action.Kind]}]）；同一種動作最多一個。");
                    }
                    else if (action.Kind == ShopActionKind.Ordering && action.Name is { } name && !orderingNames.TryAdd(name, index))
                    {
                        Rule(Child(itemPath, "name"), $"和 actions[{orderingNames[name]}] 的名稱相同；點餐系統的名稱不能重複。");
                    }

                    actions.Add(action);
                }

                index++;
            }

            return complete ? actions : null;
        }

        private ShopAction? ReadAction(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, "必須是物件（{ … }）。");
                return null;
            }

            // The kind says which other fields belong.
            var kindPath = Child(path, "kind");
            var kindElements = element.EnumerateObject().Where(property => property.Name == "kind").ToList();
            if (kindElements.Count == 0)
            {
                Error(kindPath, "缺少這個欄位。");
                return null;
            }

            if (TextOf(kindElements[0].Value, kindPath) is not { } kindName)
            {
                return null;
            }

            if (!WireNames<ShopActionKind>.All.Contains(kindName))
            {
                Error(kindPath, $"不認得的動作種類「{kindName}」；可用：{string.Join('、', WireNames<ShopActionKind>.All)}。");
                return null;
            }

            var kind = WireNames<ShopActionKind>.Parse(kindName);
            var ordering = kind == ShopActionKind.Ordering;
            var properties = Properties(
                element,
                path,
                ordering ? OrderingActionFields : StepsActionFields,
                name => (ordering, name) switch
                {
                    (true, "steps") => "點餐系統要分別寫 pauseSteps（暫停接單的步驟）與 resumeSteps（恢復接單的步驟）。",
                    (false, "name" or "pauseSteps" or "resumeSteps") => $"只有 ordering（點餐系統）動作有 {name}。",
                    _ => null,
                });
            if (properties is null)
            {
                return null;
            }

            var modePath = Child(path, "mode");
            ShopActionMode? mode = null;
            if (RequiredString(properties, path, "mode") is { } modeName)
            {
                if (WireNames<ShopActionMode>.All.Contains(modeName))
                {
                    mode = WireNames<ShopActionMode>.Parse(modeName);
                    if (ordering && mode == ShopActionMode.Auto)
                    {
                        Rule(modePath, "點餐系統沒有可以串接的 API，只能是 reminder（提醒）。");
                    }
                }
                else
                {
                    Error(modePath, "必須是 auto（自動）或 reminder（提醒）。");
                }
            }

            if (!ordering)
            {
                var steps = Steps(properties, path, "steps");
                return mode is not null && steps is not null ? new ShopAction(kind, mode.Value, Steps: steps) : null;
            }

            var name = RequiredString(properties, path, "name");
            if (name is not null)
            {
                SingleLine(name, Child(path, "name"), OrderingNameMaxLength);
            }

            var pauseSteps = Steps(properties, path, "pauseSteps");
            var resumeSteps = Steps(properties, path, "resumeSteps");
            return mode is not null && name is not null && pauseSteps is not null && resumeSteps is not null
                ? new ShopAction(kind, mode.Value, Name: name, PauseSteps: pauseSteps, ResumeSteps: resumeSteps)
                : null;
        }

        private string? Steps(Dictionary<string, JsonElement> properties, string path, string name)
        {
            var steps = RequiredString(properties, path, name);
            if (steps is not null)
            {
                MultiLine(steps, Child(path, name), StepsMaxLength);
            }

            return steps;
        }

        private BusinessPeriod? Period(JsonElement element, string path)
        {
            var properties = Properties(element, path, PeriodFields);
            if (properties is null)
            {
                return null;
            }

            var open = RequiredTime(properties, path, "open");
            var close = RequiredTime(properties, path, "close");
            if (open is null || close is null)
            {
                return null;
            }

            if (open == close)
            {
                Rule(path, "開店與打烊時間相同；跨午夜的時段把打烊時間寫得比開店早，例如 18:00 到 02:00。");
            }

            return new BusinessPeriod(open.Value, close.Value);
        }

        /// <summary>Overlapping periods, on the week's wall clock (Monday 00:00 = minute 0), with an
        /// overnight Sunday period spilling into Monday. Each overlap is reported once, at the later
        /// period.</summary>
        private void Overlaps(List<LocatedPeriod> periods)
        {
            const int week = 7 * 24 * 60;
            var spans = periods
                .Select(period => (Period: period, Start: period.WeekMinute, End: period.WeekMinute + (int)period.Period.Length.TotalMinutes))
                .OrderBy(span => span.Start)
                .ThenBy(span => span.End)
                .ToList();
            (LocatedPeriod Period, int Start, int End)? furthest = null;
            foreach (var span in spans)
            {
                if (furthest is { } earlier && earlier.End > span.Start)
                {
                    Rule(span.Period.Path, Overlap(earlier.Period));
                }

                if (furthest is null || span.End > furthest.Value.End)
                {
                    furthest = span;
                }
            }

            foreach (var spill in spans.Where(span => span.End > week))
            {
                foreach (var span in spans.Where(span => span.Start < spill.End - week))
                {
                    Rule(span.Period.Path, Overlap(spill.Period));
                }
            }
        }

        private static string Overlap(LocatedPeriod other) =>
            $"和 {Coordinator.WeeklyHours.WireName(other.Day)}[{other.Index}] 的 {other.Period}{(other.Period.IsOvernight ? "（跨午夜）" : string.Empty)} 重疊。";

        private void TimeZoneRule(string id, string path)
        {
            if (id.Length > ShopSettings.TimeZoneIdMaxLength
                || !TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
                || !zone.HasIanaId)
            {
                Rule(path, $"不認得的時區「{id}」；請用 IANA 時區名稱，例如 {ShopSettings.DefaultTimeZoneId}。");
            }
        }

        private void ImageUrlRule(string value, string path)
        {
            if (value.Length > ImageUrlMaxLength)
            {
                Rule(path, $"最多 {ImageUrlMaxLength} 個字元（目前 {value.Length} 個）。");
            }
            else if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                     || uri.Scheme != Uri.UriSchemeHttps
                     || string.IsNullOrEmpty(uri.Host)
                     || value.Any(char.IsWhiteSpace))
            {
                Rule(path, "必須是完整的 https:// 網址（公開的圖片位址）。");
            }
        }

        /// <summary>One-line text rules; false when the text is blank.</summary>
        private bool SingleLine(string value, string path, int maxLength)
        {
            if (!strict)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                Error(path, "不能是空白。");
                return false;
            }

            if (value.Length > maxLength)
            {
                Error(path, $"最多 {maxLength} 個字（目前 {value.Length} 個）。");
            }

            if (value.Any(char.IsControl))
            {
                Error(path, "不能有換行或控制字元。");
            }
            else if (value != value.Trim())
            {
                Error(path, "前後不能有空白。");
            }

            return true;
        }

        /// <summary>Multi-line text rules (<c>\n</c> allowed); false when the text is blank.</summary>
        private bool MultiLine(string value, string path, int maxLength)
        {
            if (!strict)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                Error(path, "不能是空白。");
                return false;
            }

            if (value.Length > maxLength)
            {
                Error(path, $"最多 {maxLength} 個字（目前 {value.Length} 個）。");
            }

            if (value.Any(character => character != '\n' && char.IsControl(character)))
            {
                Error(path, "除了換行（\\n）之外不能有控制字元。");
            }

            return true;
        }

        /// <summary>The object's properties, reporting unknown (<paramref name="unknown"/> may word it
        /// for a field) and repeated ones; <see langword="null"/> when it is not an object.</summary>
        private Dictionary<string, JsonElement>? Properties(
            JsonElement element, string path, IReadOnlyCollection<string> allowed, Func<string, string?>? unknown = null)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, "必須是物件（{ … }）。");
                return null;
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = Child(path, property.Name);
                if (!allowed.Contains(property.Name))
                {
                    Error(propertyPath, unknown?.Invoke(property.Name) ?? $"不認得的欄位；這裡可以有：{string.Join('、', allowed)}。");
                }
                else if (!properties.TryAdd(property.Name, property.Value))
                {
                    Error(propertyPath, "欄位重複。");
                }
            }

            return properties;
        }

        private string? RequiredString(Dictionary<string, JsonElement> properties, string path, string name) =>
            properties.TryGetValue(name, out var value) ? TextOf(value, Child(path, name)) : Missing<string>(Child(path, name));

        private bool? RequiredBoolean(Dictionary<string, JsonElement> properties, string path, string name)
        {
            var propertyPath = Child(path, name);
            if (!properties.TryGetValue(name, out var value))
            {
                Error(propertyPath, "缺少這個欄位。");
                return null;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            Error(propertyPath, "必須是 true 或 false。");
            return null;
        }

        /// <summary>An optional whole number (<paramref name="fallback"/> when absent) from <paramref name="min"/> to
        /// <paramref name="max"/> (the range is a rule); <see langword="null"/> when it is not a whole number.</summary>
        private int? Integer(Dictionary<string, JsonElement> properties, string path, string name, int fallback, int min, int max)
        {
            if (!properties.TryGetValue(name, out var value))
            {
                return fallback;
            }

            var propertyPath = Child(path, name);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            {
                Error(propertyPath, "必須是整數。");
                return null;
            }

            if (number < min || number > max)
            {
                Rule(propertyPath, $"必須是 {min} 到 {max} 的整數（目前 {number}）。");
            }

            return number;
        }

        private TimeOnly? RequiredTime(Dictionary<string, JsonElement> properties, string path, string name)
        {
            var propertyPath = Child(path, name);
            if (RequiredString(properties, path, name) is not { } text)
            {
                return null;
            }

            if (!ClockTime().IsMatch(text))
            {
                Error(propertyPath, "必須是 24 小時制的 HH:mm，例如 09:30。");
                return null;
            }

            return new TimeOnly(int.Parse(text[..2], CultureInfo.InvariantCulture), int.Parse(text[3..], CultureInfo.InvariantCulture));
        }

        private string? TextOf(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            Error(path, "必須是字串。");
            return null;
        }

        private T? Missing<T>(string path)
            where T : class
        {
            Error(path, "缺少這個欄位。");
            return null;
        }

        private void Error(string path, string message) => Errors.Add(new ShopSettingsError(path, message));

        private void Rule(string path, string message)
        {
            if (strict)
            {
                Error(path, message);
            }
        }
    }

    private sealed record LocatedPeriod(DayOfWeek Day, int Index, BusinessPeriod Period, string Path)
    {
        /// <summary>Minutes from Monday 00:00 to the opening.</summary>
        public int WeekMinute => (((int)Day + 6) % 7 * 24 * 60) + (Period.Open.Hour * 60) + Period.Open.Minute;
    }
}
