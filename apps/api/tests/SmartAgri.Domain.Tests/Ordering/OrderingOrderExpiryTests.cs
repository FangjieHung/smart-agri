using Shouldly;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Domain.Tests.Ordering;

/// <summary>
/// An order's expiry and the clearing of its customer's details (online-ordering design D5, D12, Slice O4, #400): the
/// timeout runs only while ordering hours are open and never past the pickup; only a pending order expires; clearing keeps
/// everything but the customer's details, counted 90 days from the promised (else chosen) pickup.
/// </summary>
/// <remarks>The week of <see cref="OrderingSamples.Week"/>: Tuesday–Friday 11:00–14:00 and 17:00–21:00, Saturday
/// 17:00–01:00, Sunday 11:00–14:00, closed Mondays. 2026-11-10 is a Tuesday.</remarks>
public sealed class OrderingOrderExpiryTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();

    private static readonly Coordinator.BusinessHours Hours = OrderingSamples.Shop(OrderingSamples.Week()).Hours;

    [Fact]
    public void Sent_while_open_it_expires_the_timeout_later()
    {
        var sent = OrderingSamples.AtTaipei(11, 10, 12, 3);
        OrderingOrder.ExpiryFor(Hours, 15, sent, OrderingSamples.AtTaipei(11, 10, 13, 0)).ShouldBe(OrderingSamples.AtTaipei(11, 10, 12, 18));
    }

    [Fact]
    public void Sent_at_night_for_tomorrow_the_timeout_starts_when_the_hours_open()
    {
        var sent = OrderingSamples.AtTaipei(11, 10, 22, 40);
        OrderingOrder.ExpiryFor(Hours, 15, sent, OrderingSamples.AtTaipei(11, 11, 12, 0)).ShouldBe(OrderingSamples.AtTaipei(11, 11, 11, 15));
        // Between the two periods of a day: from 17:00.
        OrderingOrder.ExpiryFor(Hours, 15, OrderingSamples.AtTaipei(11, 10, 15, 0), OrderingSamples.AtTaipei(11, 10, 18, 0))
            .ShouldBe(OrderingSamples.AtTaipei(11, 10, 17, 15));
        // Sunday afternoon for Tuesday: Monday is closed.
        OrderingOrder.ExpiryFor(Hours, 15, OrderingSamples.AtTaipei(11, 15, 15, 0), OrderingSamples.AtTaipei(11, 17, 12, 0))
            .ShouldBe(OrderingSamples.AtTaipei(11, 17, 11, 15));
    }

    [Fact]
    public void Sent_after_midnight_in_an_overnight_period_it_counts_as_open()
    {
        // Saturday 17:00–01:00: Sunday 00:30 is still Saturday's period.
        var sent = OrderingSamples.AtTaipei(11, 15, 0, 30);
        OrderingOrder.ExpiryFor(Hours, 15, sent, OrderingSamples.AtTaipei(11, 15, 12, 0)).ShouldBe(OrderingSamples.AtTaipei(11, 15, 0, 45));
    }

    [Fact]
    public void It_expires_at_the_latest_at_the_pickup()
    {
        OrderingOrder.ExpiryFor(Hours, 15, OrderingSamples.AtTaipei(11, 10, 22, 0), OrderingSamples.AtTaipei(11, 11, 11, 0))
            .ShouldBe(OrderingSamples.AtTaipei(11, 11, 11, 0));
        // ASAP: sent 12:00 with an estimate up to 12:15, timeout 30.
        OrderingOrder.ExpiryFor(Hours, 30, OrderingSamples.AtTaipei(11, 10, 12, 0), OrderingSamples.AtTaipei(11, 10, 12, 15))
            .ShouldBe(OrderingSamples.AtTaipei(11, 10, 12, 15));
    }

    [Fact]
    public void A_new_order_stores_its_expiry_in_UTC()
    {
        var sent = OrderingSamples.AtTaipei(11, 10, 12, 0);
        var order = Asap(sent, timeoutMinutes: 10);

        order.ExpiresAt.ShouldBe(OrderingSamples.AtTaipei(11, 10, 12, 10));
        order.ExpiresAt.Offset.ShouldBe(TimeSpan.Zero, "Npgsql writes timestamptz only from UTC");
    }

    [Fact]
    public void Only_a_pending_order_expires_and_its_version_goes_up()
    {
        var sent = OrderingSamples.AtTaipei(11, 10, 12, 0);
        var order = Asap(sent);
        order.Expire(sent.AddMinutes(16));

        (order.Status, order.Version, order.CanCancel, order.AllowedActions.Count).ShouldBe((OrderingOrderStatus.Expired, 2, false, 0));
        order.StatusChangedAt.ShouldBe(sent.AddMinutes(16));
        Should.Throw<InvalidOperationException>(() => order.Expire(sent.AddMinutes(17)));

        var accepted = Asap(sent);
        accepted.Accept(Guid.NewGuid(), null, sent.AddMinutes(1));
        Should.Throw<InvalidOperationException>(() => accepted.Expire(sent.AddMinutes(16)));
    }

    [Fact]
    public void Clearing_keeps_the_order_but_not_the_customer_and_is_idempotent()
    {
        var sent = OrderingSamples.AtTaipei(11, 10, 12, 0);
        var order = Asap(sent);
        order.Accept(Guid.NewGuid(), null, sent.AddMinutes(1));
        var (number, total, lines) = (order.Number, order.Total, order.Lines);

        order.ClearPersonalData(sent.AddDays(91));

        (order.CustomerName, order.CustomerPhone, order.CustomerEmail, order.OrderComment).ShouldBe((string.Empty, string.Empty, null, null));
        order.Lines.ShouldAllBe(line => line.Comment == null);
        order.Lines.Select(line => (line.ItemId, line.Quantity, line.LineTotal, line.Options.Count))
            .ShouldBe(lines.Select(line => (line.ItemId, line.Quantity, line.LineTotal, line.Options.Count)));
        (order.Number, order.Total, order.Status, order.Version).ShouldBe((number, total, OrderingOrderStatus.Accepted, 2));
        order.PersonalDataClearedAt.ShouldBe(sent.AddDays(91));

        order.ClearPersonalData(sent.AddDays(92));
        order.PersonalDataClearedAt.ShouldBe(sent.AddDays(91), "cleared once");
    }

    [Fact]
    public void The_retention_counts_90_days_from_the_promised_else_the_chosen_pickup()
    {
        var sent = OrderingSamples.AtTaipei(11, 10, 12, 0);
        var order = Asap(sent);
        var pickup = order.PickupAt;
        order.PersonalDataExpired(pickup.AddDays(90).AddMinutes(-1)).ShouldBeFalse("89 days 23:59");
        order.PersonalDataExpired(pickup.AddDays(90)).ShouldBeTrue("90 days");

        var promised = pickup.AddHours(2);
        order.Accept(Guid.NewGuid(), promised, sent.AddMinutes(1));
        order.PickupInstant.ShouldBe(promised);
        order.PersonalDataExpired(pickup.AddDays(90)).ShouldBeFalse("counted from the promised time");
        order.PersonalDataExpired(promised.AddDays(90)).ShouldBeTrue();

        order.ClearPersonalData(promised.AddDays(90));
        order.PersonalDataExpired(promised.AddDays(200)).ShouldBeFalse("already cleared");
    }

    private static OrderingOrder Asap(DateTimeOffset sent, int timeoutMinutes = 15) => OrderingOrder.Submit(
        Organization, OrderingServiceType.TakeoutAsap, new DateOnly(2026, 11, 10), 1, default, new AsapEstimate(15, 30), "陳小姐", "0912345678",
        "chen@example.com", "請放門口", true,
        OrderingCart.Price(OrderingSamples.Example().Menu, [new OrderingLineInput("wonton-soup", 1, null, "不要蔥")]).Lines,
        1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), Hours, timeoutMinutes, sent);
}
