using System.Linq.Expressions;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>
/// <b>The</b> retrieval eligibility rule (M2 plan §3, "每個版本都要人工確認生效"; ticket #42). A
/// chunk may be retrieved — cited by an assistant, shown by the retrieval preview — only when
/// <list type="number">
/// <item>it is not excluded, <b>and</b></item>
/// <item>its version is its document's <b>current effective version</b>
/// (<see cref="CurrentEffectiveVersion"/>): approved, processed <c>ready</c> or
/// <c>partially-readable</c>, <c>EffectiveFrom ≤ now</c>, and the latest such version of the
/// document, <b>and</b></item>
/// <item>its document is not disabled, <b>and</b></item>
/// <item>its vector is from the configured embedding model.</item>
/// </list>
/// Being processed <c>ready</c> alone is never enough (the business review: "不能只因處理狀態是
/// <c>ready</c> 就自動供對外助理使用").
/// </summary>
/// <remarks>
/// <para>
/// Everything is an expression over the entities, parameterized by <c>now</c> and the model,
/// so EF Core translates it into the query itself: <c>EffectiveFrom ≤ @now</c> is part of the
/// SQL, and no scheduled job has to switch versions when an effective date arrives. The same
/// expressions evaluate in memory over entities built by their factories (which link
/// <see cref="KnowledgeChunk.Version"/>, <see cref="KnowledgeDocumentVersion.Document"/> and
/// <see cref="KnowledgeDocument.Versions"/>), which is how the unit tests run them.
/// </para>
/// <para>
/// <b>Vector search</b> (#43): the rule is a <c>VectorSearchOptions&lt;KnowledgeChunk&gt;.Filter</c>.
/// The chunk collection hands the filter to EF's <c>Where</c>, which follows the chunk's version
/// and document through their navigations (inner joins) and the document's other versions
/// (a <c>NOT EXISTS</c> subquery), next to the collection's own organization and
/// <c>EmbeddingModel = @model</c> conditions. Call it as
/// <c>collection.SearchAsync(questionVector, top, new() { Filter = RetrievableChunks.InKnowledgeBase(knowledgeBaseId, clock.GetUtcNow(), settings.Model) })</c>,
/// with <c>settings</c> the scope's <see cref="Embeddings.KnowledgeEmbeddingSettings"/> — the
/// model the collection itself searches. Search results do not load the navigations: read
/// document names and version numbers separately by the results' ids.
/// </para>
/// <para>
/// "Latest" means the latest <c>EffectiveFrom</c> not after <c>now</c>, then the higher version
/// number when two take effect at the same moment (e.g. approved in one batch). So an
/// approved version replaces the one in effect at its own effective date, whichever was
/// uploaded first; the replaced one is then "archived" (<see cref="KnowledgeVersionStates"/>).
/// </para>
/// </remarks>
public static class RetrievableChunks
{
    /// <summary>
    /// The versions in effect at <paramref name="now"/>: for each document, at most one (its
    /// current effective version). Does not look at the document being disabled — a disabled
    /// document still has a version in effect, it is just not served (see <see cref="Rule"/>).
    /// </summary>
    public static Expression<Func<KnowledgeDocumentVersion, bool>> CurrentEffectiveVersion(DateTimeOffset now) =>
        version => version.ReviewState == KnowledgeReviewState.Approved
            && version.EffectiveFrom <= now
            && (version.ProcessingStatus == KnowledgeDocumentStatus.Ready
                || version.ProcessingStatus == KnowledgeDocumentStatus.PartiallyReadable)
            && !version.Document!.Versions.Any(later =>
                later.ReviewState == KnowledgeReviewState.Approved
                && later.EffectiveFrom <= now
                && (later.ProcessingStatus == KnowledgeDocumentStatus.Ready
                    || later.ProcessingStatus == KnowledgeDocumentStatus.PartiallyReadable)
                && (later.EffectiveFrom > version.EffectiveFrom
                    || (later.EffectiveFrom == version.EffectiveFrom && later.VersionNumber > version.VersionNumber)));

    /// <summary>Every retrievable chunk of the current organization (the collection and the
    /// context add the organization) at <paramref name="now"/>, for vectors of
    /// <paramref name="embeddingModel"/>.</summary>
    public static Expression<Func<KnowledgeChunk, bool>> Rule(DateTimeOffset now, string embeddingModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);
        Expression<Func<KnowledgeChunk, bool>> chunkAndDocument = chunk =>
            !chunk.Excluded
            && chunk.EmbeddingModel == embeddingModel
            && chunk.Version!.Document!.DisabledAt == null;
        return And(chunkAndDocument, OnChunkVersion(CurrentEffectiveVersion(now)));
    }

    /// <summary><see cref="Rule"/> within one knowledge base: what the retrieval preview and,
    /// in M3, an assistant connected to that knowledge base may cite.</summary>
    public static Expression<Func<KnowledgeChunk, bool>> InKnowledgeBase(Guid knowledgeBaseId, DateTimeOffset now, string embeddingModel)
    {
        Expression<Func<KnowledgeChunk, bool>> inKnowledgeBase = chunk => chunk.KnowledgeBaseId == knowledgeBaseId;
        return And(inKnowledgeBase, Rule(now, embeddingModel));
    }

    /// <summary><paramref name="versionRule"/> applied to <c>chunk.Version</c>.</summary>
    private static Expression<Func<KnowledgeChunk, bool>> OnChunkVersion(Expression<Func<KnowledgeDocumentVersion, bool>> versionRule)
    {
        var chunk = Expression.Parameter(typeof(KnowledgeChunk), "chunk");
        var version = Expression.Property(chunk, nameof(KnowledgeChunk.Version));
        return Expression.Lambda<Func<KnowledgeChunk, bool>>(ReplaceParameter.In(versionRule, version), chunk);
    }

    private static Expression<Func<T, bool>> And<T>(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = left.Parameters[0];
        return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(left.Body, ReplaceParameter.In(right, parameter)), parameter);
    }

    /// <summary>A lambda's body with its only parameter replaced by another expression.</summary>
    private sealed class ReplaceParameter : ExpressionVisitor
    {
        private readonly ParameterExpression _parameter;
        private readonly Expression _replacement;

        private ReplaceParameter(ParameterExpression parameter, Expression replacement)
        {
            _parameter = parameter;
            _replacement = replacement;
        }

        public static Expression In(LambdaExpression lambda, Expression replacement) =>
            new ReplaceParameter(lambda.Parameters[0], replacement).Visit(lambda.Body);

        protected override Expression VisitParameter(ParameterExpression node) => node == _parameter ? _replacement : node;
    }
}
