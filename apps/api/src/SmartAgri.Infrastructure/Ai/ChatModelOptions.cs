namespace SmartAgri.Infrastructure.Ai;

/// <summary>Which chat (generation) provider a deployment uses (<c>Ai:Chat:Provider</c>).</summary>
public enum ChatProviderKind
{
    /// <summary>api.openai.com, or any OpenAI endpoint set in <c>Endpoint</c>.</summary>
    OpenAI,

    /// <summary>Azure OpenAI's v1 endpoint (<c>https://{resource}.openai.azure.com/openai/v1/</c>)
    /// through the same official OpenAI client; <c>Model</c> is the deployment name.</summary>
    AzureOpenAI,

    /// <summary>A self-hosted server with an OpenAI-compatible API (vLLM, …), the "all on
    /// premises" path of the llm-providers ADR.</summary>
    OpenAICompatible,

    /// <summary>A scripted, reproducible answer generator, no network: Development and Testing
    /// only (<see cref="ChatModelOptions.FakeEnvironments"/>).</summary>
    Fake,
}

/// <summary>
/// Configuration section <c>Ai:Chat</c> (M3 plan §3: "對話模型的設定與稽核照抄嵌入的模式";
/// apps/api/README.md, "Chat model"): one generation model per deployment, the same shape as
/// <see cref="EmbeddingOptions"/>. Secrets come from the environment
/// (<c>Ai__Chat__ApiKey</c>); encrypting them is M5 (secrets-storage ADR).
/// </summary>
/// <remarks>
/// Leaving <see cref="Provider"/> unset is allowed — the Api still starts — but every call
/// throws <c>ChatGenerationException(providerNotConfigured: true)</c> before reaching any
/// provider, so a deployment can be installed before it has a chat model. Anything set but
/// unusable fails startup (<see cref="Validate"/>), and <see cref="ChatProviderKind.Fake"/>
/// outside Development/Testing (most importantly, Production) always fails startup: a fake
/// answer must never reach a real user.
/// </remarks>
public sealed class ChatModelOptions
{
    public const string SectionName = "Ai:Chat";

    /// <summary>The environments where <see cref="ChatProviderKind.Fake"/> may run.</summary>
    public static readonly IReadOnlyList<string> FakeEnvironments = ["Development", "Testing"];

    /// <summary><c>OpenAI</c>, <c>AzureOpenAI</c>, <c>OpenAICompatible</c> or <c>Fake</c>
    /// (case-insensitive); empty when the deployment has no chat model yet.</summary>
    public string? Provider { get; set; }

    /// <summary>The API base address: required for Azure OpenAI and OpenAI-compatible servers,
    /// optional for OpenAI.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The model (Azure: deployment) name.</summary>
    public string? Model { get; set; }

    /// <summary>Required for OpenAI and Azure OpenAI; optional for OpenAI-compatible servers.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The deployment's default cap on generated tokens, applied to every call that does
    /// not set its own (<see cref="Microsoft.Extensions.AI.ChatOptions.MaxOutputTokens"/>); a
    /// runaway answer must not be unbounded. <see langword="null"/> leaves it to the provider's
    /// own default.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>How long a single call may take before it is treated as failed; <see langword="null"/>
    /// leaves it to the client's own default.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>The parsed <see cref="Provider"/>; <see langword="null"/> when unset (or not a
    /// known name, which <see cref="Validate"/> refuses).</summary>
    public ChatProviderKind? ProviderKind =>
        Enum.GetValues<ChatProviderKind>()
            .Where(kind => string.Equals(kind.ToString(), Provider?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(kind => (ChatProviderKind?)kind)
            .SingleOrDefault();

    /// <summary>Why these options cannot be used in <paramref name="environmentName"/>, or
    /// <see langword="null"/>.</summary>
    public string? Validate(string environmentName)
    {
        ArgumentNullException.ThrowIfNull(environmentName);

        if (MaxOutputTokens is <= 0)
        {
            return $"{SectionName}:{nameof(MaxOutputTokens)} must be a positive number of tokens, or unset.";
        }

        if (TimeoutSeconds is <= 0)
        {
            return $"{SectionName}:{nameof(TimeoutSeconds)} must be a positive number of seconds, or unset.";
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            return null;
        }

        if (ProviderKind is not { } kind)
        {
            return $"{SectionName}:{nameof(Provider)} must be OpenAI, AzureOpenAI, OpenAICompatible or Fake, not '{Provider}'.";
        }

        if (kind == ChatProviderKind.Fake && !FakeEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase))
        {
            return $"{SectionName}:{nameof(Provider)}=Fake is only allowed in the Development and Testing environments, " +
                $"not in '{environmentName}': its answers are scripted, not generated. Configure OpenAI, AzureOpenAI or OpenAICompatible.";
        }

        if (string.IsNullOrWhiteSpace(Model) || Model.Trim().Length > Domain.Ai.ModelInvocation.ModelMaxLength)
        {
            return $"{SectionName}:{nameof(Model)} is required with a provider (1-{Domain.Ai.ModelInvocation.ModelMaxLength} characters; for Azure OpenAI, the deployment name).";
        }

        var endpointRequired = kind is ChatProviderKind.AzureOpenAI or ChatProviderKind.OpenAICompatible;
        if (endpointRequired && string.IsNullOrWhiteSpace(Endpoint))
        {
            return kind == ChatProviderKind.AzureOpenAI
                ? $"{SectionName}:{nameof(Endpoint)} is required for AzureOpenAI: the resource's v1 endpoint, https://{{resource}}.openai.azure.com/openai/v1/."
                : $"{SectionName}:{nameof(Endpoint)} is required for OpenAICompatible: the server's OpenAI-style base address, e.g. http://vllm:8000/v1.";
        }

        if (kind != ChatProviderKind.Fake && !string.IsNullOrWhiteSpace(Endpoint) && EndpointUri is null)
        {
            return $"{SectionName}:{nameof(Endpoint)} must be an absolute http or https address.";
        }

        return kind is ChatProviderKind.OpenAI or ChatProviderKind.AzureOpenAI && string.IsNullOrWhiteSpace(ApiKey)
            ? $"{SectionName}:{nameof(ApiKey)} is required for {kind} (set it as the environment variable Ai__Chat__ApiKey)."
            : null;
    }

    /// <summary><see cref="Endpoint"/> as an absolute http(s) URI, or <see langword="null"/>.</summary>
    internal Uri? EndpointUri =>
        Uri.TryCreate(Endpoint?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
}
