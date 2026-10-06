using Shouldly;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;

namespace SmartAgri.Domain.Tests;

public sealed class ChatMessageTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Turns_created_in_the_same_instant_still_get_increasing_sequences_and_are_counted_once()
    {
        var assistant = Assistant.Create(
            Organization, Owner, "客服助理", "回答退換貨問題", null, AssistantTone.Friendly, string.Empty,
            AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
            showCitations: true, keepConversations: true, Now);
        var thread = new ChatThread(assistant, Owner, "新對話", Now);

        var question = ChatMessage.Account(thread, "退貨期限是幾天？", Now);
        var answer = ChatMessage.Assistant(thread, "七天內可以退貨 [1]。", ChatReplyKind.CompanyData, null, [], Now);

        question.Sequence.ShouldBe(1);
        answer.Sequence.ShouldBe(2);
        thread.MessageCount.ShouldBe(2);
        thread.LastActivityAt.ShouldBe(Now);
    }

    [Fact]
    public void An_account_turn_keeps_the_client_message_id_it_was_given_or_none_at_all()
    {
        var assistant = Assistant.Create(
            Organization, Owner, "客服助理", "回答退換貨問題", null, AssistantTone.Friendly, string.Empty,
            AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
            showCitations: true, keepConversations: true, Now);
        var thread = new ChatThread(assistant, Owner, "新對話", Now);

        var withId = ChatMessage.Account(thread, "退貨期限是幾天？", Now, "client-1");
        var withoutId = ChatMessage.Account(thread, "還有其他問題", Now);

        withId.ClientMessageId.ShouldBe("client-1");
        withoutId.ClientMessageId.ShouldBeNull();
    }

    [Fact]
    public void Reply_kinds_have_their_wire_names_and_the_case_proposal_goes_last()
    {
        WireNames<ChatReplyKind>.All.ShouldBe(
            ["company-data", "general-knowledge", "no-result", "form-request", "submission-receipt", "database-query", "case-proposal"]);
        ((int)ChatReplyKind.CaseProposal).ShouldBe(6, "stored as integers: new kinds only ever go at the end");
    }

    [Fact]
    public void A_case_proposal_keeps_its_snapshot_and_is_confirmed_or_dismissed_only_once()
    {
        var thread = NewThread();
        var typeId = Guid.CreateVersion7();
        Should.Throw<ArgumentException>(() => ChatMessage.Assistant(thread, "提議", ChatReplyKind.CaseProposal, null, [], Now));
        Should.Throw<ArgumentException>(() => ChatCaseProposalSnapshot.Propose(typeId, " 有空白 ", string.Empty));
        Should.Throw<ArgumentException>(() => ChatCaseProposalSnapshot.Propose(typeId, new string('字', 121), string.Empty));

        var message = ChatMessage.CaseProposal(thread, "這件事可以開一件「設備報修」案件，請確認內容。", ChatCaseProposalSnapshot.Propose(typeId, "冷藏庫報修", string.Empty), Now);
        message.ReplyKind.ShouldBe(ChatReplyKind.CaseProposal);
        message.ReadCaseProposal().ShouldBe(new ChatCaseProposalSnapshot(typeId, "冷藏庫報修", string.Empty, ChatCaseProposalStatus.Proposed));
        message.CaseProposalJson!.ShouldContain("\"status\":\"proposed\"");
        message.ProposedCaseId.ShouldBeNull();

        var caseId = Guid.CreateVersion7();
        message.ConfirmCaseProposal(caseId, "二號冷藏庫溫度異常", "請派人檢查。");
        message.ReadCaseProposal().ShouldBe(new ChatCaseProposalSnapshot(typeId, "二號冷藏庫溫度異常", "請派人檢查。", ChatCaseProposalStatus.Confirmed));
        message.ProposedCaseId.ShouldBe(caseId);
        Should.Throw<InvalidOperationException>(() => message.ConfirmCaseProposal(Guid.CreateVersion7(), "再一次", string.Empty));
        Should.Throw<InvalidOperationException>(() => message.DismissCaseProposal());

        var dismissed = ChatMessage.CaseProposal(thread, "提議", ChatCaseProposalSnapshot.Propose(typeId, "冷藏庫報修", string.Empty), Now);
        dismissed.DismissCaseProposal();
        dismissed.ReadCaseProposal()!.Status.ShouldBe(ChatCaseProposalStatus.Dismissed);
        dismissed.ProposedCaseId.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => dismissed.ConfirmCaseProposal(caseId, "冷藏庫報修", string.Empty));

        ChatMessage.Account(thread, "問題", Now).ReadCaseProposal().ShouldBeNull();
    }

    private static ChatThread NewThread()
    {
        var assistant = Assistant.Create(
            Organization, Owner, "設備助理", "處理設備問題", null, AssistantTone.Friendly, string.Empty,
            AssistantKnowledgeScope.CompanyDataOnly, "目前的資料中找不到這個問題的答案。",
            showCitations: true, keepConversations: true, Now);
        return new ChatThread(assistant, Owner, "新對話", Now);
    }
}
