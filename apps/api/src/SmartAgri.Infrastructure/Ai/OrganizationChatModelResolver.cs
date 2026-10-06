using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

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
/// The resolver the Api registers: reads the scope's organization's <c>ChatModelId</c> (M6-2) once
/// per scope — no cache across scopes, so a manager's change applies to the next conversation, test
/// run or report — and applies <see cref="Resolve"/>: an id the catalog still offers is
/// <see cref="ChatModelSource.Selected"/>, any other one <see cref="ChatModelSource.Removed"/> with
/// the default. A scope with no organization gets the default.
/// </summary>
public sealed class OrganizationChatModelResolver : IOrganizationChatModelResolver
{
    private readonly ChatModelCatalog _catalog;
    private readonly AppDbContext _dbContext;
    private ResolvedChatModel? _resolved;

    public OrganizationChatModelResolver(ChatModelCatalog catalog, AppDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dbContext);
        _catalog = catalog;
        _dbContext = dbContext;
    }

    public async ValueTask<ResolvedChatModel> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_resolved is null)
        {
            // Organizations is the tenant table itself (not filtered): read the scope's own row.
            var selectedId = _dbContext.OrganizationContext.OrganizationId is { } organizationId
                ? await _dbContext.Organizations
                    .AsNoTracking()
                    .Where(organization => organization.Id == organizationId)
                    .Select(organization => organization.ChatModelId)
                    .SingleOrDefaultAsync(cancellationToken)
                : null;
            _resolved ??= Resolve(_catalog, selectedId);
        }

        return _resolved;
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
