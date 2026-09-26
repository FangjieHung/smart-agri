namespace SmartAgri.Application.Knowledge.Embeddings;

/// <summary>
/// The deployment's embedding settings the knowledge pipeline needs (from <c>Ai:Embedding</c>,
/// M2 plan §3; the Api builds this from its options).
/// </summary>
/// <param name="Model">The configured model; every stored vector is labelled with it and
/// search compares only vectors of it. Empty when no provider is configured.</param>
/// <param name="DocumentPrefix">Put before every chunk text that is embedded, e.g.
/// <c>"passage: "</c> for the e5 family; empty for models that need none (OpenAI).</param>
/// <param name="QueryPrefix">Put before every question that is embedded, e.g. <c>"query: "</c>.</param>
/// <param name="BatchSize">The most chunk texts sent in one call; every call is one
/// <c>ModelInvocation</c> row.</param>
public sealed record KnowledgeEmbeddingSettings(string Model, string DocumentPrefix, string QueryPrefix, int BatchSize)
{
    public const int DefaultBatchSize = 64;

    /// <summary>OpenAI's limit on inputs per embeddings request.</summary>
    public const int MaxBatchSize = 2048;

    public string Model { get; } = Model ?? throw new ArgumentNullException(nameof(Model));

    public string DocumentPrefix { get; } = DocumentPrefix ?? throw new ArgumentNullException(nameof(DocumentPrefix));

    public string QueryPrefix { get; } = QueryPrefix ?? throw new ArgumentNullException(nameof(QueryPrefix));

    public int BatchSize { get; } = BatchSize is >= 1 and <= MaxBatchSize
        ? BatchSize
        : throw new ArgumentOutOfRangeException(nameof(BatchSize), BatchSize, $"A batch holds 1-{MaxBatchSize} texts.");
}
