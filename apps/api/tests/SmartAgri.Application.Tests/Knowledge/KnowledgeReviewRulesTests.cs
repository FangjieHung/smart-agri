using Shouldly;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge;

public class KnowledgeReviewRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 8, 0, 30, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.CreateVersion7();

    [Fact]
    public void Effective_from_defaults_to_now_and_keeps_a_later_time_in_utc()
    {
        var id = Guid.NewGuid();

        ValidateOk([id.ToString()], null).ShouldBe(new KnowledgeApprovalRequest([id], Now), new ApprovalComparer());
        ValidateOk([id.ToString()], " ").EffectiveFrom.ShouldBe(Now);

        var later = ValidateOk([id.ToString()], "2026-10-01T00:00:00+08:00").EffectiveFrom;
        later.ShouldBe(new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.Zero));
        later.Offset.ShouldBe(TimeSpan.Zero, "PostgreSQL timestamptz takes UTC only");
        ValidateOk([id.ToString()], "2026-09-27T08:05:00Z").EffectiveFrom.ShouldBe(Now.AddMinutes(4).AddSeconds(30));
    }

    [Theory]
    [InlineData("2026-09-27T08:00:30Z")] // exactly now
    [InlineData("2026-09-27T07:59:30Z")] // one minute before: clock skew
    [InlineData("2026-09-27T15:59:31+08:00")] // 59 seconds before, in Taipei time
    public void A_time_up_to_a_minute_in_the_past_counts_as_now(string effectiveFrom)
    {
        ValidateOk([Guid.NewGuid().ToString()], effectiveFrom).EffectiveFrom.ShouldBe(Now);
    }

    [Theory]
    [InlineData("2026-09-27T07:59:29Z", KnowledgeReviewRules.EffectiveFromInPastMessage)]
    [InlineData("2026-09-26T08:00:30Z", KnowledgeReviewRules.EffectiveFromInPastMessage)]
    [InlineData("2026-10-01T00:00:00", KnowledgeReviewRules.EffectiveFromInvalidMessage)] // no time zone
    [InlineData("2026-10-01", KnowledgeReviewRules.EffectiveFromInvalidMessage)]
    [InlineData("明天", KnowledgeReviewRules.EffectiveFromInvalidMessage)]
    public void An_effective_time_in_the_past_or_without_a_time_zone_is_refused(string effectiveFrom, string message)
    {
        var result = KnowledgeReviewRules.ValidateApproval([Guid.NewGuid().ToString()], effectiveFrom, Now);

        result.Failures.ShouldBe([new ValidationFailure(KnowledgeReviewRules.EffectiveFromField, message)]);
    }

    [Fact]
    public void Version_ids_are_required_and_bounded_and_every_field_is_reported_at_once()
    {
        KnowledgeReviewRules.ValidateApproval(null, null, Now).Failures
            .ShouldBe([new ValidationFailure(KnowledgeReviewRules.VersionIdsField, KnowledgeReviewRules.VersionIdsRequiredMessage)]);
        KnowledgeReviewRules.ValidateApproval([], "昨天", Now).Failures.Select(failure => failure.Field)
            .ShouldBe([KnowledgeReviewRules.VersionIdsField, KnowledgeReviewRules.EffectiveFromField]);

        var tooMany = Enumerable.Range(0, KnowledgeReviewRules.MaxVersionsPerApproval + 1).Select(_ => (string?)Guid.NewGuid().ToString()).ToList();
        KnowledgeReviewRules.ValidateApproval(tooMany, null, Now).Failures
            .ShouldBe([new ValidationFailure(KnowledgeReviewRules.VersionIdsField, KnowledgeReviewRules.TooManyVersionsMessage)]);
        KnowledgeReviewRules.ValidateApproval(tooMany[1..], null, Now).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Entries_that_are_not_guids_stay_in_place_to_be_refused_as_not_found()
    {
        var id = Guid.NewGuid();

        ValidateOk(["不是 GUID", id.ToString(), null, Guid.Empty.ToString(), $" {id} "], null).VersionIds
            .ShouldBe([null, id, null, null, id]);
    }

    [Fact]
    public void Only_readable_versions_pending_review_of_this_knowledge_base_can_be_approved_and_each_refusal_names_its_position()
    {
        var ready = Version(KnowledgeDocumentStatus.Ready);
        var partial = Version(KnowledgeDocumentStatus.PartiallyReadable);
        var queued = Version(KnowledgeDocumentStatus.Queued);
        var processing = Version(KnowledgeDocumentStatus.Processing);
        var failed = Version(KnowledgeDocumentStatus.Failed);
        var approved = Version(KnowledgeDocumentStatus.Ready);
        approved.Approve(Owner, Now, Now);
        var versions = new[] { ready, partial, queued, processing, failed, approved }.ToDictionary(version => version.Id);

        KnowledgeReviewRules.ApprovalRefusals([ready.Id, partial.Id, ready.Id], versions).ShouldBeEmpty();

        KnowledgeReviewRules.ApprovalRefusals([ready.Id, queued.Id, processing.Id, failed.Id, approved.Id, Guid.NewGuid(), null], versions)
            .ShouldBe(
            [
                new ValidationFailure("versionIds[1]", KnowledgeReviewRules.VersionStillProcessingMessage),
                new ValidationFailure("versionIds[2]", KnowledgeReviewRules.VersionStillProcessingMessage),
                new ValidationFailure("versionIds[3]", KnowledgeReviewRules.VersionFailedMessage),
                new ValidationFailure("versionIds[4]", KnowledgeReviewRules.VersionAlreadyApprovedMessage),
                new ValidationFailure("versionIds[5]", KnowledgeReviewRules.VersionNotFoundMessage),
                new ValidationFailure("versionIds[6]", KnowledgeReviewRules.VersionNotFoundMessage),
            ]);
        KnowledgeReviewRules.BatchRefusedMessage(6).ShouldStartWith("有 6 個版本不能確認生效");
    }

    [Fact]
    public void A_disable_reason_is_required_trimmed_and_bounded()
    {
        KnowledgeReviewRules.ValidateDisableReason("  價格錯誤，暫停使用 \n").Value.ShouldBe("價格錯誤，暫停使用");
        KnowledgeReviewRules.ValidateDisableReason(null).Failures
            .ShouldBe([new ValidationFailure(KnowledgeReviewRules.ReasonField, KnowledgeReviewRules.ReasonRequiredMessage)]);
        KnowledgeReviewRules.ValidateDisableReason(" \t").Failures.Single().Message.ShouldBe(KnowledgeReviewRules.ReasonRequiredMessage);
        KnowledgeReviewRules.ValidateDisableReason(new string('因', KnowledgeDocument.DisabledReasonMaxLength)).IsValid.ShouldBeTrue();
        KnowledgeReviewRules.ValidateDisableReason(new string('因', KnowledgeDocument.DisabledReasonMaxLength + 1)).Failures
            .ShouldBe([new ValidationFailure(KnowledgeReviewRules.ReasonField, KnowledgeReviewRules.ReasonTooLongMessage)]);
    }

    [Fact]
    public void A_new_versions_content_may_not_repeat_any_version_in_the_knowledge_base()
    {
        var documentId = Guid.NewGuid();

        KnowledgeUploadRules.CheckNewVersionDuplicate(documentId, null).ShouldBeNull();

        var own = KnowledgeUploadRules.CheckNewVersionDuplicate(documentId, new KnowledgeExistingContent(documentId, "退貨政策.pdf", 1)).ShouldNotBeNull();
        own.Reason.ShouldBe(KnowledgeUploadRejectionReason.DuplicateContent);
        own.Message.ShouldBe("這份檔案的內容與這份文件的第 1 版完全相同，不需要再上傳一次。");
        own.ExistingDocumentName.ShouldBe("退貨政策.pdf");

        var other = KnowledgeUploadRules.CheckNewVersionDuplicate(documentId, new KnowledgeExistingContent(Guid.NewGuid(), "產品說明.docx", 2)).ShouldNotBeNull();
        other.Message.ShouldBe("這份檔案的內容與「產品說明.docx」的第 2 版完全相同，不能當作這份文件的新版本上傳。");
        other.Field.ShouldBe(KnowledgeUploadRules.FileField);
    }

    private static KnowledgeApprovalRequest ValidateOk(IReadOnlyList<string?> versionIds, string? effectiveFrom)
    {
        var result = KnowledgeReviewRules.ValidateApproval(versionIds, effectiveFrom, Now);
        result.IsValid.ShouldBeTrue(string.Join("; ", result.Failures));
        return result.Value;
    }

    private static KnowledgeDocumentVersion Version(KnowledgeDocumentStatus status)
    {
        var knowledgeBase = KnowledgeBase.Create(Guid.CreateVersion7(), Owner, "退換貨政策", string.Empty, Now);
        var document = KnowledgeDocument.CreateUploaded(knowledgeBase, "退貨政策.pdf", Now);
        var version = KnowledgeDocumentVersion.Create(document, 1, "退貨政策.pdf", "application/pdf", 3, new string('a', 64), Owner, null, Now);
        if (status == KnowledgeDocumentStatus.Queued)
        {
            return version;
        }

        version.StartProcessing(Now);
        switch (status)
        {
            case KnowledgeDocumentStatus.Failed:
                version.MarkFailed("找不到可讀文字", Now);
                break;
            case KnowledgeDocumentStatus.Ready:
                version.CompleteProcessing(status, null, Now);
                break;
            case KnowledgeDocumentStatus.PartiallyReadable:
                version.CompleteProcessing(status, "第 2 頁無法讀取", Now);
                break;
        }

        return version;
    }

    /// <summary>Records compare lists by reference; compare their contents instead.</summary>
    private sealed class ApprovalComparer : IEqualityComparer<KnowledgeApprovalRequest>
    {
        public bool Equals(KnowledgeApprovalRequest? x, KnowledgeApprovalRequest? y) =>
            x is not null && y is not null && x.VersionIds.SequenceEqual(y.VersionIds) && x.EffectiveFrom == y.EffectiveFrom;

        public int GetHashCode(KnowledgeApprovalRequest obj) => obj.EffectiveFrom.GetHashCode();
    }
}
