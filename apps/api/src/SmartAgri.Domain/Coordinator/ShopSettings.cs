using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Coordinator;

/// <summary>
/// 店家設定 (table <c>CoordinatorShopSettings</c>, store-coordinator design §5 Slice 2, decisions A and B,
/// #373): one row per organization (shop) holding its <see cref="ShopSettingsDocument"/> — the store
/// name, time zone, weekly hours and the four scenarios. Set by the platform operator with
/// <c>coordinator-shop import</c>, which validates the whole file first (<see cref="ShopSettingsJson"/>);
/// read whole, one organization at a time, by the 統籌助理.
/// </summary>
/// <remarks>
/// <see cref="WeeklyHours"/> and <see cref="Scenarios"/> are <c>jsonb</c> in the settings file's own
/// format. A change writes one <see cref="OrganizationActivityAction.CoordinatorShopSettingsUpdated"/>
/// naming the sections and scenarios that changed — never their text.
/// </remarks>
public sealed class ShopSettings : IOrganizationScoped
{
    /// <summary>The time zone of a shop that has not said otherwise.</summary>
    public const string DefaultTimeZoneId = "Asia/Taipei";

    /// <summary>Longest store name (characters).</summary>
    public const int StoreNameMaxLength = 50;

    /// <summary>Longest time zone id stored.</summary>
    public const int TimeZoneIdMaxLength = 64;

    /// <summary>The settings file's top-level sections, as <see cref="ShopSettingsChange.Sections"/> names them.</summary>
    public static readonly IReadOnlyList<string> SectionNames = ["storeName", "timeZone", "weeklyHours", "scenarios"];

    /// <summary>For EF Core materialization.</summary>
    private ShopSettings()
    {
        StoreName = string.Empty;
        TimeZoneId = DefaultTimeZoneId;
        WeeklyHours = new WeeklyHours(new Dictionary<DayOfWeek, IReadOnlyList<BusinessPeriod>>());
        Scenarios = new Dictionary<ShopScenario, ShopScenarioSettings>();
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The name the 統籌助理 shows for the shop (in place of the organization's name).</summary>
    public string StoreName { get; private set; }

    /// <summary>The IANA time zone the shop's dates and times are in.</summary>
    public string TimeZoneId { get; private set; }

    public WeeklyHours WeeklyHours { get; private set; }

    public IReadOnlyDictionary<ShopScenario, ShopScenarioSettings> Scenarios { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When an import last changed something.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The stored settings, as the file has them.</summary>
    public ShopSettingsDocument Document => new(StoreName, TimeZoneId, WeeklyHours, Scenarios);

    /// <summary>The first settings of <paramref name="organizationId"/>.</summary>
    public static ShopSettings Create(Guid organizationId, ShopSettingsDocument document, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Shop settings always belong to an organization.", nameof(organizationId));
        }

        ArgumentNullException.ThrowIfNull(document);
        var settings = new ShopSettings
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            CreatedAt = now,
        };
        settings.Set(document, now);
        return settings;
    }

    /// <summary>Replaces the settings with <paramref name="document"/> and says what changed; nothing
    /// (not even <see cref="UpdatedAt"/>) changes when it is the same.</summary>
    public ShopSettingsChange Replace(ShopSettingsDocument document, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        var change = ShopSettingsChange.Between(Document, document);
        if (change.IsEmpty)
        {
            return change;
        }

        Set(document, now);
        return change;
    }

    private void Set(ShopSettingsDocument document, DateTimeOffset now)
    {
        if (!Enum.GetValues<ShopScenario>().All(document.Scenarios.ContainsKey))
        {
            throw new ArgumentException("Shop settings have all four scenarios.", nameof(document));
        }

        StoreName = document.StoreName;
        TimeZoneId = document.TimeZoneId;
        WeeklyHours = document.WeeklyHours;
        Scenarios = new Dictionary<ShopScenario, ShopScenarioSettings>(document.Scenarios);
        UpdatedAt = now;
    }
}

/// <summary>What an import changed: top-level sections (<see cref="ShopSettings.SectionNames"/>) and,
/// within <c>scenarios</c>, which scenarios. Wire names only, never content.</summary>
public sealed record ShopSettingsChange(IReadOnlyList<string> Sections, IReadOnlyList<ShopScenario> Scenarios)
{
    public bool IsEmpty => Sections.Count == 0;

    /// <summary>Everything, for a shop's first settings.</summary>
    public static ShopSettingsChange All { get; } = new(ShopSettings.SectionNames, Enum.GetValues<ShopScenario>());

    /// <summary>What differs between <paramref name="before"/> and <paramref name="after"/>, compared
    /// as the settings file writes them.</summary>
    public static ShopSettingsChange Between(ShopSettingsDocument before, ShopSettingsDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var scenarios = Enum.GetValues<ShopScenario>()
            .Where(scenario =>
                before.Scenarios.TryGetValue(scenario, out var old) != after.Scenarios.TryGetValue(scenario, out var current)
                || (old is not null && current is not null
                    && ShopSettingsJson.WriteScenario(scenario, old) != ShopSettingsJson.WriteScenario(scenario, current)))
            .ToList();
        var sections = new List<string>();
        if (before.StoreName != after.StoreName)
        {
            sections.Add("storeName");
        }

        if (before.TimeZoneId != after.TimeZoneId)
        {
            sections.Add("timeZone");
        }

        if (ShopSettingsJson.WriteWeeklyHours(before.WeeklyHours) != ShopSettingsJson.WriteWeeklyHours(after.WeeklyHours))
        {
            sections.Add("weeklyHours");
        }

        if (scenarios.Count > 0)
        {
            sections.Add("scenarios");
        }

        return new ShopSettingsChange(sections, scenarios);
    }
}
