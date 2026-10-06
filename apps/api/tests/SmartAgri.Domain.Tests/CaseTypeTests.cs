using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Tests;

/// <summary><see cref="CaseType"/> and the organization activity it writes (M7-2, #247). The request rules are in
/// <c>SmartAgri.Application.Tests/Cases/CaseTypeRulesTests.cs</c>.</summary>
public class CaseTypeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2160, true)]
    [InlineData(2161, false)]
    public void The_entity_keeps_one_to_2160_hours(int hours, bool valid)
    {
        var organizationId = Guid.CreateVersion7();
        var group = CaseGroup.Create(organizationId, "設備組", Now);
        if (valid)
        {
            CaseType.Create(organizationId, "設備故障報修", "", group, hours, true, Now).DefaultDueHours.ShouldBe(hours);
        }
        else
        {
            Should.Throw<ArgumentOutOfRangeException>(() => CaseType.Create(organizationId, "設備故障報修", "", group, hours, true, Now));
        }
    }

    [Fact]
    public void An_update_lists_what_changed_and_nothing_when_nothing_did()
    {
        var organizationId = Guid.CreateVersion7();
        var equipment = CaseGroup.Create(organizationId, "設備組", Now);
        var purchasing = CaseGroup.Create(organizationId, "採購組", Now);
        var type = CaseType.Create(organizationId, "設備故障報修", "冷藏庫、溫控設備故障", equipment, 72, true, Now);

        (type.OrganizationId, type.DefaultGroupId, type.IsActive, type.CreatedAt, type.UpdatedAt)
            .ShouldBe((organizationId, equipment.Id, true, Now, Now));

        type.Update("設備故障報修", "冷藏庫、溫控設備故障", equipment, 72, true, Now.AddMinutes(1)).ShouldBeEmpty();
        type.UpdatedAt.ShouldBe(Now);

        type.Update("設備報修", "冷藏庫、溫控設備故障", purchasing, 48, false, Now.AddMinutes(2))
            .ShouldBe(["name", "defaultGroupId", "defaultDueHours", "isActive"]);
        (type.Name, type.DefaultGroupId, type.DefaultDueHours, type.IsActive, type.UpdatedAt)
            .ShouldBe(("設備報修", purchasing.Id, 48, false, Now.AddMinutes(2)));
        type.Update("設備報修", "", purchasing, 48, false, Now.AddMinutes(3)).ShouldBe(["description"]);
    }

    [Fact]
    public void An_archived_group_is_never_chosen_and_stays_only_on_an_inactive_type()
    {
        var organizationId = Guid.CreateVersion7();
        var equipment = CaseGroup.Create(organizationId, "設備組", Now);
        var archived = CaseGroup.Create(organizationId, "舊倉儲組", Now);
        archived.Archive(Now);

        Should.Throw<InvalidOperationException>(() => CaseType.Create(organizationId, "倉儲盤點", "", archived, 24, false, Now));

        var type = CaseType.Create(organizationId, "設備故障報修", "", equipment, 24, true, Now);
        Should.Throw<InvalidOperationException>(() => type.Update("設備故障報修", "", archived, 24, false, Now));

        // A type whose group was archived after it was deactivated may keep it while inactive.
        var stocktake = CaseType.Create(organizationId, "倉儲盤點", "", equipment, 24, false, Now);
        stocktake.Update("倉儲盤點", "", equipment, 24, false, Now).ShouldBeEmpty();
        equipment.Archive(Now);
        stocktake.Update("倉儲盤點", "每月盤點", equipment, 24, false, Now).ShouldBe(["description"]);
        Should.Throw<InvalidOperationException>(() => stocktake.Update("倉儲盤點", "每月盤點", equipment, 24, true, Now));
    }

    [Fact]
    public void The_entity_refuses_unchecked_values_and_a_group_of_another_organization()
    {
        var organizationId = Guid.CreateVersion7();
        var group = CaseGroup.Create(organizationId, "設備組", Now);

        Should.Throw<ArgumentException>(() => CaseType.Create(organizationId, " 設備 ", "", group, 24, true, Now));
        Should.Throw<ArgumentException>(() => CaseType.Create(organizationId, new string('類', 41), "", group, 24, true, Now));
        Should.Throw<ArgumentException>(() => CaseType.Create(organizationId, "設備", new string('說', 501), group, 24, true, Now));
        Should.Throw<ArgumentException>(() => CaseType.Create(Guid.Empty, "設備", "", group, 24, true, Now));
        Should.Throw<ArgumentException>(() =>
            CaseType.Create(organizationId, "設備", "", CaseGroup.Create(Guid.CreateVersion7(), "外組", Now), 24, true, Now));
    }

    [Fact]
    public void Created_and_updated_activities_hold_ids_names_and_field_names_only()
    {
        var organizationId = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();
        var typeId = Guid.CreateVersion7();

        var created = OrganizationActivity.CaseTypeCreated(organizationId, actor, Now, typeId, "設備故障報修");
        created.Action.ShouldBe(OrganizationActivityAction.CaseTypeCreated);
        using (var detail = JsonDocument.Parse(created.Detail!))
        {
            detail.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["id", "name"]);
            detail.RootElement.GetProperty("id").GetGuid().ShouldBe(typeId);
        }

        var updated = OrganizationActivity.CaseTypeUpdated(organizationId, actor, Now, typeId, "設備報修", ["name", "description"], false);
        updated.Action.ShouldBe(OrganizationActivityAction.CaseTypeUpdated);
        using (var detail = JsonDocument.Parse(updated.Detail!))
        {
            detail.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["id", "name", "changed", "isActive"]);
            detail.RootElement.GetProperty("changed").EnumerateArray().Select(field => field.GetString()).ShouldBe(["name", "description"]);
            detail.RootElement.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        }

        Should.Throw<ArgumentException>(() => OrganizationActivity.CaseTypeUpdated(organizationId, actor, Now, typeId, "x", [], true));
    }
}
