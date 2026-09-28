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
}
