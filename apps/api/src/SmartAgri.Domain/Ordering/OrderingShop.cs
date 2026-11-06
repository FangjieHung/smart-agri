using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Ordering;

/// <summary>
/// 點餐設定 (table <c>OrderingShops</c>, online-ordering design D8, D9, Slice O1, #397): one row per organization (shop)
/// — its <see cref="OrderingShopSettings"/>, imported with <c>ordering-shop import</c>, and the staff's switches
/// <see cref="Paused"/> and <see cref="AsapPrepBand"/>, which the import leaves alone. Independent of the 統籌助理's
/// <see cref="ShopSettings"/>: the slug, name, time zone and hours are its own.
/// </summary>
/// <remarks><see cref="Version"/> goes up with every change (import or switch) and is the row's concurrency token, so
/// two people changing the shop at once cannot both win (compare-and-set, Slice O3).</remarks>
public sealed class OrderingShop : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private OrderingShop()
    {
        Slug = string.Empty;
        Name = string.Empty;
        TimeZoneId = ShopSettings.DefaultTimeZoneId;
        Phone = string.Empty;
        OrderingHours = new WeeklyHours(new Dictionary<DayOfWeek, IReadOnlyList<BusinessPeriod>>());
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>Unique across all organizations.</summary>
    public string Slug { get; private set; }

    public string Name { get; private set; }

    public string TimeZoneId { get; private set; }

    public string Phone { get; private set; }

    public string? Address { get; private set; }

    public string? ReminderNotice { get; private set; }

    public string? CheckoutNotice { get; private set; }

    public bool TakeoutScheduled { get; private set; }

    public bool TakeoutAsap { get; private set; }

    /// <summary>外送（預約）(#401).</summary>
    public bool Delivery { get; private set; }

    /// <summary>外送（儘快）(#401).</summary>
    public bool DeliveryAsap { get; private set; }

    /// <summary>The zones, hours and lead time of delivery (#401); <see langword="null"/> when the file has no
    /// <c>delivery</c> section.</summary>
    public OrderingDeliverySettings? DeliverySettings { get; private set; }

    public WeeklyHours OrderingHours { get; private set; }

    public int SlotMinutes { get; private set; }

    public int DaysAhead { get; private set; }

    public int MinLeadMinutes { get; private set; }

    public int? MaxOrdersPerSlot { get; private set; }

    public bool ShowOrderComment { get; private set; }

    public bool ShowTableware { get; private set; }

    /// <summary>How long an order may wait for an answer, in ordering hours (#400).</summary>
    public int PendingTimeoutMinutes { get; private set; }

    /// <summary>暫停接單: no order is taken, scheduled or ASAP (switched by the staff, Slice O3).</summary>
    public bool Paused { get; private set; }

    /// <summary>How long an ASAP order takes now (switched by the staff, Slice O3).</summary>
    public AsapPrepBand AsapPrepBand { get; private set; }

    /// <summary>Goes up with every change; the concurrency token.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The imported settings.</summary>
    public OrderingShopSettings Settings => new(
        Slug, Name, TimeZoneId, Phone, Address, ReminderNotice, CheckoutNotice, Services, OrderingHours, SlotMinutes, DaysAhead,
        MinLeadMinutes, MaxOrdersPerSlot, ShowOrderComment, ShowTableware, PendingTimeoutMinutes, DeliverySettings);

    public OrderingServices Services => new(TakeoutScheduled, TakeoutAsap, Delivery, DeliveryAsap);

    /// <summary>The calculations over <see cref="OrderingHours"/> in the shop's time zone.</summary>
    public BusinessHours Hours => new(OrderingHours, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));

    /// <summary>Delivery is offered (scheduled; ASAP only with <see cref="DeliveryAsap"/>) and has zones.</summary>
    public bool DeliveryOffered => Delivery && DeliverySettings is { Zones.Count: > 0 };

    /// <summary>When delivery orders are delivered: the delivery section's own hours, else the takeout ordering hours.</summary>
    public WeeklyHours DeliveryHours => DeliverySettings?.OrderingHours ?? OrderingHours;

    /// <summary>The hours of <paramref name="channel"/> in the shop's time zone.</summary>
    public BusinessHours HoursFor(OrderingChannel channel) =>
        channel == OrderingChannel.Delivery ? new BusinessHours(DeliveryHours, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId)) : Hours;

    /// <summary>The earliest a scheduled slot of <paramref name="channel"/> can be after now.</summary>
    public int MinLeadMinutesFor(OrderingChannel channel) =>
        channel == OrderingChannel.Delivery
            ? DeliverySettings?.MinLeadMinutes ?? OrderingDeliverySettings.DefaultMinLeadMinutes
            : MinLeadMinutes;

    /// <summary>The first settings of <paramref name="organizationId"/>: taking orders, ASAP band
    /// <see cref="AsapPrepBands.Default"/>, version 1.</summary>
    public static OrderingShop Create(Guid organizationId, OrderingShopSettings settings, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An ordering shop always belongs to an organization.", nameof(organizationId));
        }

        ArgumentNullException.ThrowIfNull(settings);
        var shop = new OrderingShop
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AsapPrepBand = AsapPrepBands.Default,
            CreatedAt = now,
        };
        shop.Set(settings, now);
        shop.Version = 1;
        return shop;
    }

    /// <summary>Replaces the imported settings and names the fields that changed; nothing changes (not even
    /// <see cref="Version"/>) when they are the same.</summary>
    public IReadOnlyList<string> Replace(OrderingShopSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var changed = OrderingShopChange.Between(Settings, settings);
        if (changed.Count > 0)
        {
            Set(settings, now);
            Version++;
        }

        return changed;
    }

    /// <summary>Pauses or resumes taking orders; false when it already was so.</summary>
    public bool SetPaused(bool paused, DateTimeOffset now)
    {
        if (Paused == paused)
        {
            return false;
        }

        Paused = paused;
        Touch(now);
        return true;
    }

    /// <summary>Sets how long an ASAP order takes; false when it already was so.</summary>
    public bool SetAsapPrepBand(AsapPrepBand band, DateTimeOffset now)
    {
        if (!Enum.IsDefined(band))
        {
            throw new ArgumentOutOfRangeException(nameof(band), band, "Not an ASAP preparation band.");
        }

        if (AsapPrepBand == band)
        {
            return false;
        }

        AsapPrepBand = band;
        Touch(now);
        return true;
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    private void Set(OrderingShopSettings settings, DateTimeOffset now)
    {
        Slug = settings.Slug;
        Name = settings.Name;
        TimeZoneId = settings.TimeZoneId;
        Phone = settings.Phone;
        Address = settings.Address;
        ReminderNotice = settings.ReminderNotice;
        CheckoutNotice = settings.CheckoutNotice;
        TakeoutScheduled = settings.Services.TakeoutScheduled;
        TakeoutAsap = settings.Services.TakeoutAsap;
        Delivery = settings.Services.Delivery;
        DeliveryAsap = settings.Services.DeliveryAsap;
        DeliverySettings = settings.Delivery;
        OrderingHours = settings.OrderingHours;
        SlotMinutes = settings.SlotMinutes;
        DaysAhead = settings.DaysAhead;
        MinLeadMinutes = settings.MinLeadMinutes;
        MaxOrdersPerSlot = settings.MaxOrdersPerSlot;
        ShowOrderComment = settings.ShowOrderComment;
        ShowTableware = settings.ShowTableware;
        PendingTimeoutMinutes = settings.PendingTimeoutMinutes;
        UpdatedAt = now;
    }
}
