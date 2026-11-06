using Shouldly;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Domain.Tests.Ordering;

/// <summary>
/// Pricing an order line against the menu (online-ordering design D6, Slice O1, #397): every group at every level checked —
/// a required group left out, too many chosen, a repeat in a group that is not repeatable, an unknown option or group —
/// and the price of four nested levels, with the snapshot an order keeps.
/// </summary>
public class MenuPricingTests
{
    private static readonly MenuDocument Example = OrderingSamples.Example().Menu;

    private static readonly MenuDocument Nested = OrderingSamples.Menu(OrderingShopJsonTests.FourLevels(fifth: false));

    [Fact]
    public void An_item_with_its_required_choices_is_priced_with_a_snapshot_in_the_menus_order()
    {
        var result = MenuPricing.Price(
            Example,
            "beef-noodle",
            2,
            [
                new OptionSelection("toppings", "egg", 2),
                new OptionSelection("noodle", "wide"),
                new OptionSelection("size", "large"),
                new OptionSelection("toppings", "tofu"),
            ]);

        result.Errors.ShouldBeEmpty();
        var line = result.Line.ShouldNotBeNull();
        (line.ItemId, line.ItemName, line.ItemPrice, line.Quantity).ShouldBe(("beef-noodle", "紅燒牛肉麵", 150, 2));
        line.UnitPrice.ShouldBe(150 + 20 + 0 + (2 * 15) + 10);
        line.LineTotal.ShouldBe(line.UnitPrice * 2);
        line.Options.Select(option => $"{option.GroupId}/{option.OptionId}×{option.Count}").ShouldBe(
            ["size/large×1", "noodle/wide×1", "toppings/egg×2", "toppings/tofu×1"]);
        var size = line.Options[0];
        (size.GroupId, size.GroupName, size.OptionId, size.OptionName, size.Price, size.Count).ShouldBe(("size", "份量", "large", "大碗", 20, 1));
        size.Options.ShouldBeEmpty();
    }

    [Fact]
    public void A_required_group_left_out_is_too_few()
    {
        var result = MenuPricing.Price(Example, "beef-noodle", 1, [new OptionSelection("size", "regular")]);

        result.IsValid.ShouldBeFalse();
        result.Line.ShouldBeNull();
        result.Errors.ShouldBe([new PricingError(PricingErrorCode.TooFew, "noodle")]);
    }

    [Fact]
    public void More_than_the_maximum_is_too_many_counting_repeats()
    {
        Errors(Example, "beef-noodle", Required("size", "regular", "noodle", "thin"), new OptionSelection("toppings", "egg", 4), new OptionSelection("toppings", "tofu", 2))
            .ShouldBe([new PricingError(PricingErrorCode.TooMany, "toppings")]);
        Errors(Example, "beef-noodle", [new OptionSelection("size", "regular"), new OptionSelection("size", "large"), new OptionSelection("noodle", "thin")])
            .ShouldBe([new PricingError(PricingErrorCode.TooMany, "size")]);
        Errors(Example, "beef-noodle", Required("size", "regular", "noodle", "thin"), new OptionSelection("toppings", "egg", 5)).ShouldBeEmpty();
    }

    [Fact]
    public void A_repeat_in_a_group_that_is_not_repeatable_and_the_same_option_twice_are_refused()
    {
        Errors(Example, "beef-noodle", [new OptionSelection("size", "large", 2), new OptionSelection("noodle", "thin")])
            .ShouldBe([new PricingError(PricingErrorCode.NotRepeatable, "size.large"), new PricingError(PricingErrorCode.TooMany, "size")]);
        Errors(Example, "beef-noodle", Required("size", "regular", "noodle", "thin"), new OptionSelection("toppings", "egg"), new OptionSelection("toppings", "egg"))
            .ShouldBe([new PricingError(PricingErrorCode.DuplicateOption, "toppings.egg")]);
        Errors(Example, "beef-noodle", Required("size", "regular", "noodle", "thin"), new OptionSelection("toppings", "egg", 0))
            .ShouldBe([new PricingError(PricingErrorCode.InvalidCount, "toppings.egg")]);
    }

    [Fact]
    public void Unknown_items_groups_and_options_and_a_bad_quantity_are_refused()
    {
        MenuPricing.Price(Example, "pizza", 1, []).Errors.ShouldBe([new PricingError(PricingErrorCode.UnknownItem, string.Empty)]);
        Errors(Example, "beef-noodle", [new OptionSelection("size", "huge"), new OptionSelection("noodle", "thin"), new OptionSelection("sauce", "chili")])
            .ShouldBe(
            [
                new PricingError(PricingErrorCode.UnknownGroup, "sauce"),
                new PricingError(PricingErrorCode.UnknownOption, "size.huge"),
                new PricingError(PricingErrorCode.TooFew, "size"),
            ]);
        MenuPricing.Price(Example, "wonton-soup", 0, []).Errors.ShouldBe([new PricingError(PricingErrorCode.InvalidQuantity, string.Empty)]);
        MenuPricing.Price(Example, "wonton-soup", 100, []).Errors.ShouldBe([new PricingError(PricingErrorCode.InvalidQuantity, string.Empty)]);
        MenuPricing.Price(Example, "wonton-soup", 99, []).Line.ShouldNotBeNull().LineTotal.ShouldBe(70 * 99);
    }

    [Fact]
    public void A_required_group_under_an_option_is_required_only_when_the_option_is_chosen()
    {
        var basic = Required("size", "regular", "noodle", "thin");

        Errors(Example, "beef-noodle", basic).ShouldBeEmpty();
        Errors(Example, "beef-noodle", basic, new OptionSelection("combo", "set"))
            .ShouldBe([new PricingError(PricingErrorCode.TooFew, "combo.set.side"), new PricingError(PricingErrorCode.TooFew, "combo.set.drink")]);

        var line = MenuPricing.Price(
            Example,
            "beef-noodle",
            1,
            [.. basic, new OptionSelection("combo", "set", Selections: [new OptionSelection("drink", "winter-melon"), new OptionSelection("side", "seaweed")])]).Line.ShouldNotBeNull();
        line.UnitPrice.ShouldBe(150 + 60 + 10);
        var set = line.Options.Single(option => option.GroupId == "combo");
        set.Options.Select(option => option.OptionName).ShouldBe(["海帶絲", "冬瓜檸檬"]);
    }

    [Fact]
    public void Choices_under_an_option_without_groups_are_refused()
    {
        Errors(Example, "beef-noodle", [new OptionSelection("size", "regular", Selections: [new OptionSelection("drink", "black-tea")]), new OptionSelection("noodle", "thin")])
            .ShouldBe([new PricingError(PricingErrorCode.UnknownGroup, "size.regular.drink")]);
    }

    [Fact]
    public void Four_nested_levels_add_up_each_count_applying_to_what_is_under_it()
    {
        // set 100 + rice 10 + 2 × (egg 15 + fried 5 + soy 2) + tofu 10 = 164
        var selections = new[]
        {
            new OptionSelection("main", "rice", Selections:
            [
                new OptionSelection("topping", "egg", 2, [new OptionSelection("egg-style", "fried", Selections: [new OptionSelection("sauce", "soy")])]),
                new OptionSelection("topping", "tofu"),
            ]),
        };

        var line = MenuPricing.Price(Nested, "set", 3, selections).Line.ShouldNotBeNull();

        line.UnitPrice.ShouldBe(164);
        line.LineTotal.ShouldBe(492);
        var egg = line.Options.ShouldHaveSingleItem().Options[0];
        (egg.OptionName, egg.Price, egg.Count).ShouldBe(("蛋", 15, 2));
        var soy = egg.Options.ShouldHaveSingleItem().Options.ShouldHaveSingleItem();
        (soy.GroupName, soy.OptionId, soy.OptionName, soy.Price, soy.Count).ShouldBe(("醬料", "soy", "醬油", 2, 1));
        soy.Options.ShouldBeEmpty();
    }

    [Fact]
    public void Errors_deep_down_carry_their_whole_path()
    {
        var selections = new[]
        {
            new OptionSelection("main", "rice", Selections:
            [
                new OptionSelection("topping", "egg", Selections: [new OptionSelection("egg-style", "fried", Selections: [new OptionSelection("sauce", "ketchup")])]),
                new OptionSelection("topping", "tofu", Selections: []),
                new OptionSelection("topping", "egg-white"),
            ]),
        };

        Errors(Nested, "set", selections).ShouldBe(
        [
            new PricingError(PricingErrorCode.UnknownOption, "main.rice.topping.egg.egg-style.fried.sauce.ketchup"),
            new PricingError(PricingErrorCode.UnknownOption, "main.rice.topping.egg-white"),
        ]);
        Errors(Nested, "set", [new OptionSelection("main", "rice", Selections: [new OptionSelection("topping", "egg")])])
            .ShouldBe([new PricingError(PricingErrorCode.TooFew, "main.rice.topping.egg.egg-style")]);
    }

    [Fact]
    public void A_line_above_the_largest_total_is_refused()
    {
        var menu = OrderingSamples.Menu("""
            { "categories": [{ "id": "c", "name": "分類", "items": [{ "id": "feast", "name": "大餐", "price": 100000, "optionGroups": [
              { "id": "extra", "name": "加點", "min": 0, "max": 99, "repeatable": true, "options": [{ "id": "lobster", "name": "龍蝦", "price": 100000 }] }
            ] }] }] }
            """);

        MenuPricing.Price(menu, "feast", 9, []).Line.ShouldNotBeNull().LineTotal.ShouldBe(900_000);
        MenuPricing.Price(menu, "feast", 11, []).Errors.ShouldBe([new PricingError(PricingErrorCode.AmountTooLarge, string.Empty)]);
        MenuPricing.Price(menu, "feast", 99, [new OptionSelection("extra", "lobster", 99)]).Errors
            .ShouldBe([new PricingError(PricingErrorCode.AmountTooLarge, string.Empty)]);
    }

    private static OptionSelection[] Required(string group1, string option1, string group2, string option2) =>
        [new OptionSelection(group1, option1), new OptionSelection(group2, option2)];

    private static IReadOnlyList<PricingError> Errors(MenuDocument menu, string itemId, IEnumerable<OptionSelection> selections, params OptionSelection[] more) =>
        MenuPricing.Price(menu, itemId, 1, [.. selections, .. more]).Errors;
}
