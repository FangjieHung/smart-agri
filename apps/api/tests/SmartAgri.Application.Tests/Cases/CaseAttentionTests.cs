using Shouldly;
using SmartAgri.Application.Cases;
using SmartAgri.Domain.Cases;
using Case = SmartAgri.Domain.Cases.Case;

namespace SmartAgri.Application.Tests.Cases;

/// <summary>
/// <see cref="CaseAttention"/> in memory (M7 plan §3 E, decision D; issue #250): the overdue boundary at an
/// explicit "now" (the clock the endpoints inject), 待補件 still counting, closed cases never counting, and
/// whose number an overdue case adds to — the case owner, every member of a 待受理 case's current group,
/// the new group after a transfer, and nothing extra for the manager. The API tests
/// (<c>CaseAttentionEndpointsTests</c>) run the same rule through EF Core against PostgreSQL.
/// </summary>
public class CaseAttentionTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Due = Created.AddHours(72);

    private static readonly Guid Creator = Guid.CreateVersion7();

    private static readonly Guid Aming = Guid.CreateVersion7();

    private static readonly Guid Colleague = Guid.CreateVersion7();

    private static readonly Guid Purchaser = Guid.CreateVersion7();

    private static readonly Guid Manager = Guid.CreateVersion7();

    [Fact]
    public void A_case_is_overdue_only_after_its_due_time_has_passed()
    {
        var world = World();
        var item = world.NewCase();

        Overdue(item, Due.AddTicks(-1)).ShouldBeFalse("before the due time");
        Overdue(item, Due).ShouldBeFalse("at the very moment it is due");
        Overdue(item, Due.AddTicks(1)).ShouldBeTrue("one tick later");
        Overdue(item, Due.AddDays(30)).ShouldBeTrue();
    }

    [Fact]
    public void Awaiting_info_keeps_counting_and_completed_or_cancelled_never_count()
    {
        var world = World();
        var after = Due.AddMinutes(1);

        var pending = world.NewCase();
        var inProgress = world.NewCase();
        world.Record(inProgress.Accept(Aming, Created.AddHours(1)));
        var awaiting = world.NewCase();
        world.Record(awaiting.Accept(Aming, Created.AddHours(1)));
        world.Record(awaiting.RequestInfo(Aming, "請補上溫度紀錄的照片", Created.AddHours(2)));
        var completed = world.NewCase();
        world.Record(completed.Accept(Aming, Created.AddHours(1)));
        world.Record(completed.Complete(Aming, "已更換壓縮機", Created.AddHours(3)));
        var cancelled = world.NewCase();
        world.Record(cancelled.Cancel(Creator, null, Created.AddHours(1)));

        awaiting.Status.ShouldBe(CaseStatus.AwaitingInfo);
        new[] { pending, inProgress, awaiting, completed, cancelled }.AsQueryable()
            .Where(CaseAttention.Overdue(after)).ToList()
            .ShouldBe([pending, inProgress, awaiting], ignoreOrder: true);

        // The in-memory twin agrees with the expression for every status.
        foreach (var item in new[] { pending, inProgress, awaiting, completed, cancelled })
        {
            CaseAttention.IsOverdue(item.Status, item.DueAt, after).ShouldBe(Overdue(item, after), item.Status.ToString());
            CaseAttention.IsOverdue(item.Status, item.DueAt, Due).ShouldBeFalse(item.Status.ToString());
        }
    }

    [Fact]
    public void An_accepted_overdue_case_counts_only_for_its_owner()
    {
        var world = World();
        var item = world.NewCase();
        world.Record(item.Accept(Aming, Created.AddHours(1)));
        var after = Due.AddMinutes(1);

        CountsFor(world, [item], Aming, after).ShouldBe(new Counts(1, 0, 0));
        CountsFor(world, [item], Colleague, after).ShouldBe(new Counts(0, 0, 0), "a group member who did not accept it");
        CountsFor(world, [item], Creator, after).ShouldBe(new Counts(0, 0, 0), "the creator");
        CountsFor(world, [item], Manager, after, isManager: true).ShouldBe(new Counts(0, 0, 0), "the manager");
        CountsFor(world, [item], Aming, Due).ShouldBe(new Counts(0, 0, 0), "not yet overdue");
    }

    [Fact]
    public void A_pending_overdue_case_counts_for_every_member_of_its_current_group_and_moves_with_a_transfer()
    {
        var world = World();
        var item = world.NewCase();
        var after = Due.AddMinutes(1);

        CountsFor(world, [item], Aming, after).ShouldBe(new Counts(0, 1, 1));
        CountsFor(world, [item], Colleague, after).ShouldBe(new Counts(0, 1, 1));
        CountsFor(world, [item], Purchaser, after).ShouldBe(new Counts(0, 0, 0), "another group's member");
        CountsFor(world, [item], Creator, after).ShouldBe(new Counts(0, 0, 0), "the creator is in no group");
        CountsFor(world, [item], Manager, after, isManager: true).ShouldBe(new Counts(0, 0, 0), "the manager gets nothing extra");
        CountsFor(world, [item], Aming, Due).ShouldBe(new Counts(0, 0, 1), "待我受理 counts before it is overdue");

        // 阿明 accepts and transfers it to 採購組: the owner is cleared, it is 待受理 again in the new group.
        world.Record(item.Accept(Aming, Created.AddHours(1)));
        world.Record(item.Transfer(Aming, world.Purchasing, "請採購組詢價", Created.AddHours(2)));

        CountsFor(world, [item], Purchaser, after).ShouldBe(new Counts(0, 1, 1), "the new group's member");
        CountsFor(world, [item], Aming, after).ShouldBe(new Counts(0, 0, 0), "the former owner, who still sees it");
        CountsFor(world, [item], Colleague, after).ShouldBe(new Counts(0, 0, 0), "the old group");
        CountsFor(world, [item], Manager, after, isManager: true).ShouldBe(new Counts(0, 0, 0));
    }

    [Fact]
    public void A_member_of_the_group_who_also_owns_another_case_gets_both_and_they_never_overlap()
    {
        var world = World();
        var mine = world.NewCase();
        world.Record(mine.Accept(Aming, Created.AddHours(1)));
        var waiting = world.NewCase();
        var notYet = world.NewCase(Due.AddDays(3));
        var after = Due.AddMinutes(1);

        var counts = CountsFor(world, [mine, waiting, notYet], Aming, after);
        counts.ShouldBe(new Counts(1, 1, 2));

        // The owned set holds no pending case and the group set no owned case: the side navigation's number is the sum.
        var sets = CaseAttention.For(new[] { mine, waiting, notYet }.AsQueryable(), world.Members, Aming, after);
        sets.OwnedOverdue.Concat(sets.GroupPendingOverdue).Distinct().Count().ShouldBe(counts.OwnedOverdue + counts.GroupPendingOverdue);
    }

    [Fact]
    public void The_list_scopes_are_the_same_rules_the_counts_use()
    {
        var world = World();
        var owned = world.NewCase();
        world.Record(owned.Accept(Aming, Created.AddHours(1)));
        var waiting = world.NewCase();
        var cases = new[] { owned, waiting }.AsQueryable();

        cases.Where(CaseAttention.OwnedBy(Aming)).ToList().ShouldBe([owned]);
        cases.Where(CaseAttention.InGroupsOf(Aming, world.Members)).ToList().ShouldBe([owned, waiting], ignoreOrder: true);
        cases.Where(CaseAttention.PendingInGroupsOf(Aming, world.Members)).ToList().ShouldBe([waiting]);
        cases.Where(CaseAttention.InGroupsOf(Purchaser, world.Members)).ShouldBeEmpty();
    }

    private static bool Overdue(Case item, DateTimeOffset now) =>
        new[] { item }.AsQueryable().Where(CaseAttention.Overdue(now)).Any();

    private static Counts CountsFor(TestWorld world, Case[] cases, Guid callerId, DateTimeOffset now, bool isManager = false)
    {
        var visible = cases.AsQueryable().Where(CaseVisibility.VisibleTo(callerId, isManager, world.Members, world.Events));
        var sets = CaseAttention.For(visible, world.Members, callerId, now);
        return new Counts(sets.OwnedOverdue.Count(), sets.GroupPendingOverdue.Count(), sets.PendingForMe.Count());
    }

    private sealed class TestWorld(CaseType type, CaseGroup equipment, CaseGroup purchasing, IQueryable<CaseGroupMember> members)
    {
        private readonly List<CaseEvent> _events = [];

        public CaseGroup Purchasing => purchasing;

        public IQueryable<CaseGroupMember> Members => members;

        /// <summary>A case created by <see cref="Creator"/> in 設備組, due at <see cref="Due"/> unless given.</summary>
        public Case NewCase(DateTimeOffset? dueAt = null)
        {
            var (item, created) = Case.Create(CaseOrigin.Manual, type, equipment, Creator, "冷藏庫溫度異常", "", dueAt ?? Due, CaseLinks.None, Created);
            _events.Add(created);
            return item;
        }

        /// <summary>Keeps an action's event: visibility reads the <c>accepted</c> ones (a former owner keeps seeing the case).</summary>
        public void Record(CaseEvent caseEvent) => _events.Add(caseEvent);

        public IQueryable<CaseEvent> Events => _events.AsQueryable();
    }

    private static TestWorld World()
    {
        var organizationId = Guid.CreateVersion7();
        var equipment = CaseGroup.Create(organizationId, "設備組", Created);
        var purchasing = CaseGroup.Create(organizationId, "採購組", Created);
        var type = CaseType.Create(organizationId, "設備故障報修", "", equipment, 72, true, Created);
        var members = new[]
        {
            CaseGroupMember.Create(equipment, Aming, Manager, Created),
            CaseGroupMember.Create(equipment, Colleague, Manager, Created),
            CaseGroupMember.Create(purchasing, Purchaser, Manager, Created),
        };
        return new TestWorld(type, equipment, purchasing, members.AsQueryable());
    }
}

/// <summary>One caller's attention: their own overdue cases, their groups' overdue 待受理, 待我受理.</summary>
internal sealed record Counts(int OwnedOverdue, int GroupPendingOverdue, int PendingForMe);
