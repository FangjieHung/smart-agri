using System.Text.Json.Serialization;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>Why an organization uses the chat model it does (M6 plan §3 D, <c>source</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatModelSource>))]
public enum ChatModelSource
{
    /// <summary>The organization chose this model, and the deployment still offers it.</summary>
    [JsonStringEnumMemberName("selected")]
    Selected,

    /// <summary>The organization has not chosen: the deployment default.</summary>
    [JsonStringEnumMemberName("deployment-default")]
    DeploymentDefault,

    /// <summary>The organization chose a model the deployment no longer offers: the deployment
    /// default instead, until it chooses again.</summary>
    [JsonStringEnumMemberName("removed")]
    Removed,
}

/// <summary>The chat model a scope's organization uses, and why.</summary>
public sealed record ResolvedChatModel(ChatModelEntry Entry, ChatModelSource Source);

/// <summary>
/// Scoped. Which of the deployment's chat models (<see cref="ChatModelCatalog"/>) the scope's
/// organization uses (M6 plan §3 B). Every chat call goes through it — the scoped
/// <c>IChatClient</c> resolves it before its first call, and a handler that records the model it
/// used (a test-set rerun, a report summary) asks it for the same answer.
/// </summary>
public interface IOrganizationChatModelResolver
{
    /// <summary>The scope's organization's chat model; the same answer for the whole scope.</summary>
    ValueTask<ResolvedChatModel> ResolveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The resolver the Api registers. M6-1: organizations cannot choose yet, so every organization
/// gets the deployment default (<see cref="ChatModelSource.DeploymentDefault"/>) and behavior is
/// unchanged.
/// </summary>
/// <remarks>
/// M6-2 makes it read the organization's <c>ChatModelId</c> once per scope (no cache across scopes,
/// so a change applies to the next conversation): an id the catalog still offers is
/// <see cref="ChatModelSource.Selected"/>, any other one <see cref="ChatModelSource.Removed"/> with
/// the default — see <see cref="Resolve"/>, which already implements that rule.
/// </remarks>
public sealed class OrganizationChatModelResolver : IOrganizationChatModelResolver
{
    private readonly ChatModelCatalog _catalog;
    private ResolvedChatModel? _resolved;

    public OrganizationChatModelResolver(ChatModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public ValueTask<ResolvedChatModel> ResolveAsync(CancellationToken cancellationToken)
    {
        // M6-2: read the scope's organization's ChatModelId here (the organization-filtered
        // AppDbContext), once, and pass it as selectedId.
        _resolved ??= Resolve(_catalog, selectedId: null);
        return ValueTask.FromResult(_resolved);
    }

    /// <summary>The entry <paramref name="catalog"/> gives an organization that chose
    /// <paramref name="selectedId"/> (<see langword="null"/>: none).</summary>
    public static ResolvedChatModel Resolve(ChatModelCatalog catalog, string? selectedId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            return new ResolvedChatModel(catalog.DeploymentDefault, ChatModelSource.DeploymentDefault);
        }

        return catalog.Find(selectedId) is { } chosen
            ? new ResolvedChatModel(chosen, ChatModelSource.Selected)
            : new ResolvedChatModel(catalog.DeploymentDefault, ChatModelSource.Removed);
    }
}
