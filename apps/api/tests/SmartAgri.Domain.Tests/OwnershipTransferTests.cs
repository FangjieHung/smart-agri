using Shouldly;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// <c>TransferOwnership</c> on the four things a deactivated member hands to their successor (issue
/// #356): the owner changes and nothing else does — not the updated or saved time, not a draft's
/// revision — and the same owner or an empty id is refused.
/// </summary>
public class OwnershipTransferTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Successor = Guid.CreateVersion7();
    private static readonly DateTimeOffset Created = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_assistant_changes_owner_and_nothing_else()
    {
        var assistant = Assistant.Create(
            Organization, Member, "設備助理", "回答設備問題", null, AssistantTone.Friendly, string.Empty,
            AssistantKnowledgeScope.CompanyDataOnly, "找不到答案。", showCitations: true, keepConversations: true, Created);

        assistant.TransferOwnership(Successor);

        (assistant.OwnerAccountId, assistant.UpdatedAt, assistant.Status).ShouldBe((Successor, Created, AssistantStatus.Ready));
        Refuses(assistant.TransferOwnership);
    }

    [Fact]
    public void A_draft_changes_owner_but_keeps_its_revision_and_saved_time()
    {
        var draft = new AssistantDraft(Organization, Member, "{}", 1, Created);

        draft.TransferOwnership(Successor);

        (draft.OwnerAccountId, draft.Revision, draft.SavedAt).ShouldBe((Successor, 1, Created));
        Refuses(draft.TransferOwnership);
    }

    [Fact]
    public void A_knowledge_base_and_a_database_change_owner_and_nothing_else()
    {
        var knowledgeBase = KnowledgeBase.Create(Organization, Member, "設備手冊", "設備說明", Created);
        var database = Database.Create(Organization, Member, "巡檢紀錄", "巡檢", DatabaseTemplateId.CustomerProfile, Created);

        knowledgeBase.TransferOwnership(Successor);
        database.TransferOwnership(Successor);

        (knowledgeBase.OwnerAccountId, knowledgeBase.UpdatedAt).ShouldBe((Successor, Created));
        (database.OwnerAccountId, database.UpdatedAt).ShouldBe((Successor, Created));
        Refuses(knowledgeBase.TransferOwnership);
        Refuses(database.TransferOwnership);
    }

    private static void Refuses(Action<Guid> transfer)
    {
        Should.Throw<ArgumentException>(() => transfer(Successor), "already the owner");
        Should.Throw<ArgumentException>(() => transfer(Guid.Empty));
    }
}
