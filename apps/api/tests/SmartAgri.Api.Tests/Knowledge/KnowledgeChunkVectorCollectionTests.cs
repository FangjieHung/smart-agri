using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Knowledge.Extraction;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Knowledge;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// <see cref="KnowledgeChunkVectorCollection"/> against real PostgreSQL and pgvector (M2 plan,
/// Slice 7; ticket #41): search only sees the configured model's vectors and the current
/// organization's chunks, <c>reindex</c> brings chunks back after a model change, and get,
/// upsert and delete keep to the organization. Retrieval proper (approved versions, the
/// question's embedding) is Slice 9's; this is the storage level under it.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class KnowledgeChunkVectorCollectionTests : IClassFixture<AuthHostFixture>
{
    private const string OtherModel = "fake-other";

    private readonly AuthHostFixture _host;

    public KnowledgeChunkVectorCollectionTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Acceptance: another model name finds none of the old vectors, until reindex --------

    [Fact]
    public async Task After_a_model_change_search_finds_no_old_vector_until_reindex_re_embeds_every_chunk()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await owner.UploadAsync(KnowledgeFixtures.ProductGuideDocx);
        await RunJobsAsync();
        var chunks = await ChunksAsync(owner, versionId);
        chunks.Count.ShouldBe(7);
        chunks.ShouldAllBe(chunk => chunk.EmbeddingModel == AuthHostFixture.EmbeddingModel);
        var target = chunks.Single(chunk => chunk.LocationLabel == "2 退換貨 › 2.2 運費");
        var targetText = KnowledgeEmbeddingText.For(KnowledgeUnitLocationKind.Section, target.LocationLabel, target.Text);

        // An excluded chunk is re-embedded too, and stays excluded.
        await SetExcludedAsync(owner, chunks[0].Id);

        // The configured model finds the chunk by its own text.
        (await SearchAsync(owner, AuthHostFixture.EmbeddingModel, targetText)).First().Record.Id.ShouldBe(target.Id);

        // Switched to another model: none of the old vectors is compared, not even the closest.
        (await SearchAsync(owner, OtherModel, targetText)).ShouldBeEmpty();

        await using var switched = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Ai:Embedding:Model", OtherModel));
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await ReindexCommand.RunAsync(switched.Services, ["--organization", owner.Organization.Code, "--batch-size", "3"], output, error, CancellationToken);

        exit.ShouldBe(ReindexCommand.ExitSuccess, error.ToString());
        output.ToString().ShouldContain($"組織 {owner.Organization.Code}（{owner.Organization.Name}）：7 個段落需要重新嵌入。");
        output.ToString().ShouldContain("  3/7");
        output.ToString().ShouldContain("  7/7");
        output.ToString().ShouldContain("完成：共重新嵌入 7 個段落。");

        var reindexed = await ChunksAsync(owner, versionId);
        reindexed.ShouldAllBe(chunk => chunk.EmbeddingModel == OtherModel);
        reindexed.Select(chunk => chunk.Id).ShouldBe(chunks.Select(chunk => chunk.Id), "the same chunks, only their vectors changed");
        reindexed[0].Excluded.ShouldBeTrue();
        reindexed.Single(chunk => chunk.Id == target.Id).Embedding.ShouldBe(new FakeEmbeddingGenerator(OtherModel).Embed(targetText));

        var found = await SearchAsync(owner, OtherModel, targetText);
        found.First().Record.Id.ShouldBe(target.Id);
        found.First().Score!.Value.ShouldBe(1, 1e-5);
        (await SearchAsync(owner, AuthHostFixture.EmbeddingModel, targetText)).ShouldBeEmpty();

        // Three calls (3 + 3 + 1), recorded for the organization with no account.
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var calls = await dbContext.ModelInvocations.AsNoTracking().Where(invocation => invocation.Model == OtherModel).ToListAsync(CancellationToken);
            calls.Count.ShouldBe(3);
            calls.ShouldAllBe(call => call.AccountId == null && call.Purpose == ModelInvocationPurpose.EmbedDocument && call.Succeeded);
        }

        // Nothing left to do: running it again changes nothing and calls nothing.
        var again = new StringWriter();
        (await ReindexCommand.RunAsync(switched.Services, [$"--organization={owner.Organization.Code}"], again, TextWriter.Null, CancellationToken))
            .ShouldBe(ReindexCommand.ExitSuccess);
        again.ToString().ShouldContain("：0 個段落需要重新嵌入。");
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            (await dbContext.ModelInvocations.CountAsync(invocation => invocation.Model == OtherModel, CancellationToken)).ShouldBe(3);
        }
    }

    [Fact]
    public async Task Reindex_embeds_chunks_that_have_no_vector_yet_and_refuses_an_unknown_organization()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await owner.UploadAsync(KnowledgeFixtures.ReturnPolicyPdf);
        await RunJobsAsync();

        // As if processed before embeddings existed (the migration adds empty columns).
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            await dbContext.KnowledgeChunks.Where(chunk => chunk.VersionId == versionId)
                .ExecuteUpdateAsync(set => set.SetProperty(chunk => chunk.Embedding, (float[]?)null).SetProperty(chunk => chunk.EmbeddingModel, (string?)null), CancellationToken);
        }

        (await ReindexCommand.RunAsync(_host.Factory.Services, ["--organization", owner.Organization.Code], TextWriter.Null, TextWriter.Null, CancellationToken))
            .ShouldBe(ReindexCommand.ExitSuccess);
        (await ChunksAsync(owner, versionId)).ShouldAllBe(chunk => chunk.Embedding != null && chunk.EmbeddingModel == AuthHostFixture.EmbeddingModel);

        var error = new StringWriter();
        (await ReindexCommand.RunAsync(_host.Factory.Services, ["--organization", "no-such-org"], TextWriter.Null, error, CancellationToken))
            .ShouldBe(ReindexCommand.ExitFailed);
        error.ToString().ShouldContain("找不到組織代碼「no-such-org」");
    }

    // --- Acceptance: organization A never sees organization B's chunks -------------------

    [Fact]
    public async Task Search_get_and_delete_never_reach_another_organizations_chunks_with_or_without_a_filter()
    {
        var ownerA = await KnowledgeTestOwner.CreateAsync(_host, "組織 A");
        var ownerB = await KnowledgeTestOwner.CreateAsync(_host, "組織 B");
        var versionA = await ownerA.UploadAsync(KnowledgeFixtures.ReturnPolicyPdf);
        var versionB = await ownerB.UploadAsync(KnowledgeFixtures.ReturnPolicyPdf);
        await RunJobsAsync();
        var chunksA = await ChunksAsync(ownerA, versionA);
        var chunksB = await ChunksAsync(ownerB, versionB);
        chunksB.Select(chunk => chunk.Embedding).ShouldBe(chunksA.Select(chunk => chunk.Embedding), "the same file: B's chunks would score exactly like A's");
        var query = chunksA[1].Embedding!;

        await using var dbContext = _host.Postgres.CreateDbContext(ownerA.Organization.Id);
        using var collection = new KnowledgeChunkVectorCollection(dbContext, AuthHostFixture.EmbeddingModel);

        var unfiltered = await collection.SearchAsync(query, 100, cancellationToken: CancellationToken).ToListAsync(CancellationToken);
        unfiltered.Select(result => result.Record.Id).ShouldBe(chunksA.Select(chunk => chunk.Id), ignoreOrder: true);
        unfiltered.ShouldAllBe(result => result.Record.OrganizationId == ownerA.Organization.Id);

        var knowledgeBaseB = ownerB.KnowledgeBaseId;
        var organizationB = ownerB.Organization.Id;
        foreach (var filter in new System.Linq.Expressions.Expression<Func<KnowledgeChunk, bool>>[]
        {
            chunk => chunk.KnowledgeBaseId == knowledgeBaseB,
            chunk => chunk.OrganizationId == organizationB,
            chunk => chunk.VersionId == versionB,
        })
        {
            (await collection.SearchAsync(query, 100, new VectorSearchOptions<KnowledgeChunk> { Filter = filter }, CancellationToken).ToListAsync(CancellationToken))
                .ShouldBeEmpty(filter.ToString());
        }

        var knowledgeBaseA = ownerA.KnowledgeBaseId;
        (await collection.SearchAsync(query, 100, new VectorSearchOptions<KnowledgeChunk> { Filter = chunk => chunk.KnowledgeBaseId == knowledgeBaseA }, CancellationToken)
            .ToListAsync(CancellationToken)).Count.ShouldBe(chunksA.Count);

        (await collection.GetAsync(chunksB[0].Id, cancellationToken: CancellationToken)).ShouldBeNull();
        (await collection.GetAsync([chunksB[0].Id, chunksA[0].Id], cancellationToken: CancellationToken).ToListAsync(CancellationToken))
            .Select(chunk => chunk.Id).ShouldBe([chunksA[0].Id]);
        await collection.DeleteAsync([chunksB[0].Id, chunksB[1].Id], CancellationToken);
        (await ChunksAsync(ownerB, versionB)).Count.ShouldBe(chunksB.Count, "another organization's chunks cannot be deleted");
    }

    // --- Search options, get, upsert and delete ------------------------------------------

    [Fact]
    public async Task Search_is_exact_cosine_ordered_with_similarity_scores_skip_and_a_minimum_score()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await owner.UploadAsync(KnowledgeFixtures.ProductGuideDocx);
        await RunJobsAsync();
        var chunks = await ChunksAsync(owner, versionId);
        var query = chunks[3].Embedding!;
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        using var collection = new KnowledgeChunkVectorCollection(dbContext, AuthHostFixture.EmbeddingModel);

        var all = await collection.SearchAsync(query, 100, cancellationToken: CancellationToken).ToListAsync(CancellationToken);

        all.Count.ShouldBe(chunks.Count);
        all[0].Record.Id.ShouldBe(chunks[3].Id);
        all[0].Score!.Value.ShouldBe(1, 1e-5);
        var expected = chunks
            .Select(chunk => (chunk.Id, Score: chunk.Embedding!.Zip(query, (a, b) => (double)a * b).Sum()))
            .OrderByDescending(pair => pair.Score)
            .ToList();
        all.Select(result => result.Record.Id).ShouldBe(expected.Select(pair => pair.Id), "exact search: every chunk, by cosine");
        all.Select(result => result.Score!.Value).ShouldBe(expected.Select(pair => pair.Score), tolerance: 1e-5);
        all.ShouldAllBe(result => result.Record.Embedding != null && result.Record.Text.Length > 0);

        (await collection.SearchAsync(query, 2, new VectorSearchOptions<KnowledgeChunk> { Skip = 1 }, CancellationToken).ToListAsync(CancellationToken))
            .Select(result => result.Record.Id).ShouldBe(all.Skip(1).Take(2).Select(result => result.Record.Id));
        var threshold = all[2].Score!.Value - 1e-6;
        (await collection.SearchAsync(query, 100, new VectorSearchOptions<KnowledgeChunk> { ScoreThreshold = threshold }, CancellationToken).ToListAsync(CancellationToken))
            .Count.ShouldBe(3);
        (await collection.SearchAsync((ReadOnlyMemory<float>)query, 1, new VectorSearchOptions<KnowledgeChunk> { VectorProperty = chunk => chunk.Embedding }, CancellationToken)
            .ToListAsync(CancellationToken)).Single().Record.Id.ShouldBe(chunks[3].Id);
        (await collection.SearchAsync(new Microsoft.Extensions.AI.Embedding<float>(query), 1, cancellationToken: CancellationToken)
            .ToListAsync(CancellationToken)).Single().Record.Id.ShouldBe(chunks[3].Id);
    }

    [Fact]
    public async Task Upsert_writes_only_what_changed_on_a_tracked_chunk_replaces_an_untracked_one_and_delete_removes_it()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await owner.UploadAsync(KnowledgeFixtures.ReturnPolicyPdf);
        await RunJobsAsync();
        var chunkId = (await ChunksAsync(owner, versionId))[0].Id;

        // Read (tracked) for re-embedding; meanwhile the owner excludes it.
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        using var collection = new KnowledgeChunkVectorCollection(dbContext, AuthHostFixture.EmbeddingModel);
        var tracked = await dbContext.KnowledgeChunks.SingleAsync(chunk => chunk.Id == chunkId, CancellationToken);
        await SetExcludedAsync(owner, chunkId);
        tracked.SetEmbedding([0.6f, 0.8f], "model-x");
        await collection.UpsertAsync(tracked, CancellationToken);

        var stored = (await collection.GetAsync(chunkId, cancellationToken: CancellationToken)).ShouldNotBeNull();
        stored.Embedding.ShouldBe([0.6f, 0.8f]);
        (stored.EmbeddingModel, stored.Excluded).ShouldBe(("model-x", true), "the exclusion made meanwhile survives");

        // An untracked record replaces the row it names.
        dbContext.ChangeTracker.Clear();
        var detached = (await collection.GetAsync(chunkId, cancellationToken: CancellationToken)).ShouldNotBeNull();
        detached.SetExcluded(false);
        detached.SetEmbedding([1f, 0f, 0f], "model-y");
        await collection.UpsertAsync([detached], CancellationToken);
        dbContext.ChangeTracker.Clear();
        var replaced = (await collection.GetAsync(chunkId, cancellationToken: CancellationToken)).ShouldNotBeNull();
        replaced.Embedding.ShouldBe([1f, 0f, 0f]);
        (replaced.EmbeddingModel, replaced.Excluded).ShouldBe(("model-y", false));

        await collection.DeleteAsync(chunkId, CancellationToken);
        (await collection.GetAsync(chunkId, cancellationToken: CancellationToken)).ShouldBeNull();
        await collection.DeleteAsync(chunkId, CancellationToken);
    }

    [Fact]
    public async Task The_scopes_collection_searches_the_configured_model()
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();

        var collection = scope.ServiceProvider.GetRequiredService<VectorStoreCollection<Guid, KnowledgeChunk>>();

        collection.ShouldBeOfType<KnowledgeChunkVectorCollection>().EmbeddingModel.ShouldBe(AuthHostFixture.EmbeddingModel);
    }

    // --- Helpers -------------------------------------------------------------------------

    private Task RunJobsAsync() => _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    private async Task<List<KnowledgeChunk>> ChunksAsync(KnowledgeTestOwner owner, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeChunks.AsNoTracking()
            .Where(chunk => chunk.VersionId == versionId)
            .OrderBy(chunk => chunk.UnitOrdinal).ThenBy(chunk => chunk.Ordinal)
            .ToListAsync(CancellationToken);
    }

    private async Task SetExcludedAsync(KnowledgeTestOwner owner, Guid chunkId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.KnowledgeChunks.SingleAsync(chunk => chunk.Id == chunkId, CancellationToken)).SetExcluded(true);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    /// <summary>Searches <paramref name="owner"/>'s organization as <paramref name="model"/>
    /// would: the query is that model's (Fake) vector of <paramref name="text"/>.</summary>
    private async Task<List<VectorSearchResult<KnowledgeChunk>>> SearchAsync(KnowledgeTestOwner owner, string model, string text)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        using var collection = new KnowledgeChunkVectorCollection(dbContext, model);
        return await collection.SearchAsync(new FakeEmbeddingGenerator(model).Embed(text), 5, cancellationToken: CancellationToken).ToListAsync(CancellationToken);
    }
}
