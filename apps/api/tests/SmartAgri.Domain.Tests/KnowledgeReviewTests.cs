using Shouldly;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Domain.Tests;

/// <summary>Version approval and emergency disabling (M2 plan, Slice 8; ticket #42) on the
/// entities themselves; the eligibility rule built on them is tested in Application.</summary>
public class KnowledgeReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Uploader = Guid.CreateVersion7();
    private static readonly Guid Approver = Guid.CreateVersion7();

    private static readonly KnowledgeBase KnowledgeBase =
        KnowledgeBase.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "退換貨政策", string.Empty, Now);

    [Fact]
    public void Every_new_version_is_pending_review_and_linked_to_its_document_in_memory()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);

        var first = NewVersion(document, 1, 'a');
        var second = NewVersion(document, 2, 'b');

        foreach (var version in new[] { first, second })
        {
            version.ReviewState.ShouldBe(KnowledgeReviewState.PendingReview);
            version.EffectiveFrom.ShouldBeNull();
            version.ApprovedByAccountId.ShouldBeNull();
            version.ApprovedAt.ShouldBeNull();
            version.Document.ShouldBeSameAs(document);
            version.CanBeApproved.ShouldBeFalse("it is not processed yet");
        }

        document.Versions.ShouldBe([first, second]);
        var chunk = KnowledgeChunk.Create(Processed(NewVersion(document, 3, 'c')), 0, 0, "第 1 頁", "退貨期限為七天。");
        chunk.Version.ShouldNotBeNull().VersionNumber.ShouldBe(3);
    }

    [Theory]
    [InlineData(KnowledgeDocumentStatus.Ready)]
    [InlineData(KnowledgeDocumentStatus.PartiallyReadable)]
    public void A_readable_version_is_approved_once_with_its_approver_times_and_effective_date(KnowledgeDocumentStatus outcome)
    {
        var version = Processed(NewVersion(KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now), 1, 'a'), outcome);
        version.CanBeApproved.ShouldBeTrue();

        version.Approve(Approver, Now.AddDays(1), Now.AddMinutes(5));

        version.ReviewState.ShouldBe(KnowledgeReviewState.Approved);
        version.EffectiveFrom.ShouldBe(Now.AddDays(1));
        version.ApprovedByAccountId.ShouldBe(Approver);
        version.ApprovedAt.ShouldBe(Now.AddMinutes(5));
        version.UpdatedAt.ShouldBe(Now.AddMinutes(5));
        version.ProcessingStatus.ShouldBe(outcome, "approval never changes the processing status");
        version.CanBeApproved.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => version.Approve(Approver, Now.AddDays(2), Now.AddDays(2)));
    }

    [Fact]
    public void Unprocessed_or_failed_versions_an_empty_approver_and_a_past_effective_date_are_caller_bugs()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);
        var queued = NewVersion(document, 1, 'a');
        var processing = NewVersion(document, 2, 'b');
        processing.StartProcessing(Now);
        var failed = NewVersion(document, 3, 'c');
        failed.MarkFailed("找不到可讀文字", Now);

        foreach (var version in new[] { queued, processing, failed })
        {
            version.CanBeApproved.ShouldBeFalse(version.ProcessingStatus.ToString());
            Should.Throw<InvalidOperationException>(() => version.Approve(Approver, Now, Now));
            version.ReviewState.ShouldBe(KnowledgeReviewState.PendingReview);
        }

        var ready = Processed(NewVersion(document, 4, 'd'));
        Should.Throw<ArgumentException>(() => ready.Approve(Guid.Empty, Now, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => ready.Approve(Approver, Now.AddTicks(-1), Now));
        ready.ReviewState.ShouldBe(KnowledgeReviewState.PendingReview);
    }

    [Fact]
    public void Disabling_records_who_when_and_why_and_enabling_clears_exactly_that()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);

        document.Disable(Approver, "價格錯誤，暫停使用", Now.AddHours(1));

        document.DisabledAt.ShouldBe(Now.AddHours(1));
        document.DisabledByAccountId.ShouldBe(Approver);
        document.DisabledReason.ShouldBe("價格錯誤，暫停使用");
        Should.Throw<InvalidOperationException>(() => document.Disable(Approver, "再一次", Now));

        document.Enable();

        document.DisabledAt.ShouldBeNull();
        document.DisabledByAccountId.ShouldBeNull();
        document.DisabledReason.ShouldBeNull();
        Should.Throw<InvalidOperationException>(document.Enable);
    }

    [Fact]
    public void A_disable_reason_the_rules_would_not_accept_is_a_caller_bug()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);

        Should.Throw<ArgumentException>(() => document.Disable(Approver, "", Now));
        Should.Throw<ArgumentException>(() => document.Disable(Approver, " 前後空白 ", Now));
        Should.Throw<ArgumentException>(() => document.Disable(Approver, new string('因', KnowledgeDocument.DisabledReasonMaxLength + 1), Now));
        Should.Throw<ArgumentException>(() => document.Disable(Guid.Empty, "價格錯誤", Now));
        document.DisabledAt.ShouldBeNull();

        document.Disable(Approver, new string('因', KnowledgeDocument.DisabledReasonMaxLength), Now);
        document.DisabledReason!.Length.ShouldBe(KnowledgeDocument.DisabledReasonMaxLength);
    }

    [Fact]
    public void Review_and_disable_activity_rows_name_the_actor_and_carry_ids_only()
    {
        var document = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Now);
        var version = NewVersion(document, 2, 'b');

        var uploaded = KnowledgeActivity.VersionUploaded(version, Uploader, Now);
        var approved = KnowledgeActivity.VersionApproved(version, Approver, Now);
        var disabled = KnowledgeActivity.DocumentDisabled(document, "條款待法務確認", Approver, Now);
        var enabled = KnowledgeActivity.DocumentEnabled(document, Approver, Now);

        (uploaded.Action, uploaded.VersionId, uploaded.ActorAccountId).ShouldBe((KnowledgeActivityAction.VersionUploaded, version.Id, Uploader));
        (approved.Action, approved.VersionId, approved.ActorAccountId).ShouldBe((KnowledgeActivityAction.VersionApproved, version.Id, Approver));
        (disabled.Action, disabled.VersionId, disabled.ActorAccountId).ShouldBe((KnowledgeActivityAction.DocumentDisabled, (Guid?)null, Approver));
        (enabled.Action, enabled.VersionId, enabled.ActorAccountId).ShouldBe((KnowledgeActivityAction.DocumentEnabled, (Guid?)null, Approver));
        foreach (var activity in new[] { uploaded, approved, disabled, enabled })
        {
            activity.OrganizationId.ShouldBe(KnowledgeBase.OrganizationId);
            activity.KnowledgeBaseId.ShouldBe(KnowledgeBase.Id);
            activity.DocumentId.ShouldBe(document.Id);
        }

        // Only the disable row carries anything besides ids: the owner's reason.
        foreach (var activity in new[] { uploaded, approved, enabled })
        {
            activity.Detail.ShouldBeNull();
            activity.DisableReason().ShouldBeNull();
        }

        disabled.DisableReason().ShouldBe("條款待法務確認");

        Should.Throw<ArgumentException>(() => KnowledgeActivity.VersionApproved(version, Guid.Empty, Now));
    }

    private static KnowledgeDocumentVersion NewVersion(KnowledgeDocument document, int number, char sha) =>
        KnowledgeDocumentVersion.Create(document, number, $"退貨政策-{number}.pdf", "application/pdf", 3, new string(sha, 64), Uploader, null, Now);

    private static KnowledgeDocumentVersion Processed(KnowledgeDocumentVersion version, KnowledgeDocumentStatus outcome = KnowledgeDocumentStatus.Ready)
    {
        version.StartProcessing(Now);
        version.CompleteProcessing(outcome, outcome == KnowledgeDocumentStatus.Ready ? null : "第 2 頁無法讀取", Now);
        return version;
    }
}
