namespace SmartAgri.Infrastructure.Ai;

/// <summary>One chat model the deployment offers (M6 plan §3 A): its id, the name people see,
/// and its client, configured with that entry's own settings.</summary>
public sealed class ChatModelEntry
{
    public ChatModelEntry(string id, string displayName, ChatClientProvider provider, bool isDeploymentDefault)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(provider);
        Id = id;
        DisplayName = displayName;
        Provider = provider;
        IsDeploymentDefault = isDeploymentDefault;
    }

    /// <summary><c>Id</c> from the configuration, or the model name when unset.</summary>
    public string Id { get; }

    /// <summary><c>DisplayName</c> from the configuration, or the model name when unset.</summary>
    public string DisplayName { get; }

    /// <summary>This model's client (never called directly: the Api wraps it in the recording
    /// middleware).</summary>
    public ChatClientProvider Provider { get; }

    /// <summary>Whether this is <c>Ai:Chat</c> itself, the model every organization gets until it
    /// chooses another.</summary>
    public bool IsDeploymentDefault { get; }

    /// <summary>The model (Azure: deployment) name, what <c>ModelInvocation.Model</c> and
    /// <c>AssistantTestRun.Model</c> record.</summary>
    public string Model => Provider.Model;
}

/// <summary>
/// The chat models this deployment offers (M6 plan §3 B), a singleton: the deployment default
/// (<c>Ai:Chat</c>, the registered <see cref="ChatClientProvider"/>) first, then every configured
/// entry of <c>Ai:Chat:Models</c>, each with its own client. Which one a call uses is decided per
/// organization by <see cref="IOrganizationChatModelResolver"/>.
/// </summary>
/// <remarks>
/// The deployment default's client is the registered <see cref="ChatClientProvider"/> singleton, not
/// a copy — so evaluation commands that use it directly, and a test host that replaces it, see the
/// same model. The catalog disposes only the clients it created for <c>Ai:Chat:Models</c>.
/// </remarks>
public sealed class ChatModelCatalog : IDisposable
{
    private readonly IReadOnlyList<ChatClientProvider> _owned;

    public ChatModelCatalog(ChatModelEntry deploymentDefault, IReadOnlyList<ChatModelEntry> additional)
        : this(deploymentDefault, additional, owned: [])
    {
    }

    private ChatModelCatalog(ChatModelEntry deploymentDefault, IReadOnlyList<ChatModelEntry> additional, IReadOnlyList<ChatClientProvider> owned)
    {
        ArgumentNullException.ThrowIfNull(deploymentDefault);
        ArgumentNullException.ThrowIfNull(additional);
        DeploymentDefault = deploymentDefault;
        Entries = deploymentDefault.Provider.IsConfigured ? [deploymentDefault, .. additional] : [];
        _owned = owned;
    }

    /// <summary><c>Ai:Chat</c>: what every organization uses until it chooses (or when its choice
    /// is no longer offered). Unconfigured when the deployment has no chat model.</summary>
    public ChatModelEntry DeploymentDefault { get; }

    /// <summary>Every model the deployment offers, the default first; empty when the deployment has
    /// no chat model (startup refuses <c>Ai:Chat:Models</c> without a default).</summary>
    public IReadOnlyList<ChatModelEntry> Entries { get; }

    /// <summary>Whether the deployment has a chat model at all: conversations can be answered.</summary>
    public bool IsConfigured => DeploymentDefault.Provider.IsConfigured;

    /// <summary>The offered entry whose id is <paramref name="id"/> (ids are compared ignoring
    /// case, as startup refuses two that differ only in case), or <see langword="null"/>.</summary>
    public ChatModelEntry? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Entries.FirstOrDefault(entry => string.Equals(entry.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The catalog <paramref name="options"/> describe (validated: <see cref="ChatModelOptions.Validate"/>),
    /// with <paramref name="deploymentDefault"/> — the registered provider for <c>Ai:Chat</c> — as its
    /// default, and a new client for each configured entry of <c>Ai:Chat:Models</c>.
    /// </summary>
    public static ChatModelCatalog Create(ChatModelOptions options, ChatClientProvider deploymentDefault) =>
        Create(options, deploymentDefault, ChatClientProvider.Create);

    /// <summary><see cref="Create(ChatModelOptions, ChatClientProvider)"/> with each additional
    /// entry's client made by <paramref name="createProvider"/> (a test seam).</summary>
    internal static ChatModelCatalog Create(
        ChatModelOptions options,
        ChatClientProvider deploymentDefault,
        Func<ChatModelEntryOptions, ChatClientProvider> createProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deploymentDefault);
        ArgumentNullException.ThrowIfNull(createProvider);

        // The default's id and name fall back to its client's model, which is the configured one —
        // or, in a test host that replaced the registered provider, the replacement's.
        var defaultEntry = new ChatModelEntry(
            string.IsNullOrWhiteSpace(options.Id) ? deploymentDefault.Model : options.Id.Trim(),
            string.IsNullOrWhiteSpace(options.DisplayName) ? deploymentDefault.Model : options.DisplayName.Trim(),
            deploymentDefault,
            isDeploymentDefault: true);
        if (!deploymentDefault.IsConfigured)
        {
            return new ChatModelCatalog(defaultEntry, [], owned: []);
        }

        var owned = new List<ChatClientProvider>();
        var additional = new List<ChatModelEntry>();
        foreach (var (entry, _) in options.ConfiguredModels)
        {
            var provider = createProvider(entry);
            owned.Add(provider);
            additional.Add(new ChatModelEntry(entry.EffectiveId, entry.EffectiveDisplayName, provider, isDeploymentDefault: false));
        }

        return new ChatModelCatalog(defaultEntry, additional, owned);
    }

    public void Dispose()
    {
        foreach (var provider in _owned)
        {
            provider.Dispose();
        }
    }
}
