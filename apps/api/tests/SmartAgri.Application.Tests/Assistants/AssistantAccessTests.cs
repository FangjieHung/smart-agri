using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

/// <summary>
/// The ownership rule ported from the mock (M3 plan §5 Slice 1): only the owner sees an
/// assistant in <c>listAssistantConfigurations</c>, and only the owner may open or change its
/// settings.
/// </summary>
public class AssistantAccessTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Colleague = Guid.CreateVersion7();

    private static readonly Assistant SomeAssistant = Assistant.Create(
        Organization, Owner, "客服助理", "回答退換貨問題", null,
        AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
        "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: true, DateTimeOffset.UtcNow);

    [Fact]
    public void Only_the_owner_sees_it_in_their_list()
    {
        AssertOwnerOnly(AssistantAccess.ListedFor(Owner).Compile(), AssistantAccess.ListedFor(Colleague).Compile());
    }

    [Fact]
    public void Only_the_owner_can_manage_it()
    {
        AssertOwnerOnly(AssistantAccess.ManageableBy(Owner).Compile(), AssistantAccess.ManageableBy(Colleague).Compile());

        AssistantAccess.CanManage(SomeAssistant, Owner).ShouldBeTrue();
        AssistantAccess.CanManage(SomeAssistant, Colleague).ShouldBeFalse();
    }

    private static void AssertOwnerOnly(Func<Assistant, bool> forOwner, Func<Assistant, bool> forColleague)
    {
        forOwner(SomeAssistant).ShouldBeTrue();
        forColleague(SomeAssistant).ShouldBeFalse();
    }
}
