namespace SmartAgri.Domain.Ordering;

/// <summary>One line of a cart as the customer sends it: an item, how many and the options chosen — never a price.</summary>
public sealed record OrderingLineInput(string? ItemId, int Quantity, IReadOnlyList<OptionSelection>? Selections, string? Comment);

/// <summary>What is wrong with line <see cref="Line"/> (0-based) of a cart.</summary>
/// <param name="Code">A <see cref="PricingErrorCode"/>'s wire name (<c>too-few</c>, <c>unknown-option</c> …), or why the
/// item cannot be had at the pickup time: <see cref="OrderingCart.SoldOutToday"/>, <see cref="OrderingCart.Unavailable"/>,
/// <see cref="OrderingCart.CategoryUnavailable"/>.</param>
/// <param name="Path">Where in the line (<see cref="PricingError.Path"/>); empty for the item or the quantity.</param>
public sealed record OrderingLineError(int Line, string Code, string Path);

/// <summary>A cart priced by the server, or every error found.</summary>
public sealed record OrderingCartResult(IReadOnlyList<OrderingOrderLine> Lines, IReadOnlyList<OrderingLineError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>The sum of the lines' totals.</summary>
    public long Subtotal => Lines.Sum(line => (long)line.LineTotal);
}

/// <summary>
/// A whole cart against a menu (online-ordering design D6, D7, Slice O2, #398): every line priced by
/// <see cref="MenuPricing"/> (the customer never sends a price), then whether each item can be had for the pickup time —
/// not 停售, not 今日售完 until after the pickup (<see cref="OrderingItemState.IsItemAvailable"/>), and its category offered
/// then (<see cref="MenuCategory.IsVisibleAt"/>). Pure; every error is reported, with its line.
/// </summary>
public static class OrderingCart
{
    /// <summary>The item is 今日售完 at the pickup time.</summary>
    public const string SoldOutToday = "sold-out-today";

    /// <summary>The item is 停售.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The item's category is not offered at the pickup time.</summary>
    public const string CategoryUnavailable = "category-unavailable";

    /// <summary>Prices every line of <paramref name="lines"/> on <paramref name="menu"/>; the comments are the caller's to
    /// check (<see cref="OrderingContact.LineCommentProblem"/>) and are kept as given.</summary>
    public static OrderingCartResult Price(MenuDocument menu, IReadOnlyList<OrderingLineInput> lines)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(lines);
        var priced = new List<OrderingOrderLine>();
        var errors = new List<OrderingLineError>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var result = MenuPricing.Price(menu, line.ItemId ?? string.Empty, line.Quantity, line.Selections);
            if (result.IsValid)
            {
                priced.Add(OrderingOrderLine.Of(result.Line!, line.Comment));
            }
            else
            {
                errors.AddRange(result.Errors.Select(error => new OrderingLineError(index, WireNames<PricingErrorCode>.ToWire(error.Code), error.Path)));
            }
        }

        return errors.Count > 0 ? new OrderingCartResult([], errors) : new OrderingCartResult(priced, []);
    }

    /// <summary>The lines of <paramref name="lines"/> that cannot be had for a pickup at <paramref name="pickupAt"/> (on the
    /// shop's clock in <paramref name="zone"/>), given the shop's item <paramref name="states"/>; empty when all can.</summary>
    public static IReadOnlyList<OrderingLineError> Unobtainable(
        MenuDocument menu,
        IReadOnlyList<OrderingItemState> states,
        IReadOnlyList<OrderingOrderLine> lines,
        DateTimeOffset pickupAt,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(zone);
        var errors = new List<OrderingLineError>();
        for (var index = 0; index < lines.Count; index++)
        {
            var itemId = lines[index].ItemId;
            if (menu.FindItem(itemId) is not { } found)
            {
                errors.Add(new OrderingLineError(index, WireNames<PricingErrorCode>.ToWire(PricingErrorCode.UnknownItem), string.Empty));
                continue;
            }

            if (!found.Category.IsVisibleAt(pickupAt, zone))
            {
                errors.Add(new OrderingLineError(index, CategoryUnavailable, string.Empty));
            }
            else if (!OrderingItemState.IsItemAvailable(states, itemId, pickupAt))
            {
                var stopped = states.Any(state => state.ItemId == itemId && state.Kind == OrderingItemStateKind.Unavailable);
                errors.Add(new OrderingLineError(index, stopped ? Unavailable : SoldOutToday, string.Empty));
            }
        }

        return errors;
    }
}
