using SmartAgri.Application.Knowledge.Processing;

namespace SmartAgri.Application.Knowledge.Embeddings;

/// <summary>
/// The embedding model could not embed something: <see cref="KnowledgeChunkEmbedder"/> throws
/// it for any failure of the call (the provider's error is the inner exception) and for an
/// answer it cannot use. Its message is what the owner is shown once processing gives up
/// (<see cref="KnowledgeProcessingIssues.EmbeddingUnavailable"/>, or
/// <see cref="KnowledgeProcessingIssues.EmbeddingNotConfigured"/> when no provider is set), so
/// it can be the job's last error as it is. Retryable: the job queue tries again with backoff.
/// </summary>
public sealed class KnowledgeEmbeddingException : Exception
{
    public KnowledgeEmbeddingException(bool providerNotConfigured, Exception innerException)
        : base(providerNotConfigured ? KnowledgeProcessingIssues.EmbeddingNotConfigured : KnowledgeProcessingIssues.EmbeddingUnavailable, innerException)
    {
        ProviderNotConfigured = providerNotConfigured;
    }

    /// <summary>Whether the deployment has no embedding provider at all.</summary>
    public bool ProviderNotConfigured { get; }
}
