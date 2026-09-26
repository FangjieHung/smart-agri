using System.Text;
using Shouldly;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Domain.Tests;

/// <summary>FAQ entries (M2 plan Slice 10): their stored content, documents and versions.</summary>
public class KnowledgeFaqTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Author = Guid.CreateVersion7();
    private static readonly string Sha = new('b', 64);

    private static readonly KnowledgeBase KnowledgeBase =
        KnowledgeBase.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "常見問題", string.Empty, Now);

    [Fact]
    public void An_entry_is_stored_as_canonical_utf8_json_and_read_back()
    {
        var entry = new KnowledgeFaqEntry("收到商品幾天內可退貨？", "收到商品後七天內可申請退貨。\n請保留發票。");

        var content = entry.ToContent();

        Encoding.UTF8.GetString(content).ShouldBe("{\"question\":\"收到商品幾天內可退貨？\",\"answer\":\"收到商品後七天內可申請退貨。\\n請保留發票。\"}");
        content[0].ShouldBe((byte)'{', "no BOM");
        new KnowledgeFaqEntry(entry.Question, entry.Answer).ToContent().ShouldBe(content, "the same entry, the same bytes");
        var read = KnowledgeFaqEntry.FromContent(content);
        (read.Question, read.Answer).ShouldBe((entry.Question, entry.Answer));
    }

    [Fact]
    public void Html_sensitive_characters_are_escaped_and_still_read_back()
    {
        var entry = new KnowledgeFaqEntry("<b>運費</b> & \"退貨\"？", "a'b");

        var json = Encoding.UTF8.GetString(entry.ToContent());

        json.ShouldNotContain("<b>");
        var read = KnowledgeFaqEntry.FromContent(entry.ToContent());
        (read.Question, read.Answer).ShouldBe((entry.Question, entry.Answer));
    }

    [Theory]
    [InlineData("", "答")]
    [InlineData(" ", "答")]
    [InlineData(" 問", "答")]
    [InlineData("問", "")]
    [InlineData("問", "答\n")]
    public void Blank_or_padded_text_is_a_caller_bug(string question, string answer)
    {
        Should.Throw<ArgumentException>(() => new KnowledgeFaqEntry(question, answer));
    }

    [Fact]
    public void Question_and_answer_have_limits()
    {
        Should.NotThrow(() => new KnowledgeFaqEntry(
            new string('問', KnowledgeFaqEntry.QuestionMaxLength), new string('答', KnowledgeFaqEntry.AnswerMaxLength)));
        Should.Throw<ArgumentException>(() => new KnowledgeFaqEntry(new string('問', KnowledgeFaqEntry.QuestionMaxLength + 1), "答"));
        Should.Throw<ArgumentException>(() => new KnowledgeFaqEntry("問", new string('答', KnowledgeFaqEntry.AnswerMaxLength + 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"question\":\"問\"}")]
    [InlineData("{\"question\":\"問\",\"answer\":1}")]
    [InlineData("{\"question\":\" \",\"answer\":\"答\"}")]
    public void Anything_else_is_not_an_entry(string stored)
    {
        Should.Throw<FormatException>(() => KnowledgeFaqEntry.FromContent(Encoding.UTF8.GetBytes(stored)));
    }

    [Fact]
    public void An_faq_entry_is_a_document_of_kind_faq_and_its_versions_hold_the_entry()
    {
        var document = KnowledgeDocument.CreateFaq(KnowledgeBase, "收到商品幾天內可退貨？", Now);
        var content = new KnowledgeFaqEntry("收到商品幾天內可退貨？", "七天內。").ToContent();

        var version = KnowledgeDocumentVersion.CreateFaq(document, 1, document.Name, content, Sha, Author, Now);

        (document.Kind, document.Name, document.KnowledgeBaseId, document.OrganizationId)
            .ShouldBe((KnowledgeItemKind.Faq, "收到商品幾天內可退貨？", KnowledgeBase.Id, KnowledgeBase.OrganizationId));
        version.DocumentId.ShouldBe(document.Id);
        version.ContentType.ShouldBe(KnowledgeFaqEntry.ContentType);
        version.IsFaq.ShouldBeTrue();
        version.FileName.ShouldBe("收到商品幾天內可退貨？");
        version.SizeBytes.ShouldBe(content.LongLength);
        version.Sha256.ShouldBe(Sha);
        version.UploadBatchId.ShouldBeNull();
        (version.ProcessingStatus, version.ReviewState).ShouldBe((KnowledgeDocumentStatus.Queued, KnowledgeReviewState.PendingReview));
        new KnowledgeFileContent(version, content).Bytes.ShouldBe(content);
    }

    [Fact]
    public void Files_and_faq_entries_never_mix_their_versions()
    {
        var faq = KnowledgeDocument.CreateFaq(KnowledgeBase, "問題", Now);
        var uploaded = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);

        Should.Throw<ArgumentException>(() => KnowledgeDocumentVersion.Create(faq, 2, "退貨政策.pdf", "application/pdf", 3, Sha, Author, null, Now));
        Should.Throw<ArgumentException>(() => KnowledgeDocumentVersion.CreateFaq(uploaded, 2, "問題", [1, 2, 3], Sha, Author, Now));
        Should.Throw<ArgumentException>(() => KnowledgeDocumentVersion.Create(
            uploaded, 2, "faq", KnowledgeFaqEntry.ContentType, 3, Sha, Author, null, Now), "no upload can have the FAQ content type");
        KnowledgeDocumentVersion.Create(uploaded, 1, "退貨政策.pdf", "application/pdf", 3, Sha, Author, null, Now).IsFaq.ShouldBeFalse();
    }

    [Fact]
    public void Only_an_faq_entry_is_renamed_and_within_the_name_rules()
    {
        var faq = KnowledgeDocument.CreateFaq(KnowledgeBase, "舊問題", Now);

        faq.RenameFaq("新問題");

        faq.Name.ShouldBe("新問題");
        Should.Throw<ArgumentException>(() => faq.RenameFaq(" 新問題"));
        Should.Throw<ArgumentException>(() => faq.RenameFaq(new string('問', KnowledgeDocument.NameMaxLength + 1)));
        Should.Throw<ArgumentException>(() => KnowledgeDocument.CreateFaq(KnowledgeBase, "", Now));
        Should.Throw<InvalidOperationException>(() => KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now).RenameFaq("新名稱"));
    }

    [Fact]
    public void Faq_activity_rows_carry_ids_only_and_deleting_an_entry_says_so()
    {
        var faq = KnowledgeDocument.CreateFaq(KnowledgeBase, "問題", Now);
        var version = KnowledgeDocumentVersion.CreateFaq(faq, 1, faq.Name, [1, 2, 3], Sha, Author, Now);

        var created = KnowledgeActivity.FaqCreated(version, Author, Now);
        var updated = KnowledgeActivity.FaqUpdated(version, Author, Now);
        var deleted = KnowledgeActivity.DocumentDeleted(faq, Author, Now);

        (created.Action, updated.Action, deleted.Action)
            .ShouldBe((KnowledgeActivityAction.FaqCreated, KnowledgeActivityAction.FaqUpdated, KnowledgeActivityAction.FaqDeleted));
        foreach (var activity in new[] { created, updated, deleted })
        {
            (activity.KnowledgeBaseId, activity.DocumentId, activity.ActorAccountId, activity.Detail)
                .ShouldBe((KnowledgeBase.Id, (Guid?)faq.Id, (Guid?)Author, (string?)null));
        }

        (created.VersionId, updated.VersionId, deleted.VersionId).ShouldBe(((Guid?)version.Id, (Guid?)version.Id, (Guid?)null));
        KnowledgeActivity.DocumentDeleted(KnowledgeDocument.CreateUploaded(KnowledgeBase, "a.pdf", Now), Author, Now)
            .Action.ShouldBe(KnowledgeActivityAction.DocumentDeleted);
    }
}
