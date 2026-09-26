using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Infrastructure.Knowledge;

/// <summary>
/// <c>Microsoft.Extensions.VectorData</c>'s <see cref="VectorStoreCollection{TKey,TRecord}"/> over
/// <c>KnowledgeChunks</c>, on EF Core and Pgvector.EntityFrameworkCore instead of the preview
/// pgvector connector (postgresql-as-single-store ADR; M2 plan §3). The record type is the EF
/// entity <see cref="KnowledgeChunk"/> itself, so a <see cref="VectorSearchOptions{TRecord}.Filter"/>
/// is an ordinary EF <c>Where</c>, and the organization filter applies to every read, filter or
/// not. Scoped with the <see cref="AppDbContext"/> it uses: writes join that context's
/// transaction, if one is open.
/// </summary>
/// <remarks>
/// <para>
/// Implements what M2 and M3 use — <see cref="SearchAsync{TInput}"/>, <see cref="UpsertAsync(KnowledgeChunk, CancellationToken)"/>,
/// <see cref="DeleteAsync(Guid, CancellationToken)"/>, <see cref="GetAsync(Guid, RecordRetrievalOptions?, CancellationToken)"/>
/// and their batch forms. Collection management (the table is the migrations' business) and
/// filtered retrieval without a vector throw <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// Search is exact (no approximate index, plan §3): only vectors of the configured model are
/// compared — any other model's vectors, even of the same dimension, are meaningless next to
/// the query's — ordered by cosine distance. <see cref="VectorSearchResult{TRecord}.Score"/> is
/// the cosine similarity, 1 − distance: higher is closer.
/// </para>
/// <para>
/// Records always carry their vector: it is a column of the row, so
/// <see cref="VectorSearchOptions{TRecord}.IncludeVectors"/> and
/// <see cref="RecordRetrievalOptions.IncludeVectors"/> being false saves nothing and is ignored.
/// </para>
/// </remarks>
public sealed class KnowledgeChunkVectorCollection : VectorStoreCollection<Guid, KnowledgeChunk>
{
    public const string CollectionName = "KnowledgeChunks";

    private readonly AppDbContext _dbContext;
    private readonly string _embeddingModel;

    public KnowledgeChunkVectorCollection(AppDbContext dbContext, string embeddingModel)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(embeddingModel);
        _dbContext = dbContext;
        _embeddingModel = embeddingModel;
    }

    public override string Name => CollectionName;

    /// <summary>The model whose vectors <see cref="SearchAsync{TInput}"/> compares.</summary>
    public string EmbeddingModel => _embeddingModel;

    /// <summary>
    /// The <paramref name="top"/> chunks of the current organization closest to
    /// <paramref name="searchValue"/> — a vector (<see cref="ReadOnlyMemory{T}"/> of
    /// <see cref="float"/>, <c>float[]</c> or <see cref="Embedding{T}"/>) from the configured
    /// model — among those embedded by that model and matching the filter.
    /// </summary>
    /// <remarks>Text is not accepted: embed it with the scope's <c>IEmbeddingGenerator</c>
    /// first, so the call is attributed and recorded like every other.</remarks>
    public override async IAsyncEnumerable<VectorSearchResult<KnowledgeChunk>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        VectorSearchOptions<KnowledgeChunk>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        var query = new Vector(QueryVector(searchValue));
        options ??= new VectorSearchOptions<KnowledgeChunk>();
        ArgumentOutOfRangeException.ThrowIfNegative(options.Skip);
        if (options.VectorProperty is { } vectorProperty && !IsEmbeddingProperty(vectorProperty))
        {
            throw new NotSupportedException($"{nameof(KnowledgeChunk)}.{nameof(KnowledgeChunk.Embedding)} is the only vector property.");
        }

        // Parameters, never constants: Pgvector.EntityFrameworkCore 0.3.0 on EF Core 10 renders
        // a constant vector as an unquoted literal, which PostgreSQL rejects.
        var model = _embeddingModel;
        var chunks = _dbContext.KnowledgeChunks
            .AsNoTracking()
            .Where(chunk => chunk.EmbeddingModel == model && chunk.Embedding != null);
        if (options.Filter is { } filter)
        {
            chunks = chunks.Where(filter);
        }

        var scored = chunks.Select(chunk => new
        {
            Chunk = chunk,
            Distance = EF.Property<Vector>(chunk, nameof(KnowledgeChunk.Embedding)).CosineDistance(query),
        });
        if (options.ScoreThreshold is { } minimumSimilarity)
        {
            var maximumDistance = 1 - minimumSimilarity;
            scored = scored.Where(result => result.Distance <= maximumDistance);
        }

        var results = await scored
            .OrderBy(result => result.Distance)
            .ThenBy(result => result.Chunk.Id)
            .Skip(options.Skip)
            .Take(top)
            .ToListAsync(cancellationToken);

        foreach (var result in results)
        {
            yield return new VectorSearchResult<KnowledgeChunk>(result.Chunk, 1 - result.Distance);
        }
    }

    /// <summary>The chunk with <paramref name="key"/> in the current organization, or
    /// <see langword="null"/>.</summary>
    public override async Task<KnowledgeChunk?> GetAsync(Guid key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default) =>
        await _dbContext.KnowledgeChunks.AsNoTracking().SingleOrDefaultAsync(chunk => chunk.Id == key, cancellationToken);

    /// <summary>The chunks with these keys in the current organization; missing ones are skipped.</summary>
    public override async IAsyncEnumerable<KnowledgeChunk> GetAsync(
        IEnumerable<Guid> keys,
        RecordRetrievalOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var ids = keys.Distinct().ToList();
        var chunks = await _dbContext.KnowledgeChunks.AsNoTracking()
            .Where(chunk => ids.Contains(chunk.Id))
            .OrderBy(chunk => chunk.Id)
            .ToListAsync(cancellationToken);
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }

    /// <summary>Saves <paramref name="record"/>: see <see cref="UpsertAsync(IEnumerable{KnowledgeChunk}, CancellationToken)"/>.</summary>
    public override Task UpsertAsync(KnowledgeChunk record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return UpsertAsync([record], cancellationToken);
    }

    /// <summary>
    /// Saves the records in one <c>SaveChanges</c>. A record this collection's context already
    /// tracks (read through it, then e.g. <see cref="KnowledgeChunk.SetEmbedding"/>) writes only
    /// what changed — <c>reindex</c> relies on that, so an exclusion the owner changes meanwhile
    /// is not overwritten. Any other record replaces the stored row with its id, or is inserted
    /// when there is none (its version and unit must exist). Only the chunk row itself is
    /// written, never an entity reachable from it (<see cref="KnowledgeChunk.Version"/>). The
    /// write guard refuses records of another organization.
    /// </summary>
    public override async Task UpsertAsync(IEnumerable<KnowledgeChunk> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = records.ToList();
        var untracked = list.Where(record => _dbContext.Entry(record).State == EntityState.Detached).ToList();
        if (untracked.Count > 0)
        {
            var ids = untracked.Select(record => record.Id).ToList();
            var existing = (await _dbContext.KnowledgeChunks.AsNoTracking()
                .Where(chunk => ids.Contains(chunk.Id))
                .Select(chunk => chunk.Id)
                .ToListAsync(cancellationToken)).ToHashSet();
            foreach (var record in untracked)
            {
                // The entry's state, not DbSet.Update/Add: those would also start tracking (and
                // write) the version a record created in memory links to.
                _dbContext.Entry(record).State = existing.Contains(record.Id) ? EntityState.Modified : EntityState.Added;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Deletes the chunk with <paramref name="key"/> in the current organization, if any.</summary>
    public override Task DeleteAsync(Guid key, CancellationToken cancellationToken = default) =>
        DeleteAsync([key], cancellationToken);

    /// <summary>Deletes the chunks with these keys in the current organization in one statement
    /// (the organization filter is part of it); missing ones are ignored.</summary>
    public override async Task DeleteAsync(IEnumerable<Guid> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var ids = keys.Distinct().ToList();
        await _dbContext.KnowledgeChunks.Where(chunk => ids.Contains(chunk.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Not supported: the table always exists once migrations ran.</summary>
    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) =>
        throw NotSupported(nameof(CollectionExistsAsync));

    /// <summary>Not supported: migrations create the table.</summary>
    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) =>
        throw NotSupported(nameof(EnsureCollectionExistsAsync));

    /// <summary>Not supported: chunks are deleted with their versions, never as a table.</summary>
    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default) =>
        throw NotSupported(nameof(EnsureCollectionDeletedAsync));

    /// <summary>Not supported: nothing needs filtered retrieval without a vector yet; query
    /// <c>AppDbContext.KnowledgeChunks</c> for maintenance instead.</summary>
    public override IAsyncEnumerable<KnowledgeChunk> GetAsync(
        Expression<Func<KnowledgeChunk, bool>> filter,
        int top,
        FilteredRecordRetrievalOptions<KnowledgeChunk>? options = null,
        CancellationToken cancellationToken = default) =>
        throw NotSupported("GetAsync(filter, top)");

    /// <summary>The collection's <see cref="VectorStoreCollectionMetadata"/> when asked for it;
    /// otherwise <see langword="null"/>, as the abstraction requires.</summary>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(VectorStoreCollectionMetadata)
            ? new VectorStoreCollectionMetadata { VectorStoreSystemName = "postgresql", CollectionName = CollectionName }
            : null;
    }

    private static ReadOnlyMemory<float> QueryVector<TInput>(TInput searchValue)
    {
        var vector = searchValue switch
        {
            ReadOnlyMemory<float> memory => memory,
            Memory<float> memory => memory,
            float[] array => array,
            Embedding<float> embedding => embedding.Vector,
            null => throw new ArgumentNullException(nameof(searchValue)),
            _ => throw new NotSupportedException(
                $"Search with a vector ((ReadOnly)Memory<float>, float[] or Embedding<float>), not {typeof(TInput).Name}: " +
                "embed text with the scope's IEmbeddingGenerator first, so the call is recorded."),
        };
        return vector.IsEmpty ? throw new ArgumentException("The search vector is empty.", nameof(searchValue)) : vector;
    }

    private static bool IsEmbeddingProperty(Expression<Func<KnowledgeChunk, object?>> property)
    {
        var body = property.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : property.Body;
        return body is MemberExpression { Member.Name: nameof(KnowledgeChunk.Embedding) } member && member.Expression == property.Parameters[0];
    }

    private static NotSupportedException NotSupported(string member) =>
        new($"{nameof(KnowledgeChunkVectorCollection)}.{member} is not supported: only search, get, upsert and delete are implemented (M2 plan §3).");
}
