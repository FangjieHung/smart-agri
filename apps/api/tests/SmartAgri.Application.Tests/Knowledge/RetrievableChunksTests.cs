using Shouldly;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge;

/// <summary>
/// The eligibility rule (M2 plan §3; ticket #42) evaluated in memory over entities linked by
/// their factories. The same expressions run as SQL inside the vector search in
/// <c>KnowledgeRetrievalEligibilityTests</c> (Api.Tests, real PostgreSQL); this table is the
/// fast, exhaustive version of it.
/// </summary>
public class RetrievableChunksTests
{
    private const string Model = "fake-test";

    private static readonly DateTimeOffset Today = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.CreateVersion7();

    /// <summary>Each case: what happens to a document's versions, then at which moments which
    /// version's chunks are retrievable (<c>0</c>: none).</summary>
    private static readonly Dictionary<string, (Action<Document> Arrange, (DateTimeOffset At, int Version)[] Expected)> Cases = new()
    {
        ["an unapproved version 1 is not retrievable"] = (
            document => document.Upload(),
            [(Today, 0), (Today.AddYears(1), 0)]),
        ["an approved version 1 is"] = (
            document => document.Approve(document.Upload(), Today),
            [(Today, 1)]),
        ["a pending version 2 leaves version 1 in effect"] = (
            document =>
            {
                document.Approve(document.Upload(), Today);
                document.Upload();
            },
            [(Today, 1), (Today.AddYears(1), 1)]),
        ["an approved version 2 archives version 1"] = (
            document =>
            {
                document.Approve(document.Upload(), Today);
                document.Approve(document.Upload(), Today.AddMinutes(1));
            },
            [(Today, 1), (Today.AddMinutes(1), 2), (Today.AddYears(1), 2)]),
        ["version 2 effective tomorrow leaves version 1 in effect today"] = (
            document =>
            {
                document.Approve(document.Upload(), Today);
                document.Approve(document.Upload(), Today.AddDays(1));
            },
            [(Today.AddHours(23), 1), (Today.AddDays(1).AddTicks(-1), 1), (Today.AddDays(1), 2), (Today.AddDays(2), 2)]),
        ["only a scheduled version: nothing before its date"] = (
            document => document.Approve(document.Upload(), Today.AddDays(1)),
            [(Today, 0), (Today.AddDays(1), 1)]),
        ["the same effective moment: the higher version wins"] = (
            document =>
            {
                var first = document.Upload();
                var second = document.Upload();
                document.Approve(second, Today);
                document.Approve(first, Today);
            },
            [(Today, 2)]),
        ["approving an older version later puts it back in effect from its own date"] = (
            document =>
            {
                var first = document.Upload();
                document.Approve(document.Upload(), Today);
                document.Approve(first, Today.AddDays(1));
            },
            [(Today, 2), (Today.AddDays(1), 1)]),
        ["a failed version 2 cannot be approved and leaves version 1 in effect"] = (
            document =>
            {
                document.Approve(document.Upload(), Today);
                document.Upload(KnowledgeDocumentStatus.Failed);
            },
            [(Today, 1)]),
        ["a partially readable version is served like a ready one"] = (
            document => document.Approve(document.Upload(KnowledgeDocumentStatus.PartiallyReadable), Today),
            [(Today, 1)]),
        ["a disabled document serves nothing"] = (
            document =>
            {
                document.Approve(document.Upload(), Today);
                document.Entity.Disable(Owner, "價格錯誤", Today);
            },
            [(Today, 0), (Today.AddYears(1), 0)]),
        ["enabling returns to the state before disabling, including approvals made meanwhile"] = (
            document =>
            {
                document.Approve(document.Upload(), Today);
                document.Entity.Disable(Owner, "價格錯誤", Today);
                document.Approve(document.Upload(), Today.AddDays(1));
                document.Entity.Enable();
            },
            [(Today, 1), (Today.AddDays(1), 2)]),
    };

    public static TheoryData<string> CaseNames => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Only_the_documents_current_effective_version_is_retrievable_and_never_its_excluded_or_other_model_chunks(string name)
    {
        var (arrange, expected) = Cases[name];
        var document = new Document();
        arrange(document);

        foreach (var (at, version) in expected)
        {
            var retrievable = document.Chunks.Where(RetrievableChunks.Rule(at, Model).Compile()).ToList();

            // Every version has one included chunk of the configured model, one excluded chunk
            // and one of another model: only the first ever passes.
            retrievable.Select(chunk => chunk.Version!.VersionNumber).ShouldBe(version == 0 ? [] : [version], $"{name} at {at:O}");
            retrievable.ShouldAllBe(chunk => !chunk.Excluded && chunk.EmbeddingModel == Model);

            // For an enabled document, the chunk rule and the version rule agree (a disabled
            // one still has a version in effect; it is just not served).
            if (document.Entity.DisabledAt is null)
            {
                document.Entity.Versions.Where(RetrievableChunks.CurrentEffectiveVersion(at).Compile())
                    .Select(current => current.VersionNumber)
                    .ShouldBe(version == 0 ? [] : [version], $"{name} at {at:O}");
            }
        }
    }

    [Fact]
    public void In_a_knowledge_base_keeps_to_that_knowledge_base()
    {
        var document = new Document();
        document.Approve(document.Upload(), Today);
        var knowledgeBaseId = document.Entity.KnowledgeBaseId;

        document.Chunks.Count(RetrievableChunks.InKnowledgeBase(knowledgeBaseId, Today, Model).Compile()).ShouldBe(1);
        document.Chunks.Count(RetrievableChunks.InKnowledgeBase(Guid.NewGuid(), Today, Model).Compile()).ShouldBe(0);
        document.Chunks.Count(RetrievableChunks.Rule(Today, "another-model").Compile()).ShouldBe(1, "the other-model chunk, in effect for that model");
        Should.Throw<ArgumentException>(() => RetrievableChunks.Rule(Today, " "));
    }

    [Fact]
    public void Version_states_are_pending_scheduled_effective_or_archived()
    {
        KnowledgeVersionStates.Of(KnowledgeReviewState.PendingReview, null, isCurrentEffective: false, Today).ShouldBe(KnowledgeVersionState.PendingReview);
        KnowledgeVersionStates.Of(KnowledgeReviewState.Approved, Today, isCurrentEffective: true, Today).ShouldBe(KnowledgeVersionState.Effective);
        KnowledgeVersionStates.Of(KnowledgeReviewState.Approved, Today.AddTicks(1), isCurrentEffective: false, Today).ShouldBe(KnowledgeVersionState.Scheduled);
        KnowledgeVersionStates.Of(KnowledgeReviewState.Approved, Today, isCurrentEffective: false, Today).ShouldBe(KnowledgeVersionState.Archived);
    }

    /// <summary>A document whose versions are processed and chunked as they are uploaded.</summary>
    private sealed class Document
    {
        private static readonly KnowledgeBase KnowledgeBase =
            KnowledgeBase.Create(Guid.CreateVersion7(), Owner, "退換貨政策", string.Empty, Today.AddYears(-1));

        public KnowledgeDocument Entity { get; } = KnowledgeDocument.CreateUploaded(KnowledgeBase, "退貨政策.pdf", Today.AddYears(-1));

        public List<KnowledgeChunk> Chunks { get; } = [];

        public KnowledgeDocumentVersion Upload(KnowledgeDocumentStatus outcome = KnowledgeDocumentStatus.Ready)
        {
            var number = Entity.Versions.Count + 1;
            var uploadedAt = Today.AddYears(-1).AddMinutes(number);
            var version = KnowledgeDocumentVersion.Create(
                Entity, number, $"退貨政策-{number}.pdf", "application/pdf", 3, new string((char)('a' + number), 64), Owner, null, uploadedAt);
            version.StartProcessing(uploadedAt);
            if (outcome == KnowledgeDocumentStatus.Failed)
            {
                version.MarkFailed("找不到可讀文字", uploadedAt);
                return version;
            }

            version.CompleteProcessing(outcome, outcome == KnowledgeDocumentStatus.Ready ? null : "第 2 頁無法讀取", uploadedAt);
            Chunks.Add(Chunk(version, 0, Model, excluded: false));
            Chunks.Add(Chunk(version, 1, Model, excluded: true));
            Chunks.Add(Chunk(version, 2, "another-model", excluded: false));
            return version;
        }

        /// <summary>Approved today, effective from <paramref name="effectiveFrom"/>.</summary>
        public void Approve(KnowledgeDocumentVersion version, DateTimeOffset effectiveFrom) =>
            version.Approve(Owner, effectiveFrom, Today);

        private static KnowledgeChunk Chunk(KnowledgeDocumentVersion version, int ordinal, string model, bool excluded)
        {
            var chunk = KnowledgeChunk.Create(version, 0, ordinal, "第 1 頁", $"第 {version.VersionNumber} 版的段落 {ordinal}");
            chunk.SetEmbedding([1f, ordinal], model);
            chunk.SetExcluded(excluded);
            return chunk;
        }
    }
}
