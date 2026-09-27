using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>Which embedding provider a deployment uses (<c>Ai:Embedding:Provider</c>).</summary>
public enum EmbeddingProviderKind
{
    /// <summary>api.openai.com, or any OpenAI endpoint set in <c>Endpoint</c>.</summary>
    OpenAI,

    /// <summary>Azure OpenAI's v1 endpoint (<c>https://{resource}.openai.azure.com/openai/v1/</c>)
    /// through the same official OpenAI client; <c>Model</c> is the deployment name.</summary>
    AzureOpenAI,

    /// <summary>A self-hosted server with an OpenAI-compatible API (vLLM, text-embeddings-inference,
    /// …), the "all on premises" path of the llm-providers ADR.</summary>
    OpenAICompatible,

    /// <summary>Deterministic hash vectors, no network: Development and Testing only.</summary>
    Fake,
}

/// <summary>
/// Configuration section <c>Ai:Embedding</c> (M2 plan §3; apps/api/README.md, "Embeddings"): one
/// embedding model per deployment. Secrets come from the environment
/// (<c>Ai__Embedding__ApiKey</c>); encrypting them is M5 (secrets-storage ADR).
/// </summary>
/// <remarks>
/// Leaving <see cref="Provider"/> unset is allowed — the Api still starts, and document
/// processing fails with a clear issue instead — so a deployment can be installed before it has
/// a model. Anything set but unusable fails startup (<see cref="Validate"/>).
/// </remarks>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Ai:Embedding";

    /// <summary>The environments where <see cref="EmbeddingProviderKind.Fake"/> may run.</summary>
    public static readonly IReadOnlyList<string> FakeEnvironments = ["Development", "Testing"];

    /// <summary><c>OpenAI</c>, <c>AzureOpenAI</c>, <c>OpenAICompatible</c> or <c>Fake</c>
    /// (case-insensitive); empty when the deployment has no embedding model yet.</summary>
    public string? Provider { get; set; }

    /// <summary>The API base address: required for Azure OpenAI and OpenAI-compatible servers
    /// (e.g. <c>http://vllm:8000/v1</c>), optional for OpenAI.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The model (Azure: deployment) name. Every vector is stored with it; changing it
    /// makes existing vectors invisible to search until <c>reindex</c> re-embeds them.</summary>
    public string? Model { get; set; }

    /// <summary>Required for OpenAI and Azure OpenAI; optional for OpenAI-compatible servers.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Put before every question, e.g. <c>"query: "</c> for the e5 family.</summary>
    public string QueryPrefix { get; set; } = string.Empty;

    /// <summary>Put before every chunk, e.g. <c>"passage: "</c> for the e5 family.</summary>
    public string DocumentPrefix { get; set; } = string.Empty;

    /// <summary>Chunks per model call.</summary>
    public int BatchSize { get; set; } = KnowledgeEmbeddingSettings.DefaultBatchSize;

    /// <summary>The parsed <see cref="Provider"/>; <see langword="null"/> when unset (or not a
    /// known name, which <see cref="Validate"/> refuses).</summary>
    public EmbeddingProviderKind? ProviderKind =>
        Enum.GetValues<EmbeddingProviderKind>()
            .Where(kind => string.Equals(kind.ToString(), Provider?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(kind => (EmbeddingProviderKind?)kind)
            .SingleOrDefault();

    /// <summary>The knowledge pipeline's view of these options.</summary>
    public KnowledgeEmbeddingSettings ToSettings() =>
        new(ProviderKind is null ? string.Empty : Model!.Trim(), DocumentPrefix, QueryPrefix, BatchSize);

    /// <summary>Why these options cannot be used in <paramref name="environmentName"/>, or
    /// <see langword="null"/>.</summary>
    public string? Validate(string environmentName)
    {
        ArgumentNullException.ThrowIfNull(environmentName);

        if (BatchSize is < 1 or > KnowledgeEmbeddingSettings.MaxBatchSize)
        {
            return $"{SectionName}:{nameof(BatchSize)} must be 1-{KnowledgeEmbeddingSettings.MaxBatchSize}.";
        }

        if (QueryPrefix is null || DocumentPrefix is null)
        {
            return $"{SectionName}:{nameof(QueryPrefix)} and {nameof(DocumentPrefix)} may be empty but not null.";
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            return null;
        }

        if (ProviderKind is not { } kind)
        {
            return $"{SectionName}:{nameof(Provider)} must be OpenAI, AzureOpenAI, OpenAICompatible or Fake, not '{Provider}'.";
        }

        if (kind == EmbeddingProviderKind.Fake && !FakeEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase))
        {
            return $"{SectionName}:{nameof(Provider)}=Fake is only allowed in the Development and Testing environments, " +
                $"not in '{environmentName}': its vectors are hashes of the text, not its meaning, so retrieval would be useless. " +
                "Configure OpenAI, AzureOpenAI or OpenAICompatible.";
        }

        if (string.IsNullOrWhiteSpace(Model) || Model.Trim().Length > KnowledgeChunk.EmbeddingModelMaxLength)
        {
            return $"{SectionName}:{nameof(Model)} is required with a provider (1-{KnowledgeChunk.EmbeddingModelMaxLength} characters; for Azure OpenAI, the deployment name).";
        }

        var endpointRequired = kind is EmbeddingProviderKind.AzureOpenAI or EmbeddingProviderKind.OpenAICompatible;
        if (endpointRequired && string.IsNullOrWhiteSpace(Endpoint))
        {
            return kind == EmbeddingProviderKind.AzureOpenAI
                ? $"{SectionName}:{nameof(Endpoint)} is required for AzureOpenAI: the resource's v1 endpoint, https://{{resource}}.openai.azure.com/openai/v1/."
                : $"{SectionName}:{nameof(Endpoint)} is required for OpenAICompatible: the server's OpenAI-style base address, e.g. http://vllm:8000/v1.";
        }

        if (kind != EmbeddingProviderKind.Fake && !string.IsNullOrWhiteSpace(Endpoint) && EndpointUri is null)
        {
            return $"{SectionName}:{nameof(Endpoint)} must be an absolute http or https address.";
        }

        return kind is EmbeddingProviderKind.OpenAI or EmbeddingProviderKind.AzureOpenAI && string.IsNullOrWhiteSpace(ApiKey)
            ? $"{SectionName}:{nameof(ApiKey)} is required for {kind} (set it as the environment variable Ai__Embedding__ApiKey)."
            : null;
    }

    /// <summary><see cref="Endpoint"/> as an absolute http(s) URI, or <see langword="null"/>.</summary>
    internal Uri? EndpointUri =>
        Uri.TryCreate(Endpoint?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
}
