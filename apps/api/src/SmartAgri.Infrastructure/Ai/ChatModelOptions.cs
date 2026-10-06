using System.Text.RegularExpressions;

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
    /// only (<see cref="ChatModelEntryOptions.FakeEnvironments"/>).</summary>
    Fake,
}

/// <summary>
/// One chat model a deployment offers: the deployment default (<see cref="ChatModelOptions"/>,
/// <c>Ai:Chat</c> itself) or one more entry of <c>Ai:Chat:Models</c> (M6 plan §3 A). Every entry
/// has its own provider, key, output cap, reasoning effort and timeout.
/// </summary>
public partial class ChatModelEntryOptions
{
    /// <summary>The environments where <see cref="ChatProviderKind.Fake"/> may run.</summary>
    public static readonly IReadOnlyList<string> FakeEnvironments = ["Development", "Testing"];

    /// <summary>The longest <see cref="Id"/>: what an organization's choice is stored as (M6-2).</summary>
    public const int IdMaxLength = 64;

    /// <summary><c>OpenAI</c>, <c>AzureOpenAI</c>, <c>OpenAICompatible</c> or <c>Fake</c>
    /// (case-insensitive); empty when the deployment has no chat model yet — or, for an entry of
    /// <c>Ai:Chat:Models</c>, when that entry is a blank placeholder, which is skipped.</summary>
    public string? Provider { get; set; }

    /// <summary>The API base address: required for Azure OpenAI and OpenAI-compatible servers,
    /// optional for OpenAI.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The model (Azure: deployment) name.</summary>
    public string? Model { get; set; }

    /// <summary>Required for OpenAI and Azure OpenAI; optional for OpenAI-compatible servers.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The cap on generated tokens, applied to every call of this model that does not set
    /// its own (<see cref="Microsoft.Extensions.AI.ChatOptions.MaxOutputTokens"/>); a runaway
    /// answer must not be unbounded. <see langword="null"/> leaves it to the provider's own default.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>How long a single call may take before it is treated as failed; <see langword="null"/>
    /// leaves it to the client's own default.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>The reasoning effort applied to every call of this model that does not set its own
    /// (<see cref="Microsoft.Extensions.AI.ChatOptions.Reasoning"/>): <c>None</c>, <c>Low</c>,
    /// <c>Medium</c>, <c>High</c> or <c>ExtraHigh</c> (case-insensitive). Some reasoning models refuse
    /// function tools on Chat Completions unless it is <c>None</c> (M4 #164: <c>gpt-6-luna</c>).
    /// <see langword="null"/> leaves it to the provider's own default.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>What the deployment calls this entry, and what an organization's choice is stored
    /// as: letters, digits, <c>-</c>, <c>_</c> and <c>.</c>, at most <see cref="IdMaxLength"/>.
    /// Unset, it is <see cref="Model"/> — so a single <c>Ai:Chat</c> keeps the same id when the
    /// deployment later lists more models.</summary>
    public string? Id { get; set; }

    /// <summary>The name people see in the settings; unset, it is <see cref="Model"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Whether this entry names a provider at all (a blank one is "no model").</summary>
    public bool HasProvider => !string.IsNullOrWhiteSpace(Provider);

    /// <summary><see cref="Id"/>, or <see cref="Model"/> when unset (both trimmed).</summary>
    public string EffectiveId => string.IsNullOrWhiteSpace(Id) ? Model?.Trim() ?? string.Empty : Id.Trim();

    /// <summary><see cref="DisplayName"/>, or <see cref="Model"/> when unset (both trimmed).</summary>
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? Model?.Trim() ?? string.Empty : DisplayName.Trim();

    /// <summary>The parsed <see cref="Provider"/>; <see langword="null"/> when unset (or not a
    /// known name, which validation refuses).</summary>
    public ChatProviderKind? ProviderKind =>
        Enum.GetValues<ChatProviderKind>()
            .Where(kind => string.Equals(kind.ToString(), Provider?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(kind => (ChatProviderKind?)kind)
            .SingleOrDefault();

    /// <summary>The parsed <see cref="ReasoningEffort"/>; <see langword="null"/> when unset (or not a
    /// known name, which validation refuses).</summary>
    public Microsoft.Extensions.AI.ReasoningEffort? ReasoningEffortKind =>
        Enum.GetValues<Microsoft.Extensions.AI.ReasoningEffort>()
            .Where(effort => string.Equals(effort.ToString(), ReasoningEffort?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(effort => (Microsoft.Extensions.AI.ReasoningEffort?)effort)
            .SingleOrDefault();

    /// <summary><see cref="Endpoint"/> as an absolute http(s) URI, or <see langword="null"/>.</summary>
    internal Uri? EndpointUri =>
        Uri.TryCreate(Endpoint?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>Whether <paramref name="id"/> uses only the characters an id may have.</summary>
    public static bool IsValidId(string id) => id.Length is > 0 and <= IdMaxLength && IdPattern().IsMatch(id);

    /// <summary>Why this entry, configured at <paramref name="path"/> (<c>Ai:Chat</c> or
    /// <c>Ai:Chat:Models:{n}</c>), cannot be used in <paramref name="environmentName"/>, or
    /// <see langword="null"/>. A blank <see cref="Provider"/> only has its numbers checked.</summary>
    internal string? ValidateEntry(string environmentName, string path)
    {
        ArgumentNullException.ThrowIfNull(environmentName);

        if (MaxOutputTokens is <= 0)
        {
            return $"{path}:{nameof(MaxOutputTokens)} must be a positive number of tokens, or unset.";
        }

        if (TimeoutSeconds is <= 0)
        {
            return $"{path}:{nameof(TimeoutSeconds)} must be a positive number of seconds, or unset.";
        }

        if (!string.IsNullOrWhiteSpace(ReasoningEffort) && ReasoningEffortKind is null)
        {
            return $"{path}:{nameof(ReasoningEffort)} must be None, Low, Medium, High or ExtraHigh, not '{ReasoningEffort}', or unset.";
        }

        if (!HasProvider)
        {
            return null;
        }

        if (ProviderKind is not { } kind)
        {
            return $"{path}:{nameof(Provider)} must be OpenAI, AzureOpenAI, OpenAICompatible or Fake, not '{Provider}'.";
        }

        if (kind == ChatProviderKind.Fake && !FakeEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase))
        {
            return $"{path}:{nameof(Provider)}=Fake is only allowed in the Development and Testing environments, " +
                $"not in '{environmentName}': its answers are scripted, not generated. Configure OpenAI, AzureOpenAI or OpenAICompatible.";
        }

        if (string.IsNullOrWhiteSpace(Model) || Model.Trim().Length > Domain.Ai.ModelInvocation.ModelMaxLength)
        {
            return $"{path}:{nameof(Model)} is required with a provider (1-{Domain.Ai.ModelInvocation.ModelMaxLength} characters; for Azure OpenAI, the deployment name).";
        }

        if (!string.IsNullOrWhiteSpace(Id) && !IsValidId(Id.Trim()))
        {
            return $"{path}:{nameof(Id)} '{Id.Trim()}' may only use letters, digits, '-', '_' and '.' (1-{IdMaxLength} characters).";
        }

        var endpointRequired = kind is ChatProviderKind.AzureOpenAI or ChatProviderKind.OpenAICompatible;
        if (endpointRequired && string.IsNullOrWhiteSpace(Endpoint))
        {
            return kind == ChatProviderKind.AzureOpenAI
                ? $"{path}:{nameof(Endpoint)} is required for AzureOpenAI: the resource's v1 endpoint, https://{{resource}}.openai.azure.com/openai/v1/."
                : $"{path}:{nameof(Endpoint)} is required for OpenAICompatible: the server's OpenAI-style base address, e.g. http://vllm:8000/v1.";
        }

        if (kind != ChatProviderKind.Fake && !string.IsNullOrWhiteSpace(Endpoint) && EndpointUri is null)
        {
            return $"{path}:{nameof(Endpoint)} must be an absolute http or https address.";
        }

        return kind is ChatProviderKind.OpenAI or ChatProviderKind.AzureOpenAI && string.IsNullOrWhiteSpace(ApiKey)
            ? $"{path}:{nameof(ApiKey)} is required for {kind} (set it as the environment variable {path.Replace(":", "__", StringComparison.Ordinal)}__{nameof(ApiKey)})."
            : null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex IdPattern();
}

/// <summary>
/// Configuration section <c>Ai:Chat</c> (M3 plan §3: "對話模型的設定與稽核照抄嵌入的模式";
/// apps/api/README.md, "Chat model"): the deployment's default chat model, the same shape as
/// <see cref="EmbeddingOptions"/>, plus <see cref="Models"/>, the further models the deployment
/// offers (M6 plan §3 A). Secrets come from the environment (<c>Ai__Chat__ApiKey</c>,
/// <c>Ai__Chat__Models__0__ApiKey</c>, …); encrypting them is M5 (secrets-storage ADR).
/// </summary>
/// <remarks>
/// <para>
/// Leaving <see cref="ChatModelEntryOptions.Provider"/> unset is allowed — the Api still starts —
/// but every call throws <c>ChatGenerationException(providerNotConfigured: true)</c> before reaching
/// any provider, so a deployment can be installed before it has a chat model. Anything set but
/// unusable fails startup (<see cref="Validate"/>), and <see cref="ChatProviderKind.Fake"/> outside
/// Development/Testing (most importantly, Production) always fails startup: a fake answer must
/// never reach a real user.
/// </para>
/// <para>
/// An entry of <see cref="Models"/> without a provider is a blank placeholder (a compose file may
/// reserve one) and is skipped. With any other entry, <c>Ai:Chat</c> itself must be configured —
/// it is the default every organization gets until it chooses — every entry is validated like
/// <c>Ai:Chat</c>, and no two entries may share an id.
/// </para>
/// </remarks>
public sealed class ChatModelOptions : ChatModelEntryOptions
{
    public const string SectionName = "Ai:Chat";

    /// <summary>The further chat models the deployment offers, besides <c>Ai:Chat</c> itself
    /// (<c>Ai:Chat:Models:{n}</c>); empty for a deployment with one model.</summary>
    public List<ChatModelEntryOptions> Models { get; set; } = [];

    /// <summary>The configured entries of <see cref="Models"/> (blank placeholders skipped), each
    /// with its configuration path.</summary>
    public IEnumerable<(ChatModelEntryOptions Entry, string Path)> ConfiguredModels =>
        Models.Select((entry, index) => (entry, $"{SectionName}:{nameof(Models)}:{index}"))
            .Where(pair => pair.entry is not null && pair.entry.HasProvider);

    /// <summary>Why these options cannot be used in <paramref name="environmentName"/>, or
    /// <see langword="null"/>.</summary>
    public string? Validate(string environmentName)
    {
        if (ValidateEntry(environmentName, SectionName) is { } error)
        {
            return error;
        }

        var additional = ConfiguredModels.ToList();
        if (additional.Count == 0)
        {
            return null;
        }

        if (!HasProvider)
        {
            return $"{SectionName}:{nameof(Provider)} is required when {SectionName}:{nameof(Models)} lists more chat models: " +
                $"the {SectionName} model is the deployment default, the one every organization uses until it chooses another.";
        }

        foreach (var (entry, path) in additional)
        {
            if (entry.ValidateEntry(environmentName, path) is { } entryError)
            {
                return entryError;
            }
        }

        // With more than one model, every id is what an organization's choice is stored as — even
        // one that defaults to a model name (a single Ai:Chat keeps any model name as its id).
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, path) in additional.Prepend((this, SectionName)))
        {
            var id = entry.EffectiveId;
            if (!IsValidId(id))
            {
                return $"{path}:{nameof(Id)} is required when the model name '{id}' is not a usable id: " +
                    $"with {SectionName}:{nameof(Models)} set, every id may only use letters, digits, '-', '_' and '.' (1-{IdMaxLength} characters).";
            }

            if (ids.TryGetValue(id, out var firstPath))
            {
                return $"{path}:{nameof(Id)} '{id}' is already the id of {firstPath}: every chat model needs its own id " +
                    $"(set {nameof(Id)}, which defaults to {nameof(Model)}).";
            }

            ids.Add(id, path);
        }

        return null;
    }
}
