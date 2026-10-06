using Shouldly;
using SmartAgri.Application.Cases;
using SmartAgri.Domain.Cases;
using Case = SmartAgri.Domain.Cases.Case;

namespace SmartAgri.Application.Tests.Cases;

/// <summary>
/// <see cref="CaseStatistics"/> in memory (M7 plan §3 F, decision K; issue #251): the range expressions'
/// half-open boundaries, a cancelled case never being a completion, the average being creation →
/// completion over the completions only (<c>null</c> without any), a transferred case counting under the
/// group that completed it, and the rows. <c>CaseStatisticsEndpointsTests</c> runs the same expressions
/// through EF Core against PostgreSQL.
/// </summary>
public class CaseStatisticsTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset From = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ToExclusive = From.AddDays(2);

    private static readonly Guid Aming = Guid.CreateVersion7();

    private static readonly Guid Purchaser = Guid.CreateVersion7();

    private static readonly Guid Creator = Guid.CreateVersion7();

    [Fact]
    public void Completed_within_is_half_open_and_never_holds_a_cancelled_or_open_case()
    {
        var world = World();
        var atStart = world.Completed(From.AddHours(-5), From.AddHours(-4), at: From);
        var justBeforeEnd = world.Completed(Created, Created.AddHours(1), at: ToExclusive.AddTicks(-1));
        var atEnd = world.Completed(Created, Created.AddHours(1), at: ToExclusive);
        var beforeStart = world.Completed(Created.AddDays(-3), From.AddHours(-30), at: From.AddTicks(-1));
        var cancelled = world.NewCase(Created);
        cancelled.Cancel(Creator, null, Created.AddHours(5));
        var open = world.NewCase(Created);
        open.Accept(Aming, Created.AddHours(4));

        var all = new[] { atStart, justBeforeEnd, atEnd, beforeStart, cancelled, open }.AsQueryable();
        all.Where(CaseStatistics.CompletedWithin(From, ToExclusive)).ToList().ShouldBe([atStart, justBeforeEnd], ignoreOrder: true);
        all.Where(CaseStatistics.CancelledWithin(From, ToExclusive)).ToList().ShouldBe([cancelled]);
        all.Where(CaseStatistics.ClosedWithin(From, ToExclusive)).ToList().ShouldBe([atStart, justBeforeEnd, cancelled], ignoreOrder: true);
        cancelled.CompletedAt.ShouldBeNull("only Complete writes CompletedAt");
    }

    [Fact]
    public void The_average_is_creation_to_completion_over_the_completions_only_and_null_without_any()
    {
        var typeId = Guid.CreateVersion7();
        var groupId = Guid.CreateVersion7();
        var quietGroup = Guid.CreateVersion7();

        var rows = CaseStatistics.Rows(
            open: [new(typeId, groupId, 2), new(typeId, quietGroup, 1)],
            overdue: [new(typeId, groupId, 1)],
            completed:
            [
                new(typeId, groupId, Created, Created.AddHours(10)),
                new(typeId, groupId, Created, Created.AddHours(20)),
                new(typeId, groupId, Created, Created.AddMinutes(90)),
            ],
            cancelled: [new(typeId, groupId, 4), new(typeId, quietGroup, 1)]);

        var busy = rows.Single(row => row.GroupId == groupId);
        busy.ShouldBe(busy with { OpenCount = 2, OverdueCount = 1, CompletedCount = 3, CancelledCount = 4 });
        busy.AverageHandlingHours.ShouldNotBeNull().ShouldBe((10 + 20 + 1.5) / 3, tolerance: 1e-9);

        var quiet = rows.Single(row => row.GroupId == quietGroup);
        quiet.ShouldBe(new CaseStatisticsRow(typeId, quietGroup, 1, 0, 0, 1, null), "cancelled cases never make an average");
        rows.Count.ShouldBe(2);

        CaseStatistics.Rows([], [], [], []).ShouldBeEmpty("a pair with nothing to count has no row");
        CaseStatistics.HandlingHours(Created, Created.AddDays(2)).ShouldBe(48);
    }

    [Fact]
    public void A_transferred_case_counts_under_the_group_that_completed_it()
    {
        var world = World();
        var item = world.NewCase(Created);
        item.Accept(Aming, Created.AddHours(1));
        item.Transfer(Aming, world.Purchasing, "請採購組詢價", Created.AddHours(2));
        item.Accept(Purchaser, Created.AddHours(3));
        item.Complete(Purchaser, "已下單", Created.AddHours(6));

        var completions = new[] { item }.AsQueryable()
            .Where(CaseStatistics.CompletedWithin(From, ToExclusive))
            .Select(candidate => new CaseStatisticsCompletion(candidate.TypeId, candidate.GroupId, candidate.CreatedAt, candidate.CompletedAt!.Value))
            .ToList();
        var row = CaseStatistics.Rows([], [], completions, []).ShouldHaveSingleItem();
        row.GroupId.ShouldBe(world.Purchasing.Id);
        row.AverageHandlingHours.ShouldBe(6, "from creation in 設備組, not from the transfer");
    }

    private sealed class TestWorld(CaseType type, CaseGroup equipment, CaseGroup purchasing)
    {
        public CaseGroup Purchasing => purchasing;

        public Case NewCase(DateTimeOffset createdAt) =>
            Case.Create(CaseOrigin.Manual, type, equipment, Creator, "冷藏庫溫度異常", "", createdAt.AddHours(72), CaseLinks.None, createdAt).Case;

        public Case Completed(DateTimeOffset createdAt, DateTimeOffset acceptedAt, DateTimeOffset at)
        {
            var item = NewCase(createdAt);
            item.Accept(Aming, acceptedAt);
            item.Complete(Aming, "已更換壓縮機", at);
            return item;
        }
    }

    private static TestWorld World()
    {
        var organizationId = Guid.CreateVersion7();
        var equipment = CaseGroup.Create(organizationId, "設備組", Created.AddDays(-10));
        var purchasing = CaseGroup.Create(organizationId, "採購組", Created.AddDays(-10));
        var type = CaseType.Create(organizationId, "設備故障報修", "", equipment, 72, true, Created.AddDays(-10));
        return new TestWorld(type, equipment, purchasing);
    }
}
