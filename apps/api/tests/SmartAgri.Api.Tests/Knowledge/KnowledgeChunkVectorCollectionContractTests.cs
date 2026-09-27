using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Shouldly;
using SmartAgri.Api.Tests.Tenancy;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Knowledge;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// What <see cref="KnowledgeChunkVectorCollection"/> deliberately does not do (M2 plan §3: only
/// search, get, upsert and delete), and the query it sends; no database (every call here fails
/// or is translated before a connection is opened).
/// </summary>
public class KnowledgeChunkVectorCollectionContractTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Collection_management_and_filtered_retrieval_are_not_supported()
    {
        using var collection = Collection();

        await Should.ThrowAsync<NotSupportedException>(() => collection.CollectionExistsAsync(CancellationToken));
        await Should.ThrowAsync<NotSupportedException>(() => collection.EnsureCollectionExistsAsync(CancellationToken));
        await Should.ThrowAsync<NotSupportedException>(() => collection.EnsureCollectionDeletedAsync(CancellationToken));
        Should.Throw<NotSupportedException>(() => collection.GetAsync(chunk => !chunk.Excluded, 10, cancellationToken: CancellationToken));
    }

    [Fact]
    public async Task Search_takes_a_vector_of_the_configured_model_never_text()
    {
        using var collection = Collection();

        (await Should.ThrowAsync<NotSupportedException>(async () => await collection.SearchAsync("幾天內可退貨？", 5, cancellationToken: CancellationToken).ToListAsync(CancellationToken)))
            .Message.ShouldContain("IEmbeddingGenerator");
        await Should.ThrowAsync<NotSupportedException>(async () => await collection.SearchAsync(
            new[] { 1f }, 5, new VectorSearchOptions<KnowledgeChunk> { VectorProperty = chunk => chunk.Text }, CancellationToken).ToListAsync(CancellationToken));
        await Should.ThrowAsync<ArgumentException>(async () => await collection.SearchAsync(Array.Empty<float>(), 5, cancellationToken: CancellationToken).ToListAsync(CancellationToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await collection.SearchAsync(new[] { 1f }, 0, cancellationToken: CancellationToken).ToListAsync(CancellationToken));
    }

    [Fact]
    public void It_is_the_knowledge_chunks_collection_and_describes_itself_as_such()
    {
        using var collection = Collection();

        collection.Name.ShouldBe("KnowledgeChunks");
        var metadata = collection.GetService(typeof(VectorStoreCollectionMetadata)).ShouldBeOfType<VectorStoreCollectionMetadata>();
        (metadata.VectorStoreSystemName, metadata.CollectionName).ShouldBe(("postgresql", "KnowledgeChunks"));
        collection.GetService(typeof(IEmbeddingGenerator)).ShouldBeNull();
        collection.GetService(typeof(VectorStoreCollectionMetadata), "key").ShouldBeNull();
    }

    private static KnowledgeChunkVectorCollection Collection() => new(TenancyTestContexts.Create(Guid.NewGuid()), "fake-a");
}
