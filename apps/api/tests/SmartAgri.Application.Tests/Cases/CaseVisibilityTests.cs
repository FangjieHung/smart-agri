using System.Reflection;
using Shouldly;
using SmartAgri.Application.Cases;
using SmartAgri.Domain.Cases;
using Case = SmartAgri.Domain.Cases.Case;

namespace SmartAgri.Application.Tests.Cases;

/// <summary>
/// <see cref="CaseVisibility"/> in memory (M7 plan §3 C, risk 1; issue #248): one row per kind of caller.
/// The API tests (<c>CaseEndpointsTests</c>) run the same rule through EF Core against PostgreSQL.
/// </summary>
public class CaseVisibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);

    private static readonly Guid Creator = Guid.CreateVersion7();

    private static readonly Guid GroupMember = Guid.CreateVersion7();

    private static readonly Guid OtherGroupMember = Guid.CreateVersion7();

    private static readonly Guid FormerOwner = Guid.CreateVersion7();

    private static readonly Guid Bystander = Guid.CreateVersion7();

    public static TheoryData<string, bool, bool> Callers => new()
    {
        { "creator", false, true },
        { "member of the current group", false, true },
        { "manager", true, true },
        { "member of another group", false, false },
        { "internal account with no tie to the case", false, false },
    };

    [Theory]
    [MemberData(nameof(Callers))]
    public void Creator_current_group_members_and_the_manager_see_a_case_and_no_one_else(string who, bool isManager, bool visible)
    {
        var world = World();
        var callerId = who switch
        {
            "creator" => Creator,
            "member of the current group" => GroupMember,
            "member of another group" => OtherGroupMember,
            _ => Bystander,
        };

        world.Cases.AsQueryable().Where(CaseVisibility.VisibleTo(callerId, isManager, world.Members, world.Events))
            .Select(item => item.Id).ToList()
            .ShouldBe(visible ? [world.Case.Id] : [], who);
    }

    [Fact]
    public void Someone_who_once_accepted_the_case_keeps_seeing_it_but_another_cases_owner_does_not()
    {
        var world = World();
        var (other, _) = Case.Create(
            CaseOrigin.Manual, world.Type, world.OtherGroup, Creator, "別的案件", "", Now.AddHours(1), CaseLinks.None, Now);

        // M7-4's accept writes this event; until then the test fills one in by hand.
        var accepted = AcceptedEvent(world.Case, FormerOwner);
        var events = world.Events.Append(accepted).AsQueryable();

        new[] { world.Case, other }.AsQueryable()
            .Where(CaseVisibility.VisibleTo(FormerOwner, false, world.Members, events))
            .Select(item => item.Id).ToList()
            .ShouldBe([world.Case.Id]);

        // Any other action naming them (or an accept by someone else) does not count.
        var commented = AcceptedEvent(world.Case, FormerOwner);
        Set(commented, nameof(CaseEvent.Action), CaseEventAction.Commented);
        new[] { world.Case }.AsQueryable()
            .Where(CaseVisibility.VisibleTo(FormerOwner, false, world.Members, world.Events.Append(commented).AsQueryable()))
            .ShouldBeEmpty();
        new[] { world.Case }.AsQueryable()
            .Where(CaseVisibility.VisibleTo(Bystander, false, world.Members, events))
            .ShouldBeEmpty();
    }

    [Fact]
    public void A_case_opened_without_a_creator_is_seen_only_through_the_group_or_as_manager()
    {
        var world = World();
        var (automatic, created) = Case.Create(
            CaseOrigin.DatabaseSubmission, world.Type, world.Group, null, "客戶資料庫：新紀錄", "", Now.AddHours(1),
            new CaseLinks(DatabaseId: Guid.CreateVersion7(), SubmissionId: Guid.CreateVersion7()), Now);
        var cases = new[] { automatic }.AsQueryable();
        var events = new[] { created }.AsQueryable();

        cases.Where(CaseVisibility.VisibleTo(GroupMember, false, world.Members, events)).ShouldHaveSingleItem();
        cases.Where(CaseVisibility.VisibleTo(Bystander, true, world.Members, events)).ShouldHaveSingleItem();
        cases.Where(CaseVisibility.VisibleTo(Creator, false, world.Members, events)).ShouldBeEmpty();
    }

    private sealed record TestWorld(
        CaseType Type,
        CaseGroup Group,
        CaseGroup OtherGroup,
        Case Case,
        IQueryable<Case> Cases,
        IQueryable<CaseGroupMember> Members,
        IQueryable<CaseEvent> Events);

    private static TestWorld World()
    {
        var organizationId = Guid.CreateVersion7();
        var group = CaseGroup.Create(organizationId, "設備組", Now);
        var otherGroup = CaseGroup.Create(organizationId, "採購組", Now);
        var type = CaseType.Create(organizationId, "設備故障報修", "", group, 72, true, Now);
        var (item, created) = Case.Create(CaseOrigin.Manual, type, group, Creator, "冷藏庫溫度異常", "", Now.AddHours(72), CaseLinks.None, Now);
        var members = new[]
        {
            CaseGroupMember.Create(group, GroupMember, Creator, Now),
            CaseGroupMember.Create(otherGroup, OtherGroupMember, Creator, Now),
        };
        return new TestWorld(type, group, otherGroup, item, new[] { item }.AsQueryable(), members.AsQueryable(), new[] { created }.AsQueryable());
    }

    /// <summary>An <c>accepted</c> event by <paramref name="ownerId"/>, made from a <c>created</c> event
    /// (its constructor is the domain's own).</summary>
    private static CaseEvent AcceptedEvent(Case item, Guid ownerId)
    {
        var organizationId = item.OrganizationId;
        var group = CaseGroup.Create(organizationId, "暫時", Now);
        var type = CaseType.Create(organizationId, "暫時", "", group, 1, true, Now);
        var (_, template) = Case.Create(CaseOrigin.Manual, type, group, ownerId, "暫時", "", Now, CaseLinks.None, Now);
        Set(template, nameof(CaseEvent.CaseId), item.Id);
        Set(template, nameof(CaseEvent.Action), CaseEventAction.Accepted);
        Set(template, nameof(CaseEvent.OwnerAccountId), (Guid?)ownerId);
        return template;
    }

    private static void Set(CaseEvent caseEvent, string property, object? value) =>
        typeof(CaseEvent).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(caseEvent, [value]);
}
