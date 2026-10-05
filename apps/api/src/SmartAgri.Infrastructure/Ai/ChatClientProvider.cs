using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>
/// The deployment's chat client as configured (<see cref="ChatModelOptions"/>), without the
/// recording middleware: a singleton the Api wraps per scope in
/// <see cref="ModelInvocationRecordingChatClient"/>. Never call <see cref="Client"/>
/// directly — every model call must be recorded. The same shape as <see cref="EmbeddingProvider"/>.
/// </summary>
public sealed class ChatClientProvider : IDisposable
{
    public ChatClientProvider(IChatClient client, string name, string telemetryName, string model, Uri? endpoint)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(telemetryName);
        ArgumentNullException.ThrowIfNull(model);
        Client = client;
        Name = name;
        TelemetryName = telemetryName;
        Model = model;
        Endpoint = endpoint;
    }

    /// <summary>The provider's client, unwrapped.</summary>
    public IChatClient Client { get; }

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
    public bool IsConfigured => Client is not UnconfiguredChatClient;

    /// <summary>
    /// Builds the client <paramref name="options"/> describe (the same three OpenAI-style
    /// providers as <see cref="EmbeddingProvider.Create"/>, through the same official
    /// <c>OpenAI</c> client). <see cref="ChatModelOptions.MaxOutputTokens"/> and
    /// <see cref="ChatModelOptions.ReasoningEffort"/>, when set, are applied as every call's default through <c>ChatClientBuilder.ConfigureOptions</c> (the
    /// full <c>Microsoft.Extensions.AI</c> package) — the only things a caller cannot already do
    /// through <see cref="Microsoft.Extensions.AI.ChatOptions"/> itself.
    /// </summary>
    /// <remarks>Call only with options that passed <see cref="ChatModelOptions.Validate"/>.</remarks>
    public static ChatClientProvider Create(ChatModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ProviderKind is not { } kind)
        {
            return new ChatClientProvider(new UnconfiguredChatClient(), "none", "none", string.Empty, endpoint: null);
        }

        var model = options.Model!.Trim();
        var endpoint = options.EndpointUri;
        return kind switch
        {
            ChatProviderKind.Fake => new ChatClientProvider(new FakeChatClient(model), "fake", "smartagri.fake", model, endpoint: null),
            ChatProviderKind.OpenAI => OpenAIStyle(options, model, endpoint, "openai", "openai"),
            ChatProviderKind.AzureOpenAI => OpenAIStyle(options, model, endpoint, "azure-openai", "azure.ai.openai"),
            ChatProviderKind.OpenAICompatible => OpenAIStyle(options, model, endpoint, "openai-compatible", "openai"),
            _ => throw new ArgumentOutOfRangeException(nameof(options), kind, null),
        };
    }

    public void Dispose() => Client.Dispose();

    private static ChatClientProvider OpenAIStyle(ChatModelOptions options, string model, Uri? endpoint, string name, string telemetryName)
    {
        var clientOptions = new OpenAIClientOptions();
        if (endpoint is not null)
        {
            clientOptions.Endpoint = endpoint;
        }

        if (options.TimeoutSeconds is { } timeoutSeconds)
        {
            clientOptions.NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? EmbeddingProvider.PlaceholderApiKey : options.ApiKey.Trim();
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        var baseClient = client.GetChatClient(model).AsIChatClient();
        var maxOutputTokens = options.MaxOutputTokens;
        var reasoningEffort = options.ReasoningEffortKind;
        var chatClient = maxOutputTokens is null && reasoningEffort is null
            ? baseClient
            : new ChatClientBuilder(baseClient).ConfigureOptions(callOptions =>
            {
                callOptions.MaxOutputTokens ??= maxOutputTokens;
                if (reasoningEffort is { } effort)
                {
                    callOptions.Reasoning ??= new ReasoningOptions { Effort = effort };
                }
            }).Build();
        return new ChatClientProvider(chatClient, name, telemetryName, model, endpoint ?? new Uri("https://api.openai.com/v1"));
    }
}
