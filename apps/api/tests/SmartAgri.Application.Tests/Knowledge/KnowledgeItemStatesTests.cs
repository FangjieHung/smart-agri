using Shouldly;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge;

public class KnowledgeItemStatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Uploader = Guid.CreateVersion7();

    private static readonly KnowledgeBase KnowledgeBase =
        KnowledgeBase.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "退換貨政策", string.Empty, Now);

    [Fact]
    public void A_document_shows_its_latest_versions_status_issue_and_time()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);
        var first = Version(document, 1, 'a');
        first.StartProcessing(Now.AddMinutes(1));
        first.MarkFailed("解析功能尚未啟用", Now.AddMinutes(2));
        var second = Version(document, 2, 'b');

        var state = KnowledgeItemStates.Of(new[] { document }.AsQueryable(), new[] { second, first }.AsQueryable())
            .ShouldHaveSingleItem();

        state.ShouldBe(new KnowledgeItemState(
            document.Id, KnowledgeBase.Id, KnowledgeItemKind.Document, "退貨政策.pdf", Now,
            KnowledgeDocumentStatus.Queued, null, second.UpdatedAt));

        var onlyFirst = KnowledgeItemStates.Of(new[] { document }.AsQueryable(), new[] { first }.AsQueryable()).Single();
        onlyFirst.Status.ShouldBe(KnowledgeDocumentStatus.Failed);
        onlyFirst.Issue.ShouldBe("解析功能尚未啟用");
        onlyFirst.UpdatedAt.ShouldBe(Now.AddMinutes(2));
    }

    [Fact]
    public void A_document_without_any_version_is_not_listed()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);

        KnowledgeItemStates.Of(new[] { document }.AsQueryable(), Array.Empty<KnowledgeDocumentVersion>().AsQueryable())
            .ShouldBeEmpty();
    }

    [Fact]
    public void The_tally_counts_kinds_and_every_status_and_keeps_the_latest_change()
    {
        var items = new[]
        {
            Item(KnowledgeItemKind.Document, KnowledgeDocumentStatus.Queued, Now),
            Item(KnowledgeItemKind.Document, KnowledgeDocumentStatus.Failed, Now.AddHours(2)),
            Item(KnowledgeItemKind.Document, KnowledgeDocumentStatus.Failed, Now.AddHours(1)),
            Item(KnowledgeItemKind.Faq, KnowledgeDocumentStatus.Ready, Now),
        };

        var tally = KnowledgeBaseTally.Of(items);

        tally.DocumentCount.ShouldBe(3);
        tally.FaqCount.ShouldBe(1);
        tally.StatusCounts.ShouldBe(
            new Dictionary<KnowledgeDocumentStatus, int>
            {
                [KnowledgeDocumentStatus.Queued] = 1,
                [KnowledgeDocumentStatus.Processing] = 0,
                [KnowledgeDocumentStatus.Ready] = 1,
                [KnowledgeDocumentStatus.PartiallyReadable] = 0,
                [KnowledgeDocumentStatus.Failed] = 2,
            },
            ignoreOrder: true);
        tally.LastItemUpdate.ShouldBe(Now.AddHours(2));

        KnowledgeBaseTally.Empty.DocumentCount.ShouldBe(0);
        KnowledgeBaseTally.Empty.StatusCounts.Count.ShouldBe(5);
        KnowledgeBaseTally.Empty.StatusCounts.Values.ShouldAllBe(count => count == 0);
        KnowledgeBaseTally.Empty.LastItemUpdate.ShouldBeNull();
    }

    [Fact]
    public void Only_failed_versions_can_be_retried()
    {
        KnowledgeVersionRules.RetryRefusal(KnowledgeDocumentStatus.Failed).ShouldBeNull();
        KnowledgeVersionRules.RetryRefusal(KnowledgeDocumentStatus.Queued).ShouldBe(KnowledgeVersionRules.StillProcessingMessage);
        KnowledgeVersionRules.RetryRefusal(KnowledgeDocumentStatus.Processing).ShouldBe(KnowledgeVersionRules.StillProcessingMessage);
        KnowledgeVersionRules.RetryRefusal(KnowledgeDocumentStatus.Ready).ShouldBe(KnowledgeVersionRules.NotFailedMessage);
        KnowledgeVersionRules.RetryRefusal(KnowledgeDocumentStatus.PartiallyReadable).ShouldBe(KnowledgeVersionRules.NotFailedMessage);
    }

    private static KnowledgeDocumentVersion Version(KnowledgeDocument document, int number, char sha) =>
        KnowledgeDocumentVersion.Create(
            document, number, document.Name, "application/pdf", 1, new string(sha, 64), Uploader, null, Now.AddMinutes(number * 10));

    private static KnowledgeItemState Item(KnowledgeItemKind kind, KnowledgeDocumentStatus status, DateTimeOffset updatedAt) =>
        new(Guid.NewGuid(), KnowledgeBase.Id, kind, "項目", Now, status, null, updatedAt);
}
