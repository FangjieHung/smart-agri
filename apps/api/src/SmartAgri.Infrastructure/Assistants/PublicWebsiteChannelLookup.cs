using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>What <see cref="PublicWebsiteChannelLookup"/> reveals about a website channel: whether the
/// owner published it, and which host names may embed it. Nothing else — no organization, no
/// assistant, no settings.</summary>
/// <param name="State">The owner's choice (draft, published or paused).</param>
/// <param name="Domains">The allowed host names, lower case, in ordinal order.</param>
public sealed record PublicWebsiteChannel(WebsiteChannelState State, IReadOnlyList<string> Domains);

/// <summary>
/// Finds a website channel by its assistant's id for the page that is embedded in a customer's
/// website (<c>GET /use/{assistantId}</c>, M5a plan §3 B). The visitor's browser carries no
/// organization, so the organization filter would hide every row; this is therefore the second
/// place in <c>apps/api/src</c> allowed to switch that filter off, next to <c>AccountLookup</c>
/// (a source-scanning test enforces the list).
/// </summary>
/// <remarks>
/// The assistant id is a globally unique key, so the lookup is pinned to exactly one assistant,
/// and it projects only <see cref="PublicWebsiteChannel"/>: the caller never gets an entity, an
/// organization id or any setting of another organization. Read on every call and never cached,
/// so a domain removed in the settings stops being allowed with the next request.
/// </remarks>
public sealed class PublicWebsiteChannelLookup
{
    private readonly AppDbContext _dbContext;

    public PublicWebsiteChannelLookup(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// The channel of the assistant <paramref name="assistantId"/>, or <see langword="null"/> when no
    /// settings were ever saved for it or the assistant does not exist (callers must not reveal which).
    /// </summary>
    public async Task<PublicWebsiteChannel?> FindAsync(Guid assistantId, CancellationToken cancellationToken = default)
    {
        var state = await _dbContext.AssistantWebsiteChannels
            .IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .AsNoTracking()
            .Where(channel => channel.AssistantId == assistantId)
            .Select(channel => (WebsiteChannelState?)channel.State)
            .SingleOrDefaultAsync(cancellationToken);
        if (state is null)
        {
            return null;
        }

        var domains = await _dbContext.AssistantWebsiteDomains
            .IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .AsNoTracking()
            .Where(domain => domain.AssistantId == assistantId)
            .Select(domain => domain.Domain)
            .ToListAsync(cancellationToken);
        domains.Sort(StringComparer.Ordinal);

        return new PublicWebsiteChannel(state.Value, domains);
    }
}
