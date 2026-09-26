using Shouldly;
using SmartAgri.Application.Knowledge.Retrieval;

namespace SmartAgri.Application.Tests.Knowledge.Retrieval;

public class KnowledgeRetrievalRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t　")]
    public void A_blank_question_is_refused(string? question)
    {
        var result = KnowledgeRetrievalRules.ValidatePreview(question, null);

        result.Failures.ShouldBe([new("question", KnowledgeRetrievalRules.QuestionRequiredMessage)]);
    }

    [Fact]
    public void A_question_is_trimmed_and_may_have_500_characters_but_not_501()
    {
        var longest = new string('問', KnowledgeRetrievalRules.QuestionMaxLength);

        KnowledgeRetrievalRules.ValidatePreview($"  {longest}\n", null).Value.ShouldBe(new KnowledgeRetrievalPreviewRequest(longest, null));
        KnowledgeRetrievalRules.ValidatePreview(longest + "問", null).Failures
            .ShouldBe([new("question", "問題最多 500 個字。")]);
    }

    [Fact]
    public void Top_is_optional_and_otherwise_1_to_20_and_every_broken_rule_is_reported_at_once()
    {
        KnowledgeRetrievalRules.ValidatePreview("幾天內可退貨？", 1).Value.Top.ShouldBe(1);
        KnowledgeRetrievalRules.ValidatePreview("幾天內可退貨？", 20).Value.Top.ShouldBe(20);
        KnowledgeRetrievalRules.ValidatePreview("幾天內可退貨？", 21).Failures.ShouldBe([new("top", "筆數必須介於 1 到 20 之間。")]);
        KnowledgeRetrievalRules.ValidatePreview("", 0).Failures.Select(failure => failure.Field).ShouldBe(["question", "top"]);
    }

    [Fact]
    public void An_excerpt_is_the_whole_text_up_to_300_characters_and_is_cut_with_an_ellipsis_after_that()
    {
        var exactly = new string('段', KnowledgeRetrievalRules.ExcerptMaxLength);
        KnowledgeRetrievalRules.Excerpt(exactly).ShouldBeSameAs(exactly);
        KnowledgeRetrievalRules.Excerpt("第 2 頁\n收到商品後七天內可申請退貨。").ShouldBe("第 2 頁\n收到商品後七天內可申請退貨。");

        KnowledgeRetrievalRules.Excerpt(exactly + "多").ShouldBe(exactly + "…");

        // White space at the cut goes; a character outside the BMP counts once and is never split.
        KnowledgeRetrievalRules.Excerpt(new string('段', 298) + "  尾巴").ShouldBe(new string('段', 298) + "…");
        var wide = string.Concat(Enumerable.Repeat("𠀀", 301));
        var cut = KnowledgeRetrievalRules.Excerpt(wide);
        cut.ShouldBe(string.Concat(Enumerable.Repeat("𠀀", 300)) + "…");
        cut.EnumerateRunes().Count().ShouldBe(301);
    }
}
