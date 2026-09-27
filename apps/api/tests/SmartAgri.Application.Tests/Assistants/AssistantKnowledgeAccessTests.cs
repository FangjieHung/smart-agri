using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Assistants;

/// <summary>
/// Which knowledge bases an assistant's owner may connect: their own, any <c>public</c> one,
/// or a <c>specific-accounts</c> one that shares with them — never someone else's
/// <c>private</c> one, and never a <c>specific-accounts</c> one that does not name them (M3
/// plan §3 step 1; acceptance: "連接「別人的 Private 知識庫」得到 422").
/// </summary>
public class AssistantKnowledgeAccessTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Stranger = Guid.CreateVersion7();

    [Fact]
    public void The_owners_own_knowledge_base_is_connectable_whatever_its_sharing()
    {
        foreach (var scope in Enum.GetValues<KnowledgeSharingScope>())
        {
            var knowledgeBase = KnowledgeBase.Create(Organization, Owner, "自己的知識庫", string.Empty, Now);
            knowledgeBase.ChangeSharing(scope, allowOriginalDownload: scope == KnowledgeSharingScope.Public, Now);

            IsConnectable(knowledgeBase, Owner, []).ShouldBeTrue(scope.ToString());
        }
    }

    [Fact]
    public void A_strangers_private_knowledge_base_is_not_connectable()
    {
        var knowledgeBase = KnowledgeBase.Create(Organization, Stranger, "別人的知識庫", string.Empty, Now);

        IsConnectable(knowledgeBase, Owner, []).ShouldBeFalse();
    }

    [Fact]
    public void A_strangers_public_knowledge_base_is_connectable()
    {
        var knowledgeBase = KnowledgeBase.Create(Organization, Stranger, "公開知識庫", string.Empty, Now);
        knowledgeBase.ChangeSharing(KnowledgeSharingScope.Public, allowOriginalDownload: true, Now);

        IsConnectable(knowledgeBase, Owner, []).ShouldBeTrue();
    }

    [Fact]
    public void A_specific_accounts_knowledge_base_is_connectable_only_when_shared_with_the_owner()
    {
        var knowledgeBase = KnowledgeBase.Create(Organization, Stranger, "指定分享的知識庫", string.Empty, Now);
        knowledgeBase.ChangeSharing(KnowledgeSharingScope.SpecificAccounts, allowOriginalDownload: false, Now);

        IsConnectable(knowledgeBase, Owner, []).ShouldBeFalse("not shared with the owner yet");
        IsConnectable(knowledgeBase, Owner, [new KnowledgeBaseShare(knowledgeBase, Owner)]).ShouldBeTrue("now shared with the owner");
        IsConnectable(knowledgeBase, Owner, [new KnowledgeBaseShare(knowledgeBase, Stranger)])
            .ShouldBeFalse("shared with someone else, not the owner");
    }

    private static bool IsConnectable(KnowledgeBase knowledgeBase, Guid ownerAccountId, KnowledgeBaseShare[] shares) =>
        AssistantKnowledgeAccess.ConnectableBy(ownerAccountId, shares.AsQueryable()).Compile()(knowledgeBase);
}
