using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Domain.Tests;

public class KnowledgeBaseTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly DateTimeOffset Created = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_knowledge_base_is_private_trimmed_and_owned_by_its_creator()
    {
        var knowledgeBase = KnowledgeBase.Create(Organization, Owner, " 退換貨政策 ", " 退貨期限 ", Created);

        knowledgeBase.Id.ShouldNotBe(Guid.Empty);
        knowledgeBase.OrganizationId.ShouldBe(Organization);
        knowledgeBase.OwnerAccountId.ShouldBe(Owner);
        knowledgeBase.Name.ShouldBe("退換貨政策");
        knowledgeBase.Purpose.ShouldBe("退貨期限");
        knowledgeBase.SharingScope.ShouldBe(KnowledgeSharingScope.Private);
        knowledgeBase.AllowOriginalDownload.ShouldBeFalse();
        knowledgeBase.CreatedAt.ShouldBe(Created);
        knowledgeBase.UpdatedAt.ShouldBe(Created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_rejected(string name)
    {
        Should.Throw<ArgumentException>(() => KnowledgeBase.Create(Organization, Owner, name, string.Empty, Created));
    }

    [Fact]
    public void Over_long_fields_and_empty_ids_are_rejected()
    {
        Should.Throw<ArgumentException>(() => KnowledgeBase.Create(
            Organization, Owner, new string('名', KnowledgeBase.NameMaxLength + 1), string.Empty, Created));
        Should.Throw<ArgumentException>(() => KnowledgeBase.Create(
            Organization, Owner, "名稱", new string('用', KnowledgeBase.PurposeMaxLength + 1), Created));
        Should.Throw<ArgumentException>(() => KnowledgeBase.Create(Guid.Empty, Owner, "名稱", string.Empty, Created));
        Should.Throw<ArgumentException>(() => KnowledgeBase.Create(Organization, Guid.Empty, "名稱", string.Empty, Created));
    }

    [Fact]
    public void Changing_details_reports_the_changed_fields_and_bumps_updated_at_only_when_something_changed()
    {
        var knowledgeBase = KnowledgeBase.Create(Organization, Owner, "名稱", "用途", Created);
        var later = Created.AddHours(1);

        knowledgeBase.ChangeDetails(" 名稱 ", "用途", later).ShouldBeEmpty();
        knowledgeBase.UpdatedAt.ShouldBe(Created);

        knowledgeBase.ChangeDetails("新名稱", "用途", later).ShouldBe(["name"]);
        knowledgeBase.ChangeDetails("新名稱2", "新用途", later.AddHours(1)).ShouldBe(["name", "purpose"]);
        knowledgeBase.UpdatedAt.ShouldBe(later.AddHours(1));
    }

    [Theory]
    [InlineData(KnowledgeSharingScope.Private)]
    [InlineData(KnowledgeSharingScope.SpecificAccounts)]
    public void Original_downloads_require_public_sharing(KnowledgeSharingScope scope)
    {
        var knowledgeBase = KnowledgeBase.Create(Organization, Owner, "名稱", string.Empty, Created);

        Should.Throw<ArgumentException>(() => knowledgeBase.ChangeSharing(scope, allowOriginalDownload: true, Created));

        knowledgeBase.ChangeSharing(KnowledgeSharingScope.Public, allowOriginalDownload: true, Created.AddMinutes(1));
        knowledgeBase.AllowOriginalDownload.ShouldBeTrue();
        knowledgeBase.UpdatedAt.ShouldBe(Created.AddMinutes(1));
    }

    [Fact]
    public void Activity_details_describe_changes_without_names_or_content()
    {
        var actor = Guid.CreateVersion7();
        var shared = Guid.CreateVersion7();
        var knowledgeBase = KnowledgeBase.Create(Organization, Owner, "機密專案名稱", "機密用途", Created);

        KnowledgeActivity.KnowledgeBaseCreated(knowledgeBase, actor, Created).Detail.ShouldBeNull();
        KnowledgeActivity.KnowledgeBaseDeleted(knowledgeBase, actor, Created).Detail.ShouldBeNull();

        var updated = KnowledgeActivity.KnowledgeBaseUpdated(knowledgeBase, actor, Created, ["name"]);
        updated.Detail.ShouldBe("""{"changed":["name"]}""");

        knowledgeBase.ChangeSharing(KnowledgeSharingScope.SpecificAccounts, allowOriginalDownload: false, Created);
        var sharing = KnowledgeActivity.SharingChanged(knowledgeBase, actor, Created, [shared]);
        using var json = JsonDocument.Parse(sharing.Detail!);
        json.RootElement.GetProperty("scope").GetString().ShouldBe("specific-accounts");
        json.RootElement.GetProperty("sharedWithAccountIds").EnumerateArray().Select(id => id.GetGuid()).ShouldBe([shared]);
        json.RootElement.GetProperty("allowOriginalDownload").GetBoolean().ShouldBeFalse();

        foreach (var activity in new[] { updated, sharing })
        {
            activity.OrganizationId.ShouldBe(Organization);
            activity.KnowledgeBaseId.ShouldBe(knowledgeBase.Id);
            activity.ActorAccountId.ShouldBe(actor);
            activity.Detail!.ShouldNotContain("機密");
        }
    }
}
