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

        var state = KnowledgeItemStates.Of(new[] { document }.AsQueryable(), new[] { second, first }.AsQueryable(), Now)
            .ShouldHaveSingleItem();

        state.ShouldBe(new KnowledgeItemState(
            document.Id, KnowledgeBase.Id, KnowledgeItemKind.Document, "退貨政策.pdf", Now,
            KnowledgeDocumentStatus.Queued, null, second.UpdatedAt,
            second.Id, 2, KnowledgeVersionState.PendingReview, EffectiveVersionNumber: null, Disabled: false));
        state.InEffect.ShouldBeFalse();
        state.AwaitingApproval.ShouldBeFalse("still queued");

        var onlyFirst = KnowledgeItemStates.Of(new[] { document }.AsQueryable(), new[] { first }.AsQueryable(), Now).Single();
        onlyFirst.Status.ShouldBe(KnowledgeDocumentStatus.Failed);
        onlyFirst.Issue.ShouldBe("解析功能尚未啟用");
        onlyFirst.UpdatedAt.ShouldBe(Now.AddMinutes(2));
    }

    [Fact]
    public void A_document_without_any_version_is_not_listed()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);

        KnowledgeItemStates.Of(new[] { document }.AsQueryable(), Array.Empty<KnowledgeDocumentVersion>().AsQueryable(), Now)
            .ShouldBeEmpty();
    }

    [Fact]
    public void A_document_shows_its_latest_versions_processing_next_to_the_version_in_effect()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);
        var first = Processed(Version(document, 1, 'a'));
        first.Approve(Uploader, Now.AddHours(1), Now.AddHours(1));
        var second = Processed(Version(document, 2, 'b'));
        var later = Now.AddHours(2);

        var state = States(later, document, first, second).Single();

        // Processing and review of the latest version (v2, approvable), and v1 in effect.
        (state.Status, state.LatestVersionNumber, state.LatestVersionState).ShouldBe((KnowledgeDocumentStatus.Ready, 2, KnowledgeVersionState.PendingReview));
        state.EffectiveVersionNumber.ShouldBe(1);
        state.InEffect.ShouldBeTrue();
        state.AwaitingApproval.ShouldBeTrue();

        // v2 scheduled for tomorrow: still v1 today, v2 from tomorrow on.
        second.Approve(Uploader, later.AddDays(1), later);
        var today = States(later, document, first, second).Single();
        (today.LatestVersionState, today.EffectiveVersionNumber, today.AwaitingApproval).ShouldBe((KnowledgeVersionState.Scheduled, (int?)1, false));
        var tomorrow = States(later.AddDays(1), document, first, second).Single();
        (tomorrow.LatestVersionState, tomorrow.EffectiveVersionNumber).ShouldBe((KnowledgeVersionState.Effective, (int?)2));

        // Disabled: still has a version in effect, but is not in effect.
        document.Disable(Uploader, "價格錯誤", later);
        var disabled = States(later.AddDays(1), document, first, second).Single();
        (disabled.EffectiveVersionNumber, disabled.Disabled, disabled.InEffect).ShouldBe(((int?)2, true, false));
    }

    [Fact]
    public void The_tally_counts_kinds_and_every_status_and_keeps_the_latest_change()
    {
        var items = new[]
        {
            Item(KnowledgeItemKind.Document, KnowledgeDocumentStatus.Queued, Now, effectiveVersion: 1),
            Item(KnowledgeItemKind.Document, KnowledgeDocumentStatus.Failed, Now.AddHours(2)),
            Item(KnowledgeItemKind.Document, KnowledgeDocumentStatus.Failed, Now.AddHours(1), KnowledgeVersionState.Effective, 1, disabled: true),
            Item(KnowledgeItemKind.Faq, KnowledgeDocumentStatus.Ready, Now),
            Item(KnowledgeItemKind.Document, KnowledgeDocumentStatus.PartiallyReadable, Now, KnowledgeVersionState.Effective, 3),
        };

        var tally = KnowledgeBaseTally.Of(items);

        tally.DocumentCount.ShouldBe(4);
        tally.FaqCount.ShouldBe(1);
        tally.StatusCounts.ShouldBe(
            new Dictionary<KnowledgeDocumentStatus, int>
            {
                [KnowledgeDocumentStatus.Queued] = 1,
                [KnowledgeDocumentStatus.Processing] = 0,
                [KnowledgeDocumentStatus.Ready] = 1,
                [KnowledgeDocumentStatus.PartiallyReadable] = 1,
                [KnowledgeDocumentStatus.Failed] = 2,
            },
            ignoreOrder: true);
        (tally.InEffectCount, tally.AwaitingApprovalCount, tally.DisabledCount).ShouldBe((2, 1, 1));
        tally.LastItemUpdate.ShouldBe(Now.AddHours(2));

        KnowledgeBaseTally.Empty.DocumentCount.ShouldBe(0);
        KnowledgeBaseTally.Empty.StatusCounts.Count.ShouldBe(5);
        KnowledgeBaseTally.Empty.StatusCounts.Values.ShouldAllBe(count => count == 0);
        KnowledgeBaseTally.Empty.LastItemUpdate.ShouldBeNull();
        (KnowledgeBaseTally.Empty.InEffectCount, KnowledgeBaseTally.Empty.AwaitingApprovalCount, KnowledgeBaseTally.Empty.DisabledCount).ShouldBe((0, 0, 0));
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

    private static List<KnowledgeItemState> States(DateTimeOffset now, KnowledgeDocument document, params KnowledgeDocumentVersion[] versions) =>
        [.. KnowledgeItemStates.Of(new[] { document }.AsQueryable(), versions.AsQueryable(), now)];

    private static KnowledgeDocumentVersion Processed(KnowledgeDocumentVersion version)
    {
        version.StartProcessing(version.UploadedAt);
        version.CompleteProcessing(KnowledgeDocumentStatus.Ready, null, version.UploadedAt);
        return version;
    }

    private static KnowledgeItemState Item(
        KnowledgeItemKind kind,
        KnowledgeDocumentStatus status,
        DateTimeOffset updatedAt,
        KnowledgeVersionState state = KnowledgeVersionState.PendingReview,
        int? effectiveVersion = null,
        bool disabled = false) =>
        new(Guid.NewGuid(), KnowledgeBase.Id, kind, "項目", Now, status, null, updatedAt, Guid.NewGuid(), 1, state, effectiveVersion, disabled);
}
