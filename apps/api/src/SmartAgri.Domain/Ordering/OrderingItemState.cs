using System.Text.Json.Serialization;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Ordering;

/// <summary>Why an item cannot be ordered (design D7).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrderingItemStateKind>))]
public enum OrderingItemStateKind
{
    /// <summary>今日售完: back by itself when the next business day's ordering hours open.</summary>
    [JsonStringEnumMemberName("sold-out-today")]
    SoldOutToday,

    /// <summary>停售: until the staff put it back.</summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

/// <summary>
/// An item the staff took off (table <c>OrderingItemStates</c>, online-ordering design D7, Slice O1, #397): at most one
/// row per menu item id; no row means the item can be ordered. The menu stays as imported — a state names its item by id,
/// so it survives a new menu version that keeps the id. Switched from the admin in Slice O3.
/// </summary>
public sealed class OrderingItemState : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private OrderingItemState()
    {
        ItemId = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The menu item's id (<see cref="MenuItem.Id"/>).</summary>
    public string ItemId { get; private set; }

    public OrderingItemStateKind Kind { get; private set; }

    public DateTimeOffset SetAt { get; private set; }

    /// <summary>When a <see cref="OrderingItemStateKind.SoldOutToday"/> item is back (<see cref="SoldOutResetAt"/>), in
    /// UTC; <see langword="null"/> for <see cref="OrderingItemStateKind.Unavailable"/>.</summary>
    public DateTimeOffset? ResetsAt { get; private set; }

    /// <summary>今日售完 for <paramref name="itemId"/> from <paramref name="now"/>, back at
    /// <see cref="SoldOutResetAt"/> of the shop's ordering <paramref name="hours"/>.</summary>
    public static OrderingItemState SoldOutToday(Guid organizationId, string itemId, BusinessHours hours, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hours);
        var resetsAt = SoldOutResetAt(hours, now)
            ?? throw new ArgumentException("Ordering hours without any period never open again.", nameof(hours));
        return New(organizationId, itemId, OrderingItemStateKind.SoldOutToday, now, resetsAt.ToUniversalTime());
    }

    /// <summary>停售 for <paramref name="itemId"/> from <paramref name="now"/>.</summary>
    public static OrderingItemState Unavailable(Guid organizationId, string itemId, DateTimeOffset now) =>
        New(organizationId, itemId, OrderingItemStateKind.Unavailable, now, null);

    /// <summary>
    /// When an item sold out at <paramref name="now"/> is back: the first opening of the business day after the one in
    /// progress — the day of the period <paramref name="now"/> is in (an overnight period's small hours belong to the day it
    /// opened), else the shop's date at <paramref name="now"/>. So 今日售完 at lunch stays off for the evening period too,
    /// and before opening it covers the whole day. <see langword="null"/> only for hours without any period.
    /// </summary>
    public static DateTimeOffset? SoldOutResetAt(BusinessHours hours, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hours);
        var today = hours.DateAt(now);
        var businessDay = today;
        foreach (var date in new[] { today.AddDays(-1), today })
        {
            if (hours.PeriodsOn(date).Any(period => period.Opens <= now && now < period.Closes))
            {
                businessDay = date;
                break;
            }
        }

        return hours.FirstOpeningAfter(businessDay);
    }

    /// <summary>Whether the item can be ordered for <paramref name="at"/> (a pickup time, or now): never while
    /// <see cref="OrderingItemStateKind.Unavailable"/>; from <see cref="ResetsAt"/> on when sold out today.</summary>
    public bool IsAvailableAt(DateTimeOffset at) => Kind switch
    {
        OrderingItemStateKind.SoldOutToday => ResetsAt is { } resetsAt && at >= resetsAt,
        _ => false,
    };

    /// <summary>Whether <paramref name="itemId"/> can be ordered for <paramref name="at"/> given the shop's
    /// <paramref name="states"/>: yes without a state for it.</summary>
    public static bool IsItemAvailable(IEnumerable<OrderingItemState> states, string itemId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(states);
        return states.Where(state => string.Equals(state.ItemId, itemId, StringComparison.Ordinal)).All(state => state.IsAvailableAt(at));
    }

    private static OrderingItemState New(
        Guid organizationId, string itemId, OrderingItemStateKind kind, DateTimeOffset now, DateTimeOffset? resetsAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An item state always belongs to an organization.", nameof(organizationId));
        }

        if (!MenuLimits.IsValidId(itemId))
        {
            throw new ArgumentException("Not a menu item id.", nameof(itemId));
        }

        return new OrderingItemState
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            ItemId = itemId,
            Kind = kind,
            SetAt = now,
            ResetsAt = resetsAt,
        };
    }
}
