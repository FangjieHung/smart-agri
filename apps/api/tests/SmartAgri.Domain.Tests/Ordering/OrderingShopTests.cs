using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Ordering;
using SmartAgri.Domain.Organizations;
using static SmartAgri.Domain.Tests.Ordering.OrderingSamples;

namespace SmartAgri.Domain.Tests.Ordering;

/// <summary>
/// A shop's ordering settings and item states (online-ordering design D7, D8, Slice O1, #397): an import changes only what
/// differs and moves the version, the staff's switches survive it, and an item sold out today is back when the next business
/// day's ordering hours open; the activity names what changed and never the text.
/// </summary>
public class OrderingShopTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void An_import_changes_only_what_differs_and_keeps_the_staffs_switches()
    {
        var import = Example();
        var shop = OrderingShop.Create(Guid.CreateVersion7(), import.Shop, At);
        (shop.Version, shop.Paused, shop.AsapPrepBand).ShouldBe((1, false, AsapPrepBand.From15To30));
        shop.SetPaused(true, At).ShouldBeTrue();
        shop.SetPaused(true, At).ShouldBeFalse();
        shop.Version.ShouldBe(2);

        shop.Replace(import.Shop, At.AddHours(1)).ShouldBeEmpty();
        (shop.Version, shop.UpdatedAt).ShouldBe((2, At));

        var changed = shop.Replace(
            import.Shop with { Name = "王記麵店（新店）", CheckoutNotice = null, Services = new OrderingServices(true, false), MaxOrdersPerSlot = null },
            At.AddHours(2));

        changed.ShouldBe(["name", "notices", "services", "maxOrdersPerSlot"]);
        (shop.Version, shop.UpdatedAt, shop.Paused, shop.TakeoutAsap).ShouldBe((3, At.AddHours(2), true, false));
        shop.Settings.Name.ShouldBe("王記麵店（新店）");
    }

    [Fact]
    public void The_ordering_activity_names_what_changed_and_the_new_menu_version_only()
    {
        var organizationId = Guid.CreateVersion7();

        var first = OrganizationActivity.OrderingShopUpdated(organizationId, At, created: true, [.. OrderingShopSettings.FieldNames, "menu"], 1);
        var menuOnly = OrganizationActivity.OrderingShopUpdated(organizationId, At, created: false, ["menu"], 4);
        var nameOnly = OrganizationActivity.OrderingShopUpdated(organizationId, At, created: false, ["name"], null);

        (first.Action, first.ActorAccountId).ShouldBe((OrganizationActivityAction.OrderingShopUpdated, (Guid?)null));
        using (var detail = JsonDocument.Parse(first.Detail!))
        {
            detail.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["created", "changed", "menuVersion"]);
            detail.RootElement.GetProperty("changed").GetArrayLength().ShouldBe(OrderingShopSettings.FieldNames.Count + 1);
        }

        menuOnly.Detail.ShouldBe("""{"created":false,"changed":["menu"],"menuVersion":4}""");
        nameOnly.Detail.ShouldBe("""{"created":false,"changed":["name"],"menuVersion":null}""");
        Should.Throw<ArgumentException>(() => OrganizationActivity.OrderingShopUpdated(organizationId, At, created: false, [], null));
        Should.Throw<ArgumentException>(() => OrganizationActivity.OrderingShopUpdated(organizationId, At, created: false, ["menu"], null));
        Should.Throw<ArgumentException>(() => OrganizationActivity.OrderingShopUpdated(organizationId, At, created: false, ["name"], 2));
    }

    [Theory]
    [InlineData(11, 10, 12, 0, 11, 11, 11)] // sold out at lunch: off for the evening too, back Wednesday at opening
    [InlineData(11, 10, 9, 0, 11, 11, 11)] // before opening: the whole of Tuesday
    [InlineData(11, 10, 22, 0, 11, 11, 11)] // after closing
    [InlineData(11, 14, 23, 0, 11, 15, 11)] // Saturday night
    [InlineData(11, 15, 0, 30, 11, 15, 11)] // in Saturday's small hours: still Saturday's business day
    [InlineData(11, 15, 15, 0, 11, 17, 11)] // Sunday afternoon: closed Monday, back Tuesday
    public void An_item_sold_out_today_is_back_when_the_next_business_days_ordering_hours_open(
        int month, int day, int hour, int minute, int backMonth, int backDay, int backHour)
    {
        var shop = Shop(Week());
        var now = AtTaipei(month, day, hour, minute);
        var back = AtTaipei(backMonth, backDay, backHour);

        var state = OrderingItemState.SoldOutToday(shop.OrganizationId, "beef-noodle", shop.Hours, now);

        (state.Kind, state.SetAt, state.ResetsAt).ShouldBe((OrderingItemStateKind.SoldOutToday, now, (DateTimeOffset?)back));
        state.ResetsAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
        state.IsAvailableAt(back.AddMinutes(-1)).ShouldBeFalse();
        state.IsAvailableAt(back).ShouldBeTrue();
    }

    [Fact]
    public void An_unavailable_item_stays_off_and_items_without_a_state_are_available()
    {
        var organizationId = Guid.CreateVersion7();
        var unavailable = OrderingItemState.Unavailable(organizationId, "zhajiang", At);
        var soldOut = OrderingItemState.SoldOutToday(organizationId, "beef-noodle", Shop(Week()).Hours, AtTaipei(11, 10, 12));
        OrderingItemState[] states = [unavailable, soldOut];

        unavailable.ResetsAt.ShouldBeNull();
        unavailable.IsAvailableAt(At.AddYears(1)).ShouldBeFalse();
        OrderingItemState.IsItemAvailable(states, "zhajiang", AtTaipei(11, 20, 12)).ShouldBeFalse();
        OrderingItemState.IsItemAvailable(states, "beef-noodle", AtTaipei(11, 10, 18)).ShouldBeFalse();
        OrderingItemState.IsItemAvailable(states, "beef-noodle", AtTaipei(11, 11, 12)).ShouldBeTrue();
        OrderingItemState.IsItemAvailable(states, "wonton-soup", AtTaipei(11, 10, 12)).ShouldBeTrue();
        Should.Throw<ArgumentException>(() => OrderingItemState.Unavailable(organizationId, "Beef Noodle", At));
    }
}
