using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>
/// The deployment's embedding client as configured (<see cref="EmbeddingOptions"/>), without the
/// recording middleware: a singleton the Api wraps per scope in
/// <see cref="ModelInvocationRecordingEmbeddingGenerator"/>. Never call <see cref="Generator"/>
/// directly — every model call must be recorded.
/// </summary>
public sealed class EmbeddingProvider : IDisposable
{
    /// <summary>OpenAI's client refuses an empty key; OpenAI-compatible servers that need none
    /// (vLLM without <c>--api-key</c>) ignore the header this sends.</summary>
    internal const string PlaceholderApiKey = "no-key";

    public EmbeddingProvider(IEmbeddingGenerator<string, Embedding<float>> generator, string name, string telemetryName, string model, Uri? endpoint)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(telemetryName);
        ArgumentNullException.ThrowIfNull(model);
        Generator = generator;
        Name = name;
        TelemetryName = telemetryName;
        Model = model;
        Endpoint = endpoint;
    }

    /// <summary>The provider's client, unwrapped.</summary>
    public IEmbeddingGenerator<string, Embedding<float>> Generator { get; }

    /// <summary>Stored in <c>ModelInvocation.Provider</c>: <c>openai</c>, <c>azure-openai</c>,
    /// <c>openai-compatible</c>, <c>fake</c>; <c>none</c> when unconfigured.</summary>
    public string Name { get; }

    /// <summary>The span's <c>gen_ai.provider.name</c> (OpenTelemetry GenAI semantic conventions).</summary>
    public string TelemetryName { get; }

    /// <summary>The configured model; empty when unconfigured.</summary>
    public string Model { get; }

    /// <summary>Where requests go, when configured (the span's <c>server.address</c>).</summary>
    public Uri? Endpoint { get; }

    /// <summary>Whether calls can succeed at all (a provider is configured).</summary>
    public bool IsConfigured => Generator is not UnconfiguredEmbeddingGenerator;

    /// <summary>
    /// Builds the client <paramref name="options"/> describe. The three OpenAI-style providers
    /// all use the official <c>OpenAI</c> client through <c>Microsoft.Extensions.AI.OpenAI</c>;
    /// Azure OpenAI's v1 endpoint accepts that client with an API key as is, so
    /// <c>Azure.AI.OpenAI</c> (whose stable release is older) is not needed.
    /// </summary>
    /// <remarks>Call only with options that passed <see cref="EmbeddingOptions.Validate"/>.</remarks>
    public static EmbeddingProvider Create(EmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ProviderKind is not { } kind)
        {
            return new EmbeddingProvider(new UnconfiguredEmbeddingGenerator(), "none", "none", string.Empty, endpoint: null);
        }

        var model = options.Model!.Trim();
        var endpoint = options.EndpointUri;
        return kind switch
        {
            EmbeddingProviderKind.Fake => new EmbeddingProvider(new FakeEmbeddingGenerator(model), "fake", "smartagri.fake", model, endpoint: null),
            EmbeddingProviderKind.OpenAI => OpenAIStyle(options, model, endpoint, "openai", "openai"),
            EmbeddingProviderKind.AzureOpenAI => OpenAIStyle(options, model, endpoint, "azure-openai", "azure.ai.openai"),
            EmbeddingProviderKind.OpenAICompatible => OpenAIStyle(options, model, endpoint, "openai-compatible", "openai"),
            _ => throw new ArgumentOutOfRangeException(nameof(options), kind, null),
        };
    }

    public void Dispose() => Generator.Dispose();

    private static EmbeddingProvider OpenAIStyle(EmbeddingOptions options, string model, Uri? endpoint, string name, string telemetryName)
    {
        var clientOptions = new OpenAIClientOptions();
        if (endpoint is not null)
        {
            clientOptions.Endpoint = endpoint;
        }

        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? PlaceholderApiKey : options.ApiKey.Trim();
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        var generator = client.GetEmbeddingClient(model).AsIEmbeddingGenerator();
        return new EmbeddingProvider(generator, name, telemetryName, model, endpoint ?? new Uri("https://api.openai.com/v1"));
    }
}
