using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// One retrievable passage of a <see cref="KnowledgeDocumentVersion"/> (table
/// <c>KnowledgeChunks</c>, M2 plan §4): a piece of one readable
/// <see cref="KnowledgeExtractedUnit"/>, never more than one. The owner can exclude it
/// (a cover page, an appendix, outdated terms) so it is never retrieved.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DocumentId"/> and <see cref="KnowledgeBaseId"/> repeat the version's so
/// retrieval can filter without joins; the foreign key to the version includes both (and
/// <c>OrganizationId</c>), so the database never lets them disagree. Deleted with its version
/// and with its unit (database cascades).
/// </para>
/// <para>
/// Written by version processing, together with the units. The vector lives in this same row
/// (<see cref="Embedding"/>, a <c>vector</c> column without a fixed dimension) with the model
/// that produced it (<see cref="EmbeddingModel"/>), so a chunk and its vector are written and
/// deleted in one transaction (postgresql-as-single-store ADR) and a model change needs no
/// migration, only <c>reindex</c> (M2 plan §3).
/// </para>
/// <para>
/// Every chunk of a processed version has a vector, excluded ones too: excluding is the
/// owner's retrieval choice, and including a chunk again must not need a model call.
/// Retrieval filters on <see cref="Excluded"/>, on the current model and on its version being
/// the document's approved version in effect (the Application layer's <c>RetrievableChunks</c>).
/// </para>
/// </remarks>
public sealed class KnowledgeChunk : IOrganizationScoped
{
    /// <summary>Longer than any hosted model or deployment name (Azure allows 64 characters,
    /// Hugging Face ids are "owner/name").</summary>
    public const int EmbeddingModelMaxLength = 200;

    /// <summary>For EF Core materialization.</summary>
    private KnowledgeChunk()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public Guid DocumentId { get; private set; }

    public Guid VersionId { get; private set; }

    /// <summary>The <see cref="KnowledgeExtractedUnit.Ordinal"/> of the unit it was cut from.</summary>
    public int UnitOrdinal { get; private set; }

    /// <summary>0, 1, 2, … within its unit.</summary>
    public int Ordinal { get; private set; }

    /// <summary>Where the passage is, for citations: usually its unit's label; for a
    /// worksheet, the rows it covers (「工作表『配送時間』第 2–30 列」).</summary>
    public string LocationLabel { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    /// <summary>Set by the owner: an excluded chunk is never retrieved.</summary>
    public bool Excluded { get; private set; }

    /// <summary>The chunk's vector from <see cref="EmbeddingModel"/>; <see langword="null"/>
    /// only for chunks written before embeddings existed, until <c>reindex</c> embeds them.</summary>
    public float[]? Embedding { get; private set; }

    /// <summary>The configured embedding model (<c>Ai:Embedding:Model</c>) that produced
    /// <see cref="Embedding"/>. Search only compares vectors of the current model.</summary>
    public string? EmbeddingModel { get; private set; }

    /// <summary>
    /// The chunk's version, for query expressions: retrieval eligibility depends on the
    /// version's approval and its document's state (<c>RetrievableChunks</c>), which EF Core
    /// translates through this navigation into joins inside a vector search filter. Not loaded
    /// from the database unless a query includes it; set in memory by <see cref="Create"/>.
    /// </summary>
    public KnowledgeDocumentVersion? Version { get; private set; }

    public static KnowledgeChunk Create(
        KnowledgeDocumentVersion version,
        int unitOrdinal,
        int ordinal,
        string locationLabel,
        string text)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentOutOfRangeException.ThrowIfNegative(unitOrdinal);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        KnowledgeExtractedUnit.RequireLocationLabel(locationLabel);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A chunk needs text.", nameof(text));
        }

        return new KnowledgeChunk
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = version.OrganizationId,
            KnowledgeBaseId = version.KnowledgeBaseId,
            DocumentId = version.DocumentId,
            VersionId = version.Id,
            UnitOrdinal = unitOrdinal,
            Ordinal = ordinal,
            LocationLabel = locationLabel,
            Text = text,
            Excluded = false,
            Version = version,
        };
    }

    /// <summary>Sets the chunk's vector and the model that produced it, replacing any earlier
    /// one. The vector must be non-empty, finite and not all zeros (its cosine distance to
    /// anything would be undefined).</summary>
    public void SetEmbedding(float[] embedding, string model)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > EmbeddingModelMaxLength)
        {
            throw new ArgumentException($"An embedding model name must be at most {EmbeddingModelMaxLength} characters.", nameof(model));
        }

        if (embedding.Length == 0 || !embedding.All(float.IsFinite) || embedding.All(value => value == 0))
        {
            throw new ArgumentException("An embedding must be non-empty, finite and not all zeros.", nameof(embedding));
        }

        Embedding = embedding;
        EmbeddingModel = model;
    }

    /// <summary>Excludes or includes the chunk; false when it already was.</summary>
    public bool SetExcluded(bool excluded)
    {
        if (Excluded == excluded)
        {
            return false;
        }

        Excluded = excluded;
        return true;
    }
}
