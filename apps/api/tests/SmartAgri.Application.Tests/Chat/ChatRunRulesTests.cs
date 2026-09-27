using Shouldly;
using SmartAgri.Application.Chat;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;

namespace SmartAgri.Application.Tests.Chat;

public class ChatRunRulesTests
{
    [Theory]
    [InlineData("退貨運費由誰負擔？", "退貨運費由誰負擔？")]
    [InlineData("  前後\n有  空白\t的問題  ", "前後 有 空白 的問題")]
    [InlineData("一二三四五六七八九十一二三四五六七八九十一二三四", "一二三四五六七八九十一二三四五六七八九十一二三四")]
    [InlineData("一二三四五六七八九十一二三四五六七八九十一二三四五", "一二三四五六七八九十一二三四五六七八九十一二三四…")]
    public void A_new_threads_title_is_the_question_collapsed_and_cut_to_24_characters(string question, string expected)
    {
        ChatRunRules.TitleFromQuestion(question).ShouldBe(expected);
    }

    [Fact]
    public void Only_a_blank_default_titled_thread_is_retitled_by_its_first_question()
    {
        var assistant = Assistant.Create(
            Guid.NewGuid(), Guid.NewGuid(), "助理", "用途", null, AssistantTone.Friendly, string.Empty,
            AssistantKnowledgeScope.CompanyDataOnly, "找不到。", showCitations: true, keepConversations: true, DateTimeOffset.UtcNow);
        var blank = new ChatThread(assistant, Guid.NewGuid(), ChatRunRules.DefaultThreadTitle, DateTimeOffset.UtcNow);
        var renamed = new ChatThread(assistant, Guid.NewGuid(), "我自己取的名字", DateTimeOffset.UtcNow);

        ChatRunRules.TitleForFirstQuestion(blank, "第一個問題").ShouldBe("第一個問題");
        ChatRunRules.TitleForFirstQuestion(renamed, "第一個問題").ShouldBeNull();

        ChatMessage.Account(blank, "第一個問題", DateTimeOffset.UtcNow);
        ChatRunRules.TitleForFirstQuestion(blank, "第二個問題").ShouldBeNull("it already has a message");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_question_is_refused(string? question)
    {
        var result = ChatRunRules.ValidateQuestion(question);
        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldHaveSingleItem().Message.ShouldBe(ChatRunRules.QuestionRequiredMessage);
    }

    [Fact]
    public void A_question_is_trimmed_and_capped_at_2000_characters()
    {
        ChatRunRules.ValidateQuestion($"  {new string('問', 2000)}  ").Value.ShouldBe(new string('問', 2000));
        ChatRunRules.ValidateQuestion(new string('問', 2001)).Failures.ShouldHaveSingleItem().Message
            .ShouldBe(ChatRunRules.QuestionTooLongMessage);
    }
}
