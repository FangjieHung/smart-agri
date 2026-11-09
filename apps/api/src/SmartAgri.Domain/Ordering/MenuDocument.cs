using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Ordering;

/// <summary>
/// A shop's online-ordering menu (online-ordering design §4 D6, Slice O1, #397): categories of items, each item with
/// option groups whose options may carry groups of their own, at most <see cref="MenuLimits.MaxGroupLevels"/> levels
/// deep. Read from the import file's <c>menu</c> and stored as one <see cref="OrderingMenu"/> version by
/// <see cref="OrderingShopJson"/>, which also validates it; immutable.
/// </summary>
/// <remarks>Ids are the author's stable strings (<see cref="MenuLimits.IsValidId"/>): item ids are unique within the
/// menu (an order line names its item by id), category ids within the menu, group ids within their item or option,
/// option ids within their group. Prices are whole New Taiwan dollars.</remarks>
public sealed record MenuDocument(IReadOnlyList<MenuCategory> Categories)
{
    /// <summary>Every item, category by category.</summary>
    public IEnumerable<MenuItem> Items => Categories.SelectMany(category => category.Items);

    /// <summary>The item <paramref name="itemId"/> and its category; <see langword="null"/> when the menu has none.</summary>
    public (MenuCategory Category, MenuItem Item)? FindItem(string? itemId)
    {
        foreach (var category in Categories)
        {
            foreach (var item in category.Items)
            {
                if (string.Equals(item.Id, itemId, StringComparison.Ordinal))
                {
                    return (category, item);
                }
            }
        }

        return null;
    }
}

/// <summary>A menu category: a tab of the ordering page.</summary>
/// <param name="Availability">When it is offered, by pickup time; <see langword="null"/> = always.</param>
public sealed record MenuCategory(
    string Id, string Name, string? Description, MenuAvailability? Availability, IReadOnlyList<MenuItem> Items)
{
    /// <summary>Whether the category is offered for an order picked up at <paramref name="pickup"/>, read on the shop's
    /// clock in <paramref name="zone"/> (<see cref="MenuAvailability.Includes"/>).</summary>
    public bool IsVisibleAt(DateTimeOffset pickup, TimeZoneInfo zone) => Availability?.Includes(pickup, zone) ?? true;
}

/// <summary>
/// When a category is offered, by the shop's clock at pickup: on <see cref="Days"/> (all days when <see langword="null"/>)
/// from <see cref="From"/> (the day's start when <see langword="null"/>) until <see cref="To"/> (exclusive; the day's end
/// when <see langword="null"/>). A <see cref="To"/> earlier than <see cref="From"/> runs past midnight, and belongs to the
/// day it starts on: Friday's 22:00–02:00 covers Saturday 01:00.
/// </summary>
public sealed record MenuAvailability(IReadOnlyList<DayOfWeek>? Days, TimeOnly? From, TimeOnly? To)
{
    /// <summary>Runs past midnight.</summary>
    public bool IsOvernight => From is { } from && To is { } to && to < from;

    /// <summary>Whether a pickup at <paramref name="pickup"/> falls in the window, on the clock in <paramref name="zone"/>.</summary>
    public bool Includes(DateTimeOffset pickup, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(pickup, zone).DateTime;
        var time = TimeOnly.FromDateTime(local);
        var day = local.DayOfWeek;
        if (IsOvernight)
        {
            if (time >= From!.Value)
            {
                return OnDay(day);
            }

            return time < To!.Value && OnDay(local.AddDays(-1).DayOfWeek);
        }

        return (From is not { } start || time >= start) && (To is not { } end || time < end) && OnDay(day);
    }

    private bool OnDay(DayOfWeek day) => Days is null || Days.Contains(day);
}

/// <summary>A menu item.</summary>
/// <param name="Price">Its own price, before options (whole NT$, 0 or more).</param>
/// <param name="ImageUrl">An absolute <c>https</c> URL of a public image, or none.</param>
public sealed record MenuItem(
    string Id, string Name, int Price, string? Description, string? ImageUrl, IReadOnlyList<OptionGroup> OptionGroups);

/// <summary>
/// A group of choices under an item or an option: at least <see cref="Min"/> and at most <see cref="Max"/> chosen,
/// counting each option's count; an option is chosen more than once only when <see cref="Repeatable"/>.
/// </summary>
public sealed record OptionGroup(string Id, string Name, int Min, int Max, bool Repeatable, IReadOnlyList<MenuOption> Options)
{
    /// <summary>Must be chosen (<see cref="Min"/> above zero).</summary>
    public bool IsRequired => Min > 0;
}

/// <summary>One choice of a group.</summary>
/// <param name="Price">What it adds to the item's price (whole NT$, 0 or more), per count.</param>
/// <param name="OptionGroups">Groups chosen only when this option is (the next level).</param>
public sealed record MenuOption(string Id, string Name, int Price, IReadOnlyList<OptionGroup> OptionGroups);

/// <summary>The menu's limits (the import enforces them).</summary>
public static class MenuLimits
{
    /// <summary>Longest id.</summary>
    public const int IdMaxLength = 40;

    /// <summary>Longest category, item, group or option name (characters).</summary>
    public const int NameMaxLength = 40;

    /// <summary>Longest description (characters).</summary>
    public const int DescriptionMaxLength = 300;

    /// <summary>Highest item price or option price (NT$).</summary>
    public const int MaxPrice = 100_000;

    /// <summary>Deepest option groups: the item's are level 1, an option's groups one more than its group's.</summary>
    public const int MaxGroupLevels = 4;

    /// <summary>Highest <see cref="OptionGroup.Max"/>, and highest count of one option chosen.</summary>
    public const int MaxCount = 99;

    /// <summary>Most categories.</summary>
    public const int MaxCategories = 50;

    /// <summary>Most items in all.</summary>
    public const int MaxItems = 500;

    /// <summary>Most option groups under one item or option.</summary>
    public const int MaxGroupsPerParent = 20;

    /// <summary>Most options in one group.</summary>
    public const int MaxOptionsPerGroup = 50;

    /// <summary>Highest quantity of one order line.</summary>
    public const int MaxQuantity = 99;

    /// <summary>Highest order line total (NT$); a larger one is refused (<see cref="PricingErrorCode.AmountTooLarge"/>).</summary>
    public const int MaxLineTotal = 1_000_000;

    /// <summary>An id: 1–<see cref="IdMaxLength"/> lowercase letters, digits and <c>-</c>.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: >= 1 and <= IdMaxLength } && id.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>The days of a week in a menu file's order and spelling (<c>mon</c> … <c>sun</c>), as
    /// <see cref="WeeklyHours"/> has them.</summary>
    public static IReadOnlyList<DayOfWeek> Days => WeeklyHours.Days;
}
