using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

/// <summary>
/// Who may use an assistant (M3 plan §5 Slice 3 acceptance): the owner always, whether paused
/// or not; anyone else needs all three of <c>use-shared-assistants</c>, an
/// <see cref="AssistantShare"/> row, and the assistant being <see cref="AssistantStatus.Ready"/>.
/// </summary>
public class AssistantUseAccessTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Stranger = Guid.CreateVersion7();

    [Fact]
    public void The_owner_may_always_use_their_own_assistant_paused_or_not()
    {
        var assistant = CreateAssistant();

        IsUsableBy(assistant, Owner, hasUseSharedAssistants: false, []).ShouldBeTrue();

        assistant.SetStatus(AssistantStatus.Paused, DateTimeOffset.UtcNow);
        IsUsableBy(assistant, Owner, hasUseSharedAssistants: false, []).ShouldBeTrue("the owner may use it even while paused");
    }

    [Fact]
    public void A_stranger_may_not_use_it_until_shared_with_them()
    {
        var assistant = CreateAssistant();

        IsUsableBy(assistant, Stranger, hasUseSharedAssistants: true, []).ShouldBeFalse("not shared yet");
        IsUsableBy(assistant, Stranger, hasUseSharedAssistants: true, [new AssistantShare(assistant, Stranger)])
            .ShouldBeTrue("now shared with them, and they hold use-shared-assistants");
    }

    [Fact]
    public void Sharing_it_is_not_enough_without_use_shared_assistants()
    {
        var assistant = CreateAssistant();
        var shares = new[] { new AssistantShare(assistant, Stranger) };

        IsUsableBy(assistant, Stranger, hasUseSharedAssistants: false, shares)
            .ShouldBeFalse("shared, but the account lacks use-shared-assistants");
    }

    [Fact]
    public void Being_shared_with_someone_else_does_not_grant_use()
    {
        var assistant = CreateAssistant();
        var someoneElse = Guid.CreateVersion7();
        var shares = new[] { new AssistantShare(assistant, someoneElse) };

        IsUsableBy(assistant, Stranger, hasUseSharedAssistants: true, shares).ShouldBeFalse();
    }

    [Fact]
    public void A_paused_assistant_is_unusable_by_a_non_owner_even_when_shared()
    {
        var assistant = CreateAssistant();
        var shares = new[] { new AssistantShare(assistant, Stranger) };
        assistant.SetStatus(AssistantStatus.Paused, DateTimeOffset.UtcNow);

        IsUsableBy(assistant, Stranger, hasUseSharedAssistants: true, shares)
            .ShouldBeFalse("acceptance: 助理暫停後，非擁有者無法使用");
    }

    [Fact]
    public void Unsharing_then_resharing_toggles_use_without_touching_the_assistant()
    {
        var assistant = CreateAssistant();
        var shares = new List<AssistantShare> { new(assistant, Stranger) };

        IsUsableBy(assistant, Stranger, hasUseSharedAssistants: true, shares).ShouldBeTrue();

        shares.RemoveAt(0);
        IsUsableBy(assistant, Stranger, hasUseSharedAssistants: true, shares)
            .ShouldBeFalse("acceptance: 取消分享後，被取消的帳號不可用");

        shares.Add(new AssistantShare(assistant, Stranger));
        IsUsableBy(assistant, Stranger, hasUseSharedAssistants: true, shares)
            .ShouldBeTrue("acceptance: 重新分享後，原本的存取再次可用");
    }

    private static Assistant CreateAssistant() =>
        Assistant.Create(
            Organization, Owner, "客服助理", "回答退換貨問題", null,
            AssistantTone.Friendly, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
            "目前的資料中找不到這個問題的答案。", showCitations: true, keepConversations: true, DateTimeOffset.UtcNow);

    private static bool IsUsableBy(
        Assistant assistant, Guid accountId, bool hasUseSharedAssistants, IEnumerable<AssistantShare> shares) =>
        AssistantUseAccess.UsableBy(accountId, hasUseSharedAssistants, shares.AsQueryable()).Compile()(assistant);
}
