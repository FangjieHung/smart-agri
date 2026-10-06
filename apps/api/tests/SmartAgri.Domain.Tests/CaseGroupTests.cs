using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Tests;

/// <summary><see cref="CaseGroup"/>, its member rows and the organization activity it writes (M7-1, #246).</summary>
public class CaseGroupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_group_is_renamed_archived_and_unarchived_and_says_when_nothing_changed()
    {
        var organizationId = Guid.CreateVersion7();
        var group = CaseGroup.Create(organizationId, "設備組", Now);

        (group.OrganizationId, group.Name, group.IsArchived, group.CreatedAt, group.UpdatedAt)
            .ShouldBe((organizationId, "設備組", false, Now, Now));

        group.Rename("設備組", Now.AddMinutes(1)).ShouldBeFalse();
        group.UpdatedAt.ShouldBe(Now);
        group.Rename("設備維修組", Now.AddMinutes(1)).ShouldBeTrue();
        (group.Name, group.UpdatedAt).ShouldBe(("設備維修組", Now.AddMinutes(1)));

        group.Unarchive(Now.AddMinutes(2)).ShouldBeFalse();
        group.Archive(Now.AddMinutes(2)).ShouldBeTrue();
        (group.IsArchived, group.ArchivedAt).ShouldBe((true, (DateTimeOffset?)Now.AddMinutes(2)));
        group.Archive(Now.AddMinutes(3)).ShouldBeFalse();
        group.ArchivedAt.ShouldBe(Now.AddMinutes(2));
        group.Unarchive(Now.AddMinutes(4)).ShouldBeTrue();
        (group.IsArchived, group.UpdatedAt).ShouldBe((false, Now.AddMinutes(4)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 設備組")]
    [InlineData("設備組 ")]
    public void A_name_must_already_be_trimmed_and_not_empty(string name)
    {
        Should.Throw<ArgumentException>(() => CaseGroup.Create(Guid.CreateVersion7(), name, Now));
    }

    [Fact]
    public void A_name_is_at_most_forty_characters()
    {
        CaseGroup.Create(Guid.CreateVersion7(), new string('組', CaseGroup.NameMaxLength), Now).Name.Length.ShouldBe(40);
        Should.Throw<ArgumentException>(() => CaseGroup.Create(Guid.CreateVersion7(), new string('組', 41), Now));
        Should.Throw<ArgumentException>(() => CaseGroup.Create(Guid.Empty, "設備組", Now));
    }

    [Fact]
    public void Member_rows_and_changes_carry_the_groups_organization()
    {
        var group = CaseGroup.Create(Guid.CreateVersion7(), "採購組", Now);
        var account = Guid.CreateVersion7();
        var manager = Guid.CreateVersion7();

        var member = CaseGroupMember.Create(group, account, manager, Now);
        (member.GroupId, member.AccountId, member.OrganizationId, member.AddedByAccountId, member.AddedAt)
            .ShouldBe((group.Id, account, group.OrganizationId, manager, Now));

        var change = CaseGroupMemberChange.Create(group, account, added: false, manager, Now);
        (change.GroupId, change.AccountId, change.OrganizationId, change.Added, change.ChangedByAccountId, change.ChangedAt)
            .ShouldBe((group.Id, account, group.OrganizationId, false, manager, Now));

        Should.Throw<ArgumentException>(() => CaseGroupMember.Create(group, Guid.Empty, manager, Now));
        Should.Throw<ArgumentException>(() => CaseGroupMemberChange.Create(group, account, true, Guid.Empty, Now));
    }

    [Fact]
    public void Group_activities_record_the_id_and_the_name()
    {
        var organizationId = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();
        var groupId = Guid.CreateVersion7();

        var created = OrganizationActivity.CaseGroupCreated(organizationId, actor, Now, groupId, "設備組");
        (created.Action, created.ActorAccountId).ShouldBe((OrganizationActivityAction.CaseGroupCreated, (Guid?)actor));
        Detail(created).ShouldBe(new Dictionary<string, string> { ["id"] = groupId.ToString(), ["name"] = "設備組" });

        var renamed = OrganizationActivity.CaseGroupRenamed(organizationId, actor, Now, groupId, "設備維修組", "設備組");
        renamed.Action.ShouldBe(OrganizationActivityAction.CaseGroupRenamed);
        Detail(renamed).ShouldBe(new Dictionary<string, string>
        {
            ["id"] = groupId.ToString(), ["name"] = "設備維修組", ["previousName"] = "設備組",
        });

        OrganizationActivity.CaseGroupArchiveChanged(organizationId, actor, Now, groupId, "設備組", archived: true).Action
            .ShouldBe(OrganizationActivityAction.CaseGroupArchived);
        var unarchived = OrganizationActivity.CaseGroupArchiveChanged(organizationId, actor, Now, groupId, "設備組", archived: false);
        unarchived.Action.ShouldBe(OrganizationActivityAction.CaseGroupUnarchived);
        Detail(unarchived).Keys.ShouldBe(["id", "name"], ignoreOrder: true);
    }

    private static Dictionary<string, string> Detail(OrganizationActivity activity) =>
        JsonDocument.Parse(activity.Detail!).RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);
}
