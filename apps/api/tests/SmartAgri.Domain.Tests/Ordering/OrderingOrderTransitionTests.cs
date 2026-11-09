using Shouldly;
using SmartAgri.Domain.Ordering;

namespace SmartAgri.Domain.Tests.Ordering;

/// <summary>
/// The staff's steps on an order (online-ordering design D5, Slice O3, #399): which action each status allows, who and
/// when every step records, the promised time's range, the reasons and notes, the customer's cancellation, and that every
/// change raises the version once.
/// </summary>
public class OrderingOrderTransitionTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();

    private static readonly Guid Member = Guid.CreateVersion7();

    private static readonly DateTimeOffset Sent = OrderingSamples.AtTaipei(11, 10, 12, 3);

    private static readonly DateTimeOffset Slot = OrderingSamples.AtTaipei(11, 10, 12, 30);

    [Fact]
    public void Each_status_allows_exactly_its_actions()
    {
        var expected = new Dictionary<OrderingOrderStatus, OrderingOrderAction[]>
        {
            [OrderingOrderStatus.Pending] = [OrderingOrderAction.Accept, OrderingOrderAction.Decline],
            [OrderingOrderStatus.Accepted] = [OrderingOrderAction.MarkReady, OrderingOrderAction.Complete, OrderingOrderAction.Cancel],
            [OrderingOrderStatus.Ready] = [OrderingOrderAction.Complete, OrderingOrderAction.Cancel],
            [OrderingOrderStatus.Completed] = [],
            [OrderingOrderStatus.Declined] = [],
            [OrderingOrderStatus.Cancelled] = [],
            [OrderingOrderStatus.Expired] = [],
        };

        foreach (var status in Enum.GetValues<OrderingOrderStatus>())
        {
            Enum.GetValues<OrderingOrderAction>().Where(action => OrderingOrder.Allows(status, action)).ShouldBe(expected[status], status.ToString());
        }
    }

    [Fact]
    public void Accepted_ready_and_completed_record_who_and_when_and_raise_the_version_each_time()
    {
        var order = Scheduled();
        var accepted = Sent.AddMinutes(2);

        order.Accept(Member, null, accepted);

        (order.Status, order.Version, order.CanCancel).ShouldBe((OrderingOrderStatus.Accepted, 2, false));
        (order.AcceptedAt, order.AcceptedByAccountId, order.PromisedAt).ShouldBe((accepted, Member, Slot));
        order.StatusChangedAt.ShouldBe(accepted);
        order.AllowedActions.ShouldBe([OrderingOrderAction.MarkReady, OrderingOrderAction.Complete, OrderingOrderAction.Cancel]);

        var other = Guid.CreateVersion7();
        order.MarkReady(other, accepted.AddMinutes(20));
        (order.Status, order.Version, order.ReadyByAccountId, order.ReadyAt).ShouldBe((OrderingOrderStatus.Ready, 3, other, accepted.AddMinutes(20)));

        order.Complete(Member, accepted.AddMinutes(30));
        (order.Status, order.Version, order.CompletedByAccountId).ShouldBe((OrderingOrderStatus.Completed, 4, Member));
        order.AllowedActions.ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => order.CancelByShop(Member, OrderingCancelReason.CustomerNoShow, null, accepted.AddHours(1)));
        order.Version.ShouldBe(4);
    }

    [Fact]
    public void An_accepted_order_can_be_completed_without_being_marked_ready()
    {
        var order = Scheduled();
        order.Accept(Member, null, Sent);
        order.Complete(Member, Sent.AddMinutes(30));

        (order.Status, order.ReadyAt).ShouldBe((OrderingOrderStatus.Completed, null));
    }

    [Fact]
    public void A_scheduled_order_is_promised_at_its_time_or_later_never_earlier()
    {
        var order = Scheduled();

        order.PromisedAtProblem(null, Sent).ShouldBeNull();
        order.PromisedAtProblem(Slot, Sent).ShouldBeNull();
        order.PromisedAtProblem(Slot.AddMinutes(15), Sent).ShouldBeNull();
        order.PromisedAtProblem(Slot.AddMinutes(-1), Sent).ShouldNotBeNull();
        order.PromisedAtProblem(Slot.AddHours(24), Sent).ShouldBeNull();
        order.PromisedAtProblem(Slot.AddHours(24).AddMinutes(1), Sent).ShouldNotBeNull();
        Should.Throw<ArgumentException>(() => order.Accept(Member, Slot.AddMinutes(-5), Sent));
        order.Status.ShouldBe(OrderingOrderStatus.Pending);

        order.Accept(Member, Slot.AddMinutes(15), Sent);
        order.PromisedAt.ShouldBe(Slot.AddMinutes(15));
        order.PromisedAt!.Value.Offset.ShouldBe(TimeSpan.Zero, "Npgsql writes timestamptz only from UTC");
        order.PickupAt.ShouldBe(Slot, "what the customer chose stays: it holds the slot's place");
    }

    [Fact]
    public void An_asap_order_is_promised_from_now_and_by_default_at_its_estimate_or_now_when_that_passed()
    {
        var early = Asap();
        early.PromisedAtProblem(Sent.AddMinutes(10), Sent.AddMinutes(1)).ShouldBeNull("earlier than the estimate is fine");
        early.PromisedAtProblem(Sent.AddMinutes(-2), Sent).ShouldNotBeNull();
        early.Accept(Member, null, Sent.AddMinutes(1));
        early.PromisedAt.ShouldBe(Sent.AddMinutes(30));

        var late = Asap();
        var accepted = Sent.AddMinutes(45);
        late.Accept(Member, null, accepted);
        late.PromisedAt.ShouldBe(accepted, "the estimate's end passed before anybody answered");
    }

    [Fact]
    public void A_decline_keeps_its_reason_and_note_and_other_needs_a_note()
    {
        OrderingOrder.StaffNoteProblem(null, required: false).ShouldBeNull();
        OrderingOrder.StaffNoteProblem(null, required: true).ShouldNotBeNull();
        OrderingOrder.StaffNoteProblem(new string('字', 100), required: true).ShouldBeNull();
        OrderingOrder.StaffNoteProblem(new string('字', 101), required: false).ShouldNotBeNull();
        OrderingOrder.StaffNoteProblem("第一行\n第二行", required: false).ShouldNotBeNull();

        var order = Scheduled();
        Should.Throw<ArgumentException>(() => order.Decline(Member, OrderingDeclineReason.Other, null, Sent));
        order.Decline(Member, OrderingDeclineReason.SoldOut, "牛肉麵賣完了", Sent.AddMinutes(1));

        (order.Status, order.Version, order.DeclineReason, order.DeclineNote, order.DeclinedByAccountId)
            .ShouldBe((OrderingOrderStatus.Declined, 2, OrderingDeclineReason.SoldOut, "牛肉麵賣完了", Member));
        order.DeclinedAt.ShouldBe(Sent.AddMinutes(1));
        Should.Throw<InvalidOperationException>(() => order.Accept(Member, null, Sent.AddMinutes(2)));
        Should.Throw<InvalidOperationException>(() => order.CancelByCustomer(Sent.AddMinutes(2)));
    }

    [Fact]
    public void The_shop_cancels_an_accepted_or_ready_order_and_the_customer_only_a_pending_one()
    {
        var accepted = Scheduled();
        accepted.Accept(Member, null, Sent);
        Should.Throw<InvalidOperationException>(() => accepted.CancelByCustomer(Sent.AddMinutes(1)));
        accepted.CancelByShop(Member, OrderingCancelReason.CustomerNoShow, null, Slot.AddMinutes(40));

        (accepted.Status, accepted.CancelledBy, accepted.CancelledByAccountId, accepted.CancelReason, accepted.CancelNote)
            .ShouldBe((OrderingOrderStatus.Cancelled, OrderingCanceller.Shop, Member, OrderingCancelReason.CustomerNoShow, null));
        accepted.CancelledAt.ShouldBe(Slot.AddMinutes(40));
        accepted.Version.ShouldBe(3);

        var pending = Scheduled();
        Should.Throw<InvalidOperationException>(() => pending.CancelByShop(Member, OrderingCancelReason.Other, "店裡停電", Sent));
        pending.CancelByCustomer(Sent.AddMinutes(1));
        (pending.Status, pending.CancelledBy, pending.CancelledByAccountId, pending.CancelReason, pending.Version)
            .ShouldBe((OrderingOrderStatus.Cancelled, OrderingCanceller.Customer, null, null, 2));
    }

    [Fact]
    public void A_step_names_its_member()
    {
        Should.Throw<ArgumentException>(() => Scheduled().Accept(Guid.Empty, null, Sent));
    }

    private static OrderingOrder Scheduled() => OrderingOrder.Submit(
        Organization, OrderingServiceType.TakeoutScheduled, new DateOnly(2026, 11, 10), 1, Slot, null, "陳小姐", "0912345678", null, null, null,
        Lines(), 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), Sent);

    private static OrderingOrder Asap() => OrderingOrder.Submit(
        Organization, OrderingServiceType.TakeoutAsap, new DateOnly(2026, 11, 10), 2, default, new AsapEstimate(15, 30), "陳小姐", "0912345678",
        null, null, null, Lines(), 1, OrderingOrder.NewStatusToken(), Guid.NewGuid().ToString("D"), Sent);

    private static IReadOnlyList<OrderingOrderLine> Lines() =>
        OrderingCart.Price(OrderingSamples.Example().Menu, [new OrderingLineInput("wonton-soup", 1, null, null)]).Lines;
}
