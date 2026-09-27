using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Domain.Tests;

public class KnowledgeExtractionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.CreateVersion7();

    [Fact]
    public void Processing_ends_ready_without_an_issue_or_partially_readable_or_failed_with_one()
    {
        var ready = Processing();
        ready.CompleteProcessing(KnowledgeDocumentStatus.Ready, null, Now.AddMinutes(1));
        ready.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Ready);
        ready.Issue.ShouldBeNull();
        ready.UpdatedAt.ShouldBe(Now.AddMinutes(1));

        var partial = Processing();
        partial.CompleteProcessing(KnowledgeDocumentStatus.PartiallyReadable, "第 2 頁找不到可讀文字", Now);
        partial.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.PartiallyReadable);
        partial.Issue.ShouldBe("第 2 頁找不到可讀文字");

        var failed = Processing();
        failed.CompleteProcessing(KnowledgeDocumentStatus.Failed, "找不到可讀文字", Now);
        failed.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Failed);

        Should.Throw<ArgumentException>(() => Processing().CompleteProcessing(KnowledgeDocumentStatus.Ready, "問題", Now));
        Should.Throw<ArgumentException>(() => Processing().CompleteProcessing(KnowledgeDocumentStatus.PartiallyReadable, null, Now));
        Should.Throw<ArgumentException>(() => Processing().CompleteProcessing(KnowledgeDocumentStatus.Queued, null, Now));
        Should.Throw<ArgumentException>(() => Processing().CompleteProcessing(KnowledgeDocumentStatus.Failed, new string('長', KnowledgeDocumentVersion.IssueMaxLength + 1), Now));
    }

    [Fact]
    public void Only_a_processing_version_completes()
    {
        var queued = Queued();
        Should.Throw<InvalidOperationException>(() => queued.CompleteProcessing(KnowledgeDocumentStatus.Ready, null, Now));

        var ready = Processing();
        ready.CompleteProcessing(KnowledgeDocumentStatus.Ready, null, Now);
        Should.Throw<InvalidOperationException>(() => ready.CompleteProcessing(KnowledgeDocumentStatus.Ready, null, Now));
    }

    [Fact]
    public void A_unit_belongs_to_its_version_and_is_unreadable_exactly_when_its_issue_says_so()
    {
        var version = Processing();

        var unit = KnowledgeExtractedUnit.Create(version, 1, KnowledgeUnitLocationKind.Page, "第 2 頁", string.Empty, false, KnowledgeUnitIssue.TooLittleText);

        unit.VersionId.ShouldBe(version.Id);
        unit.OrganizationId.ShouldBe(version.OrganizationId);
        unit.Ordinal.ShouldBe(1);
        unit.Readable.ShouldBeFalse();
        unit.IssueCode.ShouldBe(KnowledgeUnitIssue.TooLittleText);

        Should.NotThrow(() => KnowledgeExtractedUnit.Create(version, 0, KnowledgeUnitLocationKind.Sheet, "工作表『價格』", "品項", true, KnowledgeUnitIssue.RowsTruncated));
        Should.NotThrow(() => KnowledgeExtractedUnit.Create(version, 0, KnowledgeUnitLocationKind.Page, "第 1 頁", "文字", true, null));
        Should.Throw<ArgumentException>(() => KnowledgeExtractedUnit.Create(version, 0, KnowledgeUnitLocationKind.Page, "第 1 頁", "文字", false, null));
        Should.Throw<ArgumentException>(() => KnowledgeExtractedUnit.Create(version, 0, KnowledgeUnitLocationKind.Page, "第 1 頁", "文字", true, KnowledgeUnitIssue.GarbledText));
        Should.Throw<ArgumentException>(() => KnowledgeExtractedUnit.Create(version, 0, KnowledgeUnitLocationKind.Page, "", "文字", true, null));
        Should.Throw<ArgumentException>(() => KnowledgeExtractedUnit.Create(
            version, 0, KnowledgeUnitLocationKind.Page, new string('頁', KnowledgeExtractedUnit.LocationLabelMaxLength + 1), "文字", true, null));
    }

    [Fact]
    public void A_chunk_repeats_its_versions_ids_starts_included_and_reports_whether_exclusion_changed()
    {
        var version = Processing();

        var chunk = KnowledgeChunk.Create(version, 2, 0, "第 3 頁", "退款將於五個工作天內退回。");

        chunk.Id.ShouldNotBe(Guid.Empty);
        (chunk.OrganizationId, chunk.KnowledgeBaseId, chunk.DocumentId, chunk.VersionId)
            .ShouldBe((version.OrganizationId, version.KnowledgeBaseId, version.DocumentId, version.Id));
        (chunk.UnitOrdinal, chunk.Ordinal).ShouldBe((2, 0));
        chunk.Excluded.ShouldBeFalse();

        chunk.SetExcluded(true).ShouldBeTrue();
        chunk.SetExcluded(true).ShouldBeFalse();
        chunk.Excluded.ShouldBeTrue();
        chunk.SetExcluded(false).ShouldBeTrue();

        Should.Throw<ArgumentException>(() => KnowledgeChunk.Create(version, 0, 0, "第 1 頁", " "));
    }

    [Fact]
    public void A_chunk_starts_without_a_vector_and_takes_one_with_the_model_that_made_it()
    {
        var chunk = KnowledgeChunk.Create(Processing(), 0, 0, "第 1 頁", "退款將於五個工作天內退回。");
        (chunk.Embedding, chunk.EmbeddingModel).ShouldBe((null, null));

        chunk.SetEmbedding([0.6f, 0.8f], "text-embedding-3-small");
        chunk.Embedding.ShouldBe([0.6f, 0.8f]);
        chunk.EmbeddingModel.ShouldBe("text-embedding-3-small");

        chunk.SetEmbedding([1f, 0f, 0f], "multilingual-e5-large");
        (chunk.Embedding!.Length, chunk.EmbeddingModel).ShouldBe((3, "multilingual-e5-large"));

        Should.Throw<ArgumentException>(() => chunk.SetEmbedding([], "m"));
        Should.Throw<ArgumentException>(() => chunk.SetEmbedding([0f, 0f], "m"));
        Should.Throw<ArgumentException>(() => chunk.SetEmbedding([float.NaN, 1f], "m"));
        Should.Throw<ArgumentException>(() => chunk.SetEmbedding([float.PositiveInfinity, 1f], "m"));
        Should.Throw<ArgumentException>(() => chunk.SetEmbedding([1f], " "));
        Should.Throw<ArgumentException>(() => chunk.SetEmbedding([1f], new string('m', KnowledgeChunk.EmbeddingModelMaxLength + 1)));
        chunk.EmbeddingModel.ShouldBe("multilingual-e5-large", "a refused vector changes nothing");
    }

    [Fact]
    public void An_exclusion_change_is_logged_with_the_chunk_id_only()
    {
        var chunk = KnowledgeChunk.Create(Processing(), 0, 0, "第 1 頁", "封面：安心商行退換貨政策");
        chunk.SetExcluded(true);

        var excluded = KnowledgeActivity.ChunkExclusionChanged(chunk, Owner, Now);

        excluded.Action.ShouldBe(KnowledgeActivityAction.ChunkExcluded);
        (excluded.KnowledgeBaseId, excluded.DocumentId, excluded.VersionId).ShouldBe((chunk.KnowledgeBaseId, (Guid?)chunk.DocumentId, (Guid?)chunk.VersionId));
        excluded.ActorAccountId.ShouldBe(Owner);
        excluded.Detail.ShouldNotBeNull().ShouldNotContain("封面");
        JsonDocument.Parse(excluded.Detail).RootElement.GetProperty("chunkId").GetGuid().ShouldBe(chunk.Id);

        chunk.SetExcluded(false);
        KnowledgeActivity.ChunkExclusionChanged(chunk, Owner, Now).Action.ShouldBe(KnowledgeActivityAction.ChunkIncluded);
    }

    private static KnowledgeDocumentVersion Queued()
    {
        var knowledgeBase = KnowledgeBase.Create(Guid.CreateVersion7(), Owner, "退換貨政策", string.Empty, Now);
        var document = KnowledgeDocument.CreateUploaded(knowledgeBase, "退貨政策.pdf", Now);
        return KnowledgeDocumentVersion.Create(document, 1, "退貨政策.pdf", "application/pdf", 3, new string('a', 64), Owner, null, Now);
    }

    private static KnowledgeDocumentVersion Processing()
    {
        var version = Queued();
        version.StartProcessing(Now);
        return version;
    }
}
