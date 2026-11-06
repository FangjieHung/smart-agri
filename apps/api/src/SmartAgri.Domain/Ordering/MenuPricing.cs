using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Ordering;

/// <summary>A customer's choice of one option of one group, with the choices under it (the next level).</summary>
/// <param name="Count">How many of the option (more than one only in a repeatable group).</param>
/// <param name="Selections">Choices in the option's own groups; only for an option that has groups.</param>
public sealed record OptionSelection(string GroupId, string OptionId, int Count = 1, IReadOnlyList<OptionSelection>? Selections = null);

/// <summary>What is wrong with an order line (<see cref="MenuPricing.Price"/>).</summary>
public enum PricingErrorCode
{
    /// <summary>The menu has no such item.</summary>
    [JsonStringEnumMemberName("unknown-item")]
    UnknownItem,

    /// <summary>The quantity is not 1 to <see cref="MenuLimits.MaxQuantity"/>.</summary>
    [JsonStringEnumMemberName("invalid-quantity")]
    InvalidQuantity,

    /// <summary>The item, or the option chosen, has no such group (also: choices under an option without groups).</summary>
    [JsonStringEnumMemberName("unknown-group")]
    UnknownGroup,

    /// <summary>The group has no such option.</summary>
    [JsonStringEnumMemberName("unknown-option")]
    UnknownOption,

    /// <summary>The same option is chosen twice in its group (a repeat is one choice with a count).</summary>
    [JsonStringEnumMemberName("duplicate-option")]
    DuplicateOption,

    /// <summary>A count is not 1 to <see cref="MenuLimits.MaxCount"/>.</summary>
    [JsonStringEnumMemberName("invalid-count")]
    InvalidCount,

    /// <summary>A count above 1 in a group that is not repeatable.</summary>
    [JsonStringEnumMemberName("not-repeatable")]
    NotRepeatable,

    /// <summary>Fewer chosen than the group's minimum (a required group left out).</summary>
    [JsonStringEnumMemberName("too-few")]
    TooFew,

    /// <summary>More chosen than the group's maximum.</summary>
    [JsonStringEnumMemberName("too-many")]
    TooMany,

    /// <summary>The line total is above <see cref="MenuLimits.MaxLineTotal"/>.</summary>
    [JsonStringEnumMemberName("amount-too-large")]
    AmountTooLarge,
}

/// <summary>One thing wrong with an order line, and where: the group and option ids from the item down, joined by
/// <c>.</c> (<c>set.salad.dressing</c> is the <c>dressing</c> group under option <c>salad</c> of group <c>set</c>); empty for
/// the item or the quantity.</summary>
public sealed record PricingError(PricingErrorCode Code, string Path);

/// <summary>One chosen option as an order line keeps it: names and prices as the menu had them then.</summary>
/// <param name="Price">The option's own price, per count.</param>
/// <param name="Options">The options chosen under it, in the menu's order.</param>
public sealed record ChosenOption(
    string GroupId, string GroupName, string OptionId, string OptionName, int Price, int Count, IReadOnlyList<ChosenOption> Options);

/// <summary>An order line priced by the server (design D6): the snapshot a later order keeps, whatever the menu becomes.</summary>
/// <param name="ItemPrice">The item's own price.</param>
/// <param name="UnitPrice">One of the item with its options: the item's price plus each chosen option's
/// <c>count × (price + the options under it)</c>.</param>
/// <param name="LineTotal"><see cref="UnitPrice"/> × <see cref="Quantity"/>.</param>
/// <param name="Options">The chosen options, groups and options in the menu's order.</param>
public sealed record PricedLine(
    string ItemId, string ItemName, int ItemPrice, int Quantity, int UnitPrice, int LineTotal, IReadOnlyList<ChosenOption> Options);

/// <summary>A priced line, or every error found.</summary>
public sealed record PricingResult(PricedLine? Line, IReadOnlyList<PricingError> Errors)
{
    public bool IsValid => Line is not null && Errors.Count == 0;
}

/// <summary>
/// Prices an order line against a menu (online-ordering design D6, Slice O1, #397): checks every group at every level
/// and computes the unit price and the line total in whole NT$, never trusting a price from the customer. Pure.
/// </summary>
/// <remarks>
/// <para>
/// Every group of the item is checked, chosen or not: the options chosen exist and each at most once (a repeat is a
/// count), counts are 1 to <see cref="MenuLimits.MaxCount"/> and above 1 only in a repeatable group, and the counts add
/// up to <see cref="OptionGroup.Min"/>–<see cref="OptionGroup.Max"/>. A chosen option's own groups are checked the same
/// way — so a required group under an option is required only when that option is chosen — and choices under an option
/// without groups, or for a group the item does not have, are refused. Every error is reported, not only the first.
/// </para>
/// <para>An option chosen <c>n</c> times adds <c>n × (its price + what is chosen under it)</c>: the choices under it
/// apply to each.</para>
/// </remarks>
public static class MenuPricing
{
    private const char Separator = '.';

    public static PricingResult Price(MenuDocument menu, string itemId, int quantity, IReadOnlyList<OptionSelection>? selections)
    {
        ArgumentNullException.ThrowIfNull(menu);
        var errors = new List<PricingError>();
        if (menu.FindItem(itemId) is not { } found)
        {
            return new PricingResult(null, [new PricingError(PricingErrorCode.UnknownItem, string.Empty)]);
        }

        var item = found.Item;

        if (quantity is < 1 or > MenuLimits.MaxQuantity)
        {
            errors.Add(new PricingError(PricingErrorCode.InvalidQuantity, string.Empty));
        }

        var (extra, options) = Check(item.OptionGroups, selections ?? [], string.Empty, errors);
        if (errors.Count > 0)
        {
            return new PricingResult(null, errors);
        }

        var unit = item.Price + extra;
        var total = unit * quantity;
        if (total > MenuLimits.MaxLineTotal)
        {
            return new PricingResult(null, [new PricingError(PricingErrorCode.AmountTooLarge, string.Empty)]);
        }

        return new PricingResult(new PricedLine(item.Id, item.Name, item.Price, quantity, (int)unit, (int)total, options), []);
    }

    /// <summary>The price <paramref name="selections"/> add under <paramref name="groups"/>, and what they choose; errors
    /// go to <paramref name="errors"/>. In <see cref="long"/>: four levels of counts can exceed an <see cref="int"/>.</summary>
    private static (long Extra, IReadOnlyList<ChosenOption> Options) Check(
        IReadOnlyList<OptionGroup> groups, IReadOnlyList<OptionSelection> selections, string path, List<PricingError> errors)
    {
        foreach (var selection in selections)
        {
            ArgumentNullException.ThrowIfNull(selection);
            if (!groups.Any(group => string.Equals(group.Id, selection.GroupId, StringComparison.Ordinal)))
            {
                errors.Add(new PricingError(PricingErrorCode.UnknownGroup, Join(path, selection.GroupId)));
            }
        }

        long extra = 0;
        var chosen = new List<(int Group, int Option, ChosenOption Choice)>();
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            var groupPath = Join(path, group.Id);
            var count = 0L;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var selection in selections.Where(selection => string.Equals(selection.GroupId, group.Id, StringComparison.Ordinal)))
            {
                var optionIndex = IndexOf(group.Options, selection.OptionId);
                var optionPath = Join(groupPath, selection.OptionId);
                if (optionIndex < 0)
                {
                    errors.Add(new PricingError(PricingErrorCode.UnknownOption, optionPath));
                    continue;
                }

                if (!seen.Add(selection.OptionId))
                {
                    errors.Add(new PricingError(PricingErrorCode.DuplicateOption, optionPath));
                    continue;
                }

                if (selection.Count is < 1 or > MenuLimits.MaxCount)
                {
                    errors.Add(new PricingError(PricingErrorCode.InvalidCount, optionPath));
                    continue;
                }

                if (selection.Count > 1 && !group.Repeatable)
                {
                    errors.Add(new PricingError(PricingErrorCode.NotRepeatable, optionPath));
                }

                count += selection.Count;
                var option = group.Options[optionIndex];
                var (nested, under) = Check(option.OptionGroups, selection.Selections ?? [], optionPath, errors);
                extra += selection.Count * (option.Price + nested);
                chosen.Add((groupIndex, optionIndex, new ChosenOption(group.Id, group.Name, option.Id, option.Name, option.Price, selection.Count, under)));
            }

            if (count < group.Min)
            {
                errors.Add(new PricingError(PricingErrorCode.TooFew, groupPath));
            }
            else if (count > group.Max)
            {
                errors.Add(new PricingError(PricingErrorCode.TooMany, groupPath));
            }
        }

        return (extra, [.. chosen.OrderBy(entry => entry.Group).ThenBy(entry => entry.Option).Select(entry => entry.Choice)]);
    }

    private static int IndexOf(IReadOnlyList<MenuOption> options, string? optionId)
    {
        for (var index = 0; index < options.Count; index++)
        {
            if (string.Equals(options[index].Id, optionId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string Join(string path, string? id) => path.Length == 0 ? id ?? string.Empty : $"{path}{Separator}{id}";
}
