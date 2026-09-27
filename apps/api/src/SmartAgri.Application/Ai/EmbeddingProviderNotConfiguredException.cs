namespace SmartAgri.Application.Ai;

/// <summary>
/// Thrown by the embedding generator of a deployment without <c>Ai:Embedding:Provider</c>: the
/// application still starts (sign-in, knowledge base management and uploads keep working), but
/// nothing can be embedded until an operator configures a provider and restarts it.
/// </summary>
public sealed class EmbeddingProviderNotConfiguredException : InvalidOperationException
{
    public EmbeddingProviderNotConfiguredException()
        : base("No embedding provider is configured: set Ai:Embedding:Provider (OpenAI, AzureOpenAI or OpenAICompatible) and Ai:Embedding:Model, then restart.")
    {
    }
}
