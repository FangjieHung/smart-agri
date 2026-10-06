using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>
/// One chat model's client as configured (<see cref="ChatModelEntryOptions"/>), without the
/// recording middleware. The one registered as a singleton is the deployment default
/// (<c>Ai:Chat</c>); <see cref="ChatModelCatalog"/> holds it and one more per entry of
/// <c>Ai:Chat:Models</c>, and the Api wraps the one an organization resolves to, per scope, in
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
    /// <c>OpenAI</c> client). <see cref="ChatModelEntryOptions.MaxOutputTokens"/> and
    /// <see cref="ChatModelEntryOptions.ReasoningEffort"/>, when set, are applied as every call's default through <c>ChatClientBuilder.ConfigureOptions</c> (the
    /// full <c>Microsoft.Extensions.AI</c> package) — the only things a caller cannot already do
    /// through <see cref="Microsoft.Extensions.AI.ChatOptions"/> itself.
    /// </summary>
    /// <remarks>Call only with options that passed <see cref="ChatModelOptions.Validate"/>: the
    /// deployment default (<see cref="ChatModelOptions"/>) or one entry of
    /// <see cref="ChatModelOptions.Models"/>, each with its own settings.</remarks>
    public static ChatClientProvider Create(ChatModelEntryOptions options) => Create(options, openAIStyleClient: null);

    /// <summary>
    /// <see cref="Create(ChatModelEntryOptions)"/>, with the OpenAI-style providers' underlying
    /// client made by <paramref name="openAIStyleClient"/> (given the client options — endpoint,
    /// timeout —, the key and the model) instead of the official OpenAI client: lets a test check
    /// what each model's calls are configured with, without a server.
    /// </summary>
    internal static ChatClientProvider Create(
        ChatModelEntryOptions options,
        Func<OpenAIClientOptions, string, string, IChatClient>? openAIStyleClient)
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
            ChatProviderKind.OpenAI => OpenAIStyle(options, model, endpoint, "openai", "openai", openAIStyleClient),
            ChatProviderKind.AzureOpenAI => OpenAIStyle(options, model, endpoint, "azure-openai", "azure.ai.openai", openAIStyleClient),
            ChatProviderKind.OpenAICompatible => OpenAIStyle(options, model, endpoint, "openai-compatible", "openai", openAIStyleClient),
            _ => throw new ArgumentOutOfRangeException(nameof(options), kind, null),
        };
    }

    public void Dispose() => Client.Dispose();

    private static ChatClientProvider OpenAIStyle(
        ChatModelEntryOptions options,
        string model,
        Uri? endpoint,
        string name,
        string telemetryName,
        Func<OpenAIClientOptions, string, string, IChatClient>? openAIStyleClient)
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
        var baseClient = openAIStyleClient?.Invoke(clientOptions, apiKey, model)
            ?? new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions).GetChatClient(model).AsIChatClient();
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
