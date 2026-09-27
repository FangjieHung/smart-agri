using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

/// <summary>
/// M3 Slice 1 only knows about ownership (sharing is #73): the owner may use their own
/// assistant, a non-owner may not.
/// </summary>
public class AssistantUseAccessTests
{
    [Fact]
    public void Only_the_owner_may_use_it_until_sharing_lands()
    {
        var organization = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();
        var stranger = Guid.CreateVersion7();
        var assistant = Assistant.Create(
            organization, owner, "客服助理", "回答退換貨問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: true, DateTimeOffset.UtcNow);

        AssistantUseAccess.UsableBy(owner).Compile()(assistant).ShouldBeTrue();
        AssistantUseAccess.UsableBy(stranger).Compile()(assistant).ShouldBeFalse();
    }
}
