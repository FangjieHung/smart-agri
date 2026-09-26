using System.Text;
using Shouldly;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge;

public class KnowledgeFaqRulesTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" \n\t　", "\r\n  \n")]
    public void Blank_questions_and_answers_are_both_reported(string? question, string? answer)
    {
        KnowledgeFaqRules.Validate(question, answer).Failures.ShouldBe(
        [
            new ValidationFailure("question", KnowledgeFaqRules.QuestionRequiredMessage),
            new ValidationFailure("answer", KnowledgeFaqRules.AnswerRequiredMessage),
        ]);
    }

    [Fact]
    public void A_question_may_have_500_characters_and_an_answer_4000_after_normalization()
    {
        var longestQuestion = new string('問', 500);
        var longestAnswer = new string('答', 4000);

        var valid = KnowledgeFaqRules.Validate($"  {longestQuestion}\n", $"\n{longestAnswer}  ").Value;
        (valid.Entry.Question, valid.Entry.Answer).ShouldBe((longestQuestion, longestAnswer));

        KnowledgeFaqRules.Validate(longestQuestion + "問", longestAnswer + "答").Failures.ShouldBe(
        [
            new ValidationFailure("question", "問題最多 500 個字。"),
            new ValidationFailure("answer", "答案最多 4000 個字。"),
        ]);
    }

    [Fact]
    public void The_question_becomes_one_line_and_the_answer_keeps_its_lines_normalized()
    {
        var draft = KnowledgeFaqRules.Validate(
            " 收到商品\r\n幾天內   可退貨？ ",
            "\r\n收到商品後七天內可申請退貨。  \r\n\r\n\r\n請保留發票。\n").Value;

        draft.Entry.Question.ShouldBe("收到商品 幾天內 可退貨？");
        draft.Entry.Answer.ShouldBe("收到商品後七天內可申請退貨。\n\n請保留發票。");
    }

    [Fact]
    public void The_draft_is_the_stored_bytes_their_sha256_and_the_name()
    {
        var draft = KnowledgeFaqRules.Validate("收到商品幾天內可退貨？", "七天內。").Value;

        draft.Content.ShouldBe(draft.Entry.ToContent());
        draft.Sha256.ShouldBe(KnowledgeUploadRules.Sha256(draft.Content));
        draft.Name.ShouldBe("收到商品幾天內可退貨？");

        // The same entry typed differently is the same content, so the duplicate rule sees it.
        KnowledgeFaqRules.Validate("收到商品幾天內可退貨？ ", "七天內。\r\n").Value.Sha256.ShouldBe(draft.Sha256);
        // NFC: a decomposed character is the composed one.
        KnowledgeFaqRules.Validate("Cafe\u0301？", "是。").Value.Entry.Question.ShouldBe("Caf\u00e9？");
        KnowledgeFaqRules.Validate("收到商品幾天內可退貨？", "十天內。").Value.Sha256.ShouldNotBe(draft.Sha256);
    }

    [Fact]
    public void A_question_longer_than_a_name_is_listed_cut_with_an_ellipsis()
    {
        var exactly = new string('問', KnowledgeDocument.NameMaxLength);
        KnowledgeFaqRules.NameFor(exactly).ShouldBe(exactly);

        var longer = KnowledgeFaqRules.NameFor(exactly + "多");
        longer.ShouldBe(new string('問', KnowledgeDocument.NameMaxLength - 1) + "…");
        longer.Length.ShouldBe(KnowledgeDocument.NameMaxLength);

        // Never a space before the ellipsis, never half a surrogate pair.
        KnowledgeFaqRules.NameFor(new string('問', 253) + " 尾巴還很長").ShouldBe(new string('問', 253) + "…");
        var wide = KnowledgeFaqRules.NameFor(new string('問', 253) + "𠀀𠀀");
        wide.ShouldBe(new string('問', 253) + "…");
        Should.NotThrow(() => Encoding.UTF8.GetBytes(wide));
    }

    [Fact]
    public void A_new_entry_duplicating_content_is_refused_first_then_one_whose_name_is_taken()
    {
        var existing = new KnowledgeExistingContent(Guid.NewGuid(), "收到商品幾天內可退貨？", 2);

        var sameContent = KnowledgeFaqRules.CheckNewEntryDuplicates("收到商品幾天內可退貨？", existing, nameTaken: true)!;
        (sameContent.Reason, sameContent.ExistingDocumentName).ShouldBe((KnowledgeUploadRejectionReason.DuplicateContent, "收到商品幾天內可退貨？"));
        sameContent.Message.ShouldContain("內容完全相同");

        var sameName = KnowledgeFaqRules.CheckNewEntryDuplicates("收到商品幾天內可退貨？", null, nameTaken: true)!;
        (sameName.Reason, sameName.ExistingDocumentName).ShouldBe((KnowledgeUploadRejectionReason.DuplicateName, (string?)null));
        sameName.Message.ShouldContain("「收到商品幾天內可退貨？」");

        KnowledgeFaqRules.CheckNewEntryDuplicates("收到商品幾天內可退貨？", null, nameTaken: false).ShouldBeNull();
    }

    [Fact]
    public void An_edit_duplicating_any_version_or_another_items_name_is_refused()
    {
        var faqId = Guid.NewGuid();

        var own = KnowledgeFaqRules.CheckEditDuplicates(faqId, "問", new KnowledgeExistingContent(faqId, "問", 1), nameTakenByAnother: false)!;
        own.Reason.ShouldBe(KnowledgeUploadRejectionReason.DuplicateContent);
        own.Message.ShouldBe("內容與這則 FAQ 的第 1 版完全相同，不需要再新增一個版本。");

        var other = KnowledgeFaqRules.CheckEditDuplicates(faqId, "問", new KnowledgeExistingContent(Guid.NewGuid(), "別的問題", 3), nameTakenByAnother: true)!;
        (other.Reason, other.ExistingDocumentName).ShouldBe((KnowledgeUploadRejectionReason.DuplicateContent, "別的問題"));
        other.Message.ShouldContain("「別的問題」");

        KnowledgeFaqRules.CheckEditDuplicates(faqId, "別的問題", null, nameTakenByAnother: true)!.Reason
            .ShouldBe(KnowledgeUploadRejectionReason.DuplicateName);
        KnowledgeFaqRules.CheckEditDuplicates(faqId, "問", null, nameTakenByAnother: false).ShouldBeNull();
    }
}
