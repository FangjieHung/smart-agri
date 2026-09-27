using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Shouldly;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge.Retrieval;

/// <summary>
/// <see cref="KnowledgeRetriever"/> over an in-memory vector collection that applies the
/// search filter to entities linked by their factories — the same expressions the real
/// collection hands to EF (<c>KnowledgeRetrievalPreviewTests</c> runs them against PostgreSQL).
/// Every chunk vector is (cos θ, sin θ) against the question's (1, 0), so its score is exactly
/// the number the test gives it.
/// </summary>
public class KnowledgeRetrieverTests
{
    private const string Model = "fake-test";
    private const string Question = "收到商品幾天內可退貨？";

    private static readonly DateTimeOffset Today = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Assistant = Guid.CreateVersion7();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Returns_the_top_retrievable_passages_of_the_given_knowledge_base_closest_first_with_what_to_cite_them_as()
    {
        var world = new World();
        var policy = world.Document(world.Main, "退貨政策.pdf");
        var effective = policy.Upload(("第 1 頁", 0.5), ("第 2 頁", 0.9), ("第 3 頁", 0.2));
        policy.Approve(effective);
        policy.Upload(("第 2 頁", 0.95));
        var elsewhere = world.Document(world.Other, "配送時間.xlsx");
        elsewhere.Approve(elsewhere.Upload(("工作表『配送時間』第 2–30 列", 0.99)));

        var result = await world.Retriever().RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner, Assistant, Top: 2), CancellationToken);

        result.Passages.Select(passage => (passage.LocationLabel, passage.VersionNumber, passage.VersionState)).ShouldBe(
        [
            ("第 2 頁", 1, KnowledgeVersionState.Effective),
            ("第 1 頁", 1, KnowledgeVersionState.Effective),
        ]);
        var top = result.Passages[0];
        top.Score.ShouldBe(0.9, 1e-6);
        (top.KnowledgeBaseId, top.DocumentId, top.DocumentName, top.VersionId).ShouldBe((world.Main.Id, policy.Entity.Id, "退貨政策.pdf", effective.Id));
        top.ChunkId.ShouldBe(world.Chunks.Single(chunk => chunk.VersionId == effective.Id && chunk.LocationLabel == "第 2 頁").Id);
        top.Text.ShouldBe("第 1 版 第 2 頁");
        result.Threshold.ShouldBe(KnowledgeRetrievalSettings.DefaultMinScore);
        result.BelowThreshold.ShouldBeFalse();
        result.Relevant.Count.ShouldBe(2);

        // One call: the question with the query prefix, attributed to the asker and the assistant.
        var call = world.Generator.Calls.ShouldHaveSingleItem();
        call.Inputs.ShouldBe(["query: " + Question]);
        call.Attribution.ShouldBe(new ModelInvocationAttribution(ModelInvocationPurpose.EmbedQuery, Owner, Assistant));
    }

    [Fact]
    public async Task Several_knowledge_bases_are_searched_together()
    {
        var world = new World();
        var policy = world.Document(world.Main, "退貨政策.pdf");
        policy.Approve(policy.Upload(("第 2 頁", 0.9)));
        var delivery = world.Document(world.Other, "配送時間.xlsx");
        delivery.Approve(delivery.Upload(("工作表『配送時間』第 2–30 列", 0.8)));

        var result = await world.Retriever().RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id, world.Other.Id], Owner), CancellationToken);

        result.Passages.Select(passage => passage.DocumentName).ShouldBe(["退貨政策.pdf", "配送時間.xlsx"]);
    }

    [Fact]
    public async Task Including_pending_adds_the_newest_pending_versions_passages_labelled_as_pending()
    {
        var world = new World();
        var policy = world.Document(world.Main, "退貨政策.pdf");
        policy.Approve(policy.Upload(("第 2 頁", 0.9)));
        var pending = policy.Upload(("第 2 頁", 0.95));

        var without = await world.Retriever().RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner), CancellationToken);
        var with = await world.Retriever().RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner, IncludePending: true), CancellationToken);

        without.Passages.Select(passage => (passage.VersionNumber, passage.VersionState)).ShouldBe([(1, KnowledgeVersionState.Effective)]);
        with.Passages.Select(passage => (passage.VersionNumber, passage.VersionState)).ShouldBe(
            [(2, KnowledgeVersionState.PendingReview), (1, KnowledgeVersionState.Effective)]);
        with.Passages[0].VersionId.ShouldBe(pending.Id);
    }

    [Fact]
    public async Task All_scores_below_the_threshold_is_below_threshold_with_the_passages_still_returned()
    {
        var world = new World();
        var policy = world.Document(world.Main, "退貨政策.pdf");
        policy.Approve(policy.Upload(("第 1 頁", 0.29), ("第 2 頁", 0.1)));

        var result = await world.Retriever().RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner), CancellationToken);

        result.BelowThreshold.ShouldBeTrue();
        result.Passages.Select(passage => passage.LocationLabel).ShouldBe(["第 1 頁", "第 2 頁"]);
        result.Relevant.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_score_equal_to_the_threshold_counts_and_a_caller_may_use_its_own_threshold()
    {
        var world = new World();
        var policy = world.Document(world.Main, "退貨政策.pdf");
        policy.Approve(policy.Upload(("第 1 頁", 0.5), ("第 2 頁", 0.25)));
        var retriever = world.Retriever(new KnowledgeRetrievalSettings(0.5, 5));

        var atThreshold = await retriever.RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner), CancellationToken);
        var stricter = await retriever.RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner, MinScore: 0.6), CancellationToken);
        var looser = await retriever.RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner, MinScore: 0.2), CancellationToken);

        (atThreshold.Threshold, atThreshold.BelowThreshold, atThreshold.Relevant.Count).ShouldBe((0.5, false, 1));
        (stricter.Threshold, stricter.BelowThreshold, stricter.Relevant.Count).ShouldBe((0.6, true, 0));
        (looser.Threshold, looser.BelowThreshold, looser.Relevant.Count).ShouldBe((0.2, false, 2));
    }

    [Fact]
    public async Task Top_defaults_to_the_settings_and_is_capped()
    {
        var world = new World();
        var policy = world.Document(world.Main, "退貨政策.pdf");
        policy.Approve(policy.Upload([.. Enumerable.Range(1, 25).Select(page => ($"第 {page} 頁", 0.9 - (page / 100.0)))]));
        var retriever = world.Retriever(new KnowledgeRetrievalSettings(0.3, 3));

        (await retriever.RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner), CancellationToken)).Passages.Count.ShouldBe(3);
        (await retriever.RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner, Top: KnowledgeRetrievalSettings.MaxTop), CancellationToken))
            .Passages.Count.ShouldBe(20);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner, Top: KnowledgeRetrievalSettings.MaxTop + 1), CancellationToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner, Top: 0), CancellationToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner, MinScore: 1.1), CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() =>
            retriever.RetrieveAsync(new KnowledgeRetrievalQuery(" ", [world.Main.Id], Owner), CancellationToken));
    }

    [Fact]
    public async Task No_knowledge_base_to_search_calls_nothing_and_finds_nothing()
    {
        var world = new World();

        var result = await world.Retriever().RetrieveAsync(new KnowledgeRetrievalQuery(Question, [], Owner), CancellationToken);

        (result.Passages.Count, result.BelowThreshold).ShouldBe((0, true));
        world.Generator.Calls.ShouldBeEmpty();
        world.Collection.Searches.ShouldBe(0);
    }

    [Fact]
    public async Task A_question_the_model_cannot_embed_throws_the_embedding_exception_and_searches_nothing()
    {
        var world = new World { Generator = { Throw = new HttpRequestException("503 Service Unavailable") } };

        var exception = await Should.ThrowAsync<KnowledgeEmbeddingException>(() =>
            world.Retriever().RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner), CancellationToken));

        exception.Message.ShouldBe(KnowledgeProcessingIssues.EmbeddingUnavailable);
        world.Collection.Searches.ShouldBe(0);
    }

    [Fact]
    public async Task A_passage_whose_version_is_gone_by_the_time_its_source_is_read_is_dropped()
    {
        var world = new World();
        var policy = world.Document(world.Main, "退貨政策.pdf");
        var gone = policy.Upload(("第 1 頁", 0.9));
        policy.Approve(gone);
        var kept = world.Document(world.Main, "換貨政策.pdf");
        kept.Approve(kept.Upload(("第 1 頁", 0.5)));
        world.Sources.Deleted.Add(gone.Id);

        var result = await world.Retriever().RetrieveAsync(new KnowledgeRetrievalQuery(Question, [world.Main.Id], Owner), CancellationToken);

        result.Passages.Select(passage => passage.DocumentName).ShouldBe(["換貨政策.pdf"]);
    }

    [Fact]
    public void Settings_accept_a_similarity_threshold_and_a_capped_top()
    {
        KnowledgeRetrievalSettings.Default.ShouldBe(new KnowledgeRetrievalSettings(0.3, 5));
        Should.Throw<ArgumentOutOfRangeException>(() => new KnowledgeRetrievalSettings(-0.1, 5));
        Should.Throw<ArgumentOutOfRangeException>(() => new KnowledgeRetrievalSettings(1.1, 5));
        Should.Throw<ArgumentOutOfRangeException>(() => new KnowledgeRetrievalSettings(double.NaN, 5));
        Should.Throw<ArgumentOutOfRangeException>(() => new KnowledgeRetrievalSettings(0.3, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new KnowledgeRetrievalSettings(0.3, KnowledgeRetrievalSettings.MaxTop + 1));
    }

    /// <summary>Two knowledge bases of one organization, their documents and chunks.</summary>
    private sealed class World
    {
        public World()
        {
            Collection = new InMemoryChunks(Chunks);
            Sources = new InMemorySources(Versions);
        }

        public KnowledgeBase Main { get; } = KnowledgeBase.Create(Organization, Owner, "退換貨政策", string.Empty, Today.AddYears(-1));

        public KnowledgeBase Other { get; } = KnowledgeBase.Create(Organization, Owner, "配送", string.Empty, Today.AddYears(-1));

        public List<KnowledgeChunk> Chunks { get; } = [];

        public List<KnowledgeDocumentVersion> Versions { get; } = [];

        public QuestionGenerator Generator { get; } = new();

        public InMemoryChunks Collection { get; }

        public InMemorySources Sources { get; }

        public TestDocument Document(KnowledgeBase knowledgeBase, string name) =>
            new(this, KnowledgeDocument.CreateUploaded(knowledgeBase, name, Today.AddYears(-1)));

        public KnowledgeRetriever Retriever(KnowledgeRetrievalSettings? settings = null) =>
            new(
                new KnowledgeChunkEmbedder(Generator, new KnowledgeEmbeddingSettings(Model, string.Empty, "query: ", KnowledgeEmbeddingSettings.DefaultBatchSize)),
                Collection,
                Sources,
                settings ?? KnowledgeRetrievalSettings.Default,
                new FixedClock(Today.AddHours(1)));
    }

    private sealed class TestDocument(World world, KnowledgeDocument entity)
    {
        public KnowledgeDocument Entity { get; } = entity;

        /// <summary>A new version processed <c>ready</c>, one chunk per (location, score), plus an
        /// excluded chunk and another model's chunk that score 1 and must never be found.</summary>
        public KnowledgeDocumentVersion Upload(params (string Location, double Score)[] chunks)
        {
            var number = Entity.Versions.Count + 1;
            var at = Today.AddYears(-1).AddMinutes(number);
            var version = KnowledgeDocumentVersion.Create(
                Entity, number, Entity.Name, "application/pdf", 3, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), Owner, null, at);
            version.StartProcessing(at);
            version.CompleteProcessing(KnowledgeDocumentStatus.Ready, null, at);
            world.Versions.Add(version);

            var ordinal = 0;
            foreach (var (location, score) in chunks)
            {
                world.Chunks.Add(Chunk(version, ordinal++, location, score, Model, excluded: false));
            }

            world.Chunks.Add(Chunk(version, ordinal++, "第 99 頁", 1, Model, excluded: true));
            world.Chunks.Add(Chunk(version, ordinal, "第 99 頁", 1, "another-model", excluded: false));
            return version;
        }

        public void Approve(KnowledgeDocumentVersion version) => version.Approve(Owner, Today, Today);

        private static KnowledgeChunk Chunk(KnowledgeDocumentVersion version, int ordinal, string location, double score, string model, bool excluded)
        {
            var chunk = KnowledgeChunk.Create(version, 0, ordinal, location, $"第 {version.VersionNumber} 版 {location}");
            chunk.SetEmbedding([(float)score, (float)Math.Sqrt(1 - (score * score))], model);
            chunk.SetExcluded(excluded);
            return chunk;
        }
    }

    private sealed record Call(string[] Inputs, ModelInvocationAttribution? Attribution);

    /// <summary>Answers every question with (1, 0).</summary>
    private sealed class QuestionGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<Call> Calls { get; } = [];

        public Exception? Throw { get; set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var inputs = values.ToArray();
            Calls.Add(new Call(inputs, ModelInvocationAttribution.From(options)));
            return Throw is not null
                ? Task.FromException<GeneratedEmbeddings<Embedding<float>>>(Throw)
                : Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(_ => new Embedding<float>(new[] { 1f, 0f }))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Exact cosine search over entities, applying the filter compiled.</summary>
    private sealed class InMemoryChunks(List<KnowledgeChunk> chunks) : VectorStoreCollection<Guid, KnowledgeChunk>
    {
        public int Searches { get; private set; }

        public override string Name => "in-memory";

        public override async IAsyncEnumerable<VectorSearchResult<KnowledgeChunk>> SearchAsync<TInput>(
            TInput searchValue,
            int top,
            VectorSearchOptions<KnowledgeChunk>? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Searches++;
            var query = searchValue as float[] ?? throw new NotSupportedException();
            var filter = options?.Filter?.Compile() ?? (_ => true);
            await Task.Yield();
            foreach (var result in chunks
                .Where(filter)
                .Select(chunk => new VectorSearchResult<KnowledgeChunk>(chunk, Cosine(query, chunk.Embedding!)))
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.Record.Id)
                .Take(top))
            {
                yield return result;
            }
        }

        public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task<KnowledgeChunk?> GetAsync(Guid key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override IAsyncEnumerable<KnowledgeChunk> GetAsync(
            Expression<Func<KnowledgeChunk, bool>> filter,
            int top,
            FilteredRecordRetrievalOptions<KnowledgeChunk>? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task DeleteAsync(Guid key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task UpsertAsync(KnowledgeChunk record, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task UpsertAsync(IEnumerable<KnowledgeChunk> records, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override object? GetService(Type serviceType, object? serviceKey = null) => null;

        private static double Cosine(float[] a, float[] b)
        {
            var dot = a.Zip(b, (x, y) => (double)x * y).Sum();
            return dot / (Math.Sqrt(a.Sum(x => (double)x * x)) * Math.Sqrt(b.Sum(y => (double)y * y)));
        }
    }

    /// <summary>Sources read from the entities, with the same state rule as the database's.</summary>
    private sealed class InMemorySources(List<KnowledgeDocumentVersion> versions) : IKnowledgeVersionSources
    {
        public HashSet<Guid> Deleted { get; } = [];

        public Task<IReadOnlyDictionary<Guid, KnowledgeVersionSource>> FindAsync(
            IReadOnlyCollection<Guid> versionIds,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var inEffect = RetrievableChunks.CurrentEffectiveVersion(now).Compile();
            IReadOnlyDictionary<Guid, KnowledgeVersionSource> found = versions
                .Where(version => versionIds.Contains(version.Id) && !Deleted.Contains(version.Id))
                .ToDictionary(
                    version => version.Id,
                    version => new KnowledgeVersionSource(
                        version.Id,
                        version.DocumentId,
                        version.Document!.Name,
                        version.VersionNumber,
                        KnowledgeVersionStates.Of(version.ReviewState, version.EffectiveFrom, inEffect(version), now)));
            return Task.FromResult(found);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
