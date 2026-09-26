using Shouldly;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Domain.Tests;

public class KnowledgeDocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Uploader = Guid.CreateVersion7();
    private static readonly string Sha = new('a', 64);

    private static readonly KnowledgeBase KnowledgeBase =
        KnowledgeBase.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "退換貨政策", string.Empty, Now);

    [Fact]
    public void An_uploaded_document_belongs_to_its_knowledge_base_and_is_named_after_the_file()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);

        document.Id.ShouldNotBe(Guid.Empty);
        document.OrganizationId.ShouldBe(KnowledgeBase.OrganizationId);
        document.KnowledgeBaseId.ShouldBe(KnowledgeBase.Id);
        document.Kind.ShouldBe(KnowledgeItemKind.Document);
        document.Name.ShouldBe("退貨政策.pdf");
        document.CreatedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 退貨政策.pdf")]
    [InlineData("退貨政策.pdf ")]
    public void A_name_the_upload_rules_would_not_produce_is_a_caller_bug(string name)
    {
        Should.Throw<ArgumentException>(() => KnowledgeDocument.CreateUploaded(KnowledgeBase, name, Now));
        Should.Throw<ArgumentException>(() =>
            KnowledgeDocument.CreateUploaded(KnowledgeBase, new string('名', KnowledgeDocument.NameMaxLength + 1), Now));
    }

    [Fact]
    public void A_new_version_is_queued_and_copies_its_documents_ids()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);
        var batch = Guid.NewGuid();

        var version = NewVersion(document, batch);

        version.Id.ShouldNotBe(Guid.Empty);
        version.OrganizationId.ShouldBe(document.OrganizationId);
        version.KnowledgeBaseId.ShouldBe(document.KnowledgeBaseId);
        version.DocumentId.ShouldBe(document.Id);
        version.VersionNumber.ShouldBe(1);
        version.FileName.ShouldBe("退貨政策.pdf");
        version.ContentType.ShouldBe("application/pdf");
        version.SizeBytes.ShouldBe(3);
        version.Sha256.ShouldBe(Sha);
        version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Queued);
        version.Issue.ShouldBeNull();
        version.UploadBatchId.ShouldBe(batch);
        version.UploadedByAccountId.ShouldBe(Uploader);
        version.UploadedAt.ShouldBe(Now);
        version.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void Versions_refuse_values_the_upload_rules_never_produce()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);

        Should.Throw<ArgumentException>(() => KnowledgeDocumentVersion.Create(
            document, 1, "退貨政策.pdf", "application/octet-stream", 3, Sha, Uploader, null, Now));
        Should.Throw<ArgumentException>(() => KnowledgeDocumentVersion.Create(
            document, 1, "退貨政策.pdf", "application/pdf", 3, Sha.ToUpperInvariant(), Uploader, null, Now));
        Should.Throw<ArgumentException>(() => KnowledgeDocumentVersion.Create(
            document, 1, "退貨政策.pdf", "application/pdf", 3, Sha[1..], Uploader, null, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => KnowledgeDocumentVersion.Create(
            document, 0, "退貨政策.pdf", "application/pdf", 3, Sha, Uploader, null, Now));
        Should.Throw<ArgumentException>(() => KnowledgeDocumentVersion.Create(
            document, 1, "退貨政策.pdf", "application/pdf", 3, Sha, Guid.Empty, null, Now));
        Should.Throw<ArgumentException>(() => KnowledgeDocumentVersion.Create(
            document, 1, "退貨政策.pdf", "application/pdf", 3, Sha, Uploader, Guid.Empty, Now));
    }

    [Fact]
    public void Processing_goes_queued_then_processing_then_failed_and_only_failed_can_be_requeued()
    {
        var version = NewVersion(KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now), null);

        Should.Throw<InvalidOperationException>(() => version.Requeue(Now));

        version.StartProcessing(Now.AddSeconds(1));
        version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Processing);
        version.UpdatedAt.ShouldBe(Now.AddSeconds(1));
        Should.Throw<InvalidOperationException>(() => version.StartProcessing(Now));
        Should.Throw<InvalidOperationException>(() => version.Requeue(Now));

        version.MarkFailed("解析功能尚未啟用", Now.AddSeconds(2));
        version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Failed);
        version.Issue.ShouldBe("解析功能尚未啟用");
        Should.Throw<InvalidOperationException>(() => version.MarkFailed("again", Now));

        version.Requeue(Now.AddSeconds(3));
        version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Queued);
        version.Issue.ShouldBeNull();
        version.UpdatedAt.ShouldBe(Now.AddSeconds(3));

        // A queued version can fail straight away too (e.g. its job gave up).
        version.MarkFailed("處理時發生錯誤，請重試。", Now.AddSeconds(4));
        version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Failed);
    }

    [Fact]
    public void An_issue_is_required_and_bounded()
    {
        var version = NewVersion(KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now), null);

        Should.Throw<ArgumentException>(() => version.MarkFailed(" ", Now));
        Should.Throw<ArgumentException>(() => version.MarkFailed(new string('錯', KnowledgeDocumentVersion.IssueMaxLength + 1), Now));
        version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Queued);
    }

    [Fact]
    public void File_content_must_match_its_versions_size_and_organization()
    {
        var version = NewVersion(KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now), null);

        var content = new KnowledgeFileContent(version, [1, 2, 3]);

        content.VersionId.ShouldBe(version.Id);
        content.OrganizationId.ShouldBe(version.OrganizationId);
        content.Bytes.ShouldBe(new byte[] { 1, 2, 3 });
        Should.Throw<ArgumentException>(() => new KnowledgeFileContent(version, [1, 2]));
    }

    [Fact]
    public void Document_activity_rows_carry_ids_only()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);
        var version = NewVersion(document, null);

        var uploaded = KnowledgeActivity.DocumentUploaded(version, Uploader, Now);
        var retried = KnowledgeActivity.VersionRetried(version, Uploader, Now);
        var deleted = KnowledgeActivity.DocumentDeleted(document, Uploader, Now);

        foreach (var activity in new[] { uploaded, retried, deleted })
        {
            activity.OrganizationId.ShouldBe(KnowledgeBase.OrganizationId);
            activity.KnowledgeBaseId.ShouldBe(KnowledgeBase.Id);
            activity.DocumentId.ShouldBe(document.Id);
            activity.ActorAccountId.ShouldBe(Uploader);
            activity.Detail.ShouldBeNull();
        }

        uploaded.Action.ShouldBe(KnowledgeActivityAction.DocumentUploaded);
        uploaded.VersionId.ShouldBe(version.Id);
        retried.Action.ShouldBe(KnowledgeActivityAction.VersionRetried);
        retried.VersionId.ShouldBe(version.Id);
        deleted.Action.ShouldBe(KnowledgeActivityAction.DocumentDeleted);
        deleted.VersionId.ShouldBeNull();

        KnowledgeActivity.KnowledgeBaseCreated(KnowledgeBase, Uploader, Now).DocumentId.ShouldBeNull();
    }

    private static KnowledgeDocumentVersion NewVersion(KnowledgeDocument document, Guid? batch) =>
        KnowledgeDocumentVersion.Create(document, 1, "退貨政策.pdf", "application/pdf", 3, Sha, Uploader, batch, Now);
}
