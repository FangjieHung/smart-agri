using Shouldly;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge;

/// <summary>
/// The visibility and ownership rules ported from the mock: <c>listKnowledgeBaseSummaries</c>
/// lists only the viewer's own knowledge bases, and <c>ownedKnowledgeBase</c> lets only the
/// owner open or change one. Sharing grants neither.
/// </summary>
public class KnowledgeBaseAccessTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Colleague = Guid.CreateVersion7();

    [Fact]
    public void Only_the_owner_sees_a_knowledge_base_in_their_list_whatever_its_sharing()
    {
        var listedForOwner = KnowledgeBaseAccess.ListedFor(Owner).Compile();
        var listedForColleague = KnowledgeBaseAccess.ListedFor(Colleague).Compile();

        foreach (var knowledgeBase in AllSharingVariants())
        {
            listedForOwner(knowledgeBase).ShouldBeTrue(knowledgeBase.SharingScope.ToString());
            listedForColleague(knowledgeBase).ShouldBeFalse(knowledgeBase.SharingScope.ToString());
        }
    }

    [Fact]
    public void Only_the_owner_can_manage_a_knowledge_base_whatever_its_sharing()
    {
        var manageableByOwner = KnowledgeBaseAccess.ManageableBy(Owner).Compile();
        var manageableByColleague = KnowledgeBaseAccess.ManageableBy(Colleague).Compile();

        foreach (var knowledgeBase in AllSharingVariants())
        {
            manageableByOwner(knowledgeBase).ShouldBeTrue();
            manageableByColleague(knowledgeBase).ShouldBeFalse();

            // viewerCanManage agrees with the query rule.
            KnowledgeBaseAccess.CanManage(knowledgeBase, Owner).ShouldBeTrue();
            KnowledgeBaseAccess.CanManage(knowledgeBase, Colleague).ShouldBeFalse();
        }
    }

    /// <summary>One knowledge base per scope; "specific accounts" is shared with the colleague.</summary>
    private static IEnumerable<KnowledgeBase> AllSharingVariants()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var scope in Enum.GetValues<KnowledgeSharingScope>())
        {
            var knowledgeBase = KnowledgeBase.Create(Organization, Owner, "知識庫", string.Empty, now);
            knowledgeBase.ChangeSharing(scope, allowOriginalDownload: scope == KnowledgeSharingScope.Public, now);
            yield return knowledgeBase;
        }
    }
}
