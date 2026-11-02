using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Infrastructure.Coordinator;

/// <summary>A shop a LINE user is bound to through the 統籌助理, and as what.</summary>
/// <param name="StoreName">The store name of the shop's settings (#373), or the organization's name when it
/// has none.</param>
public sealed record CoordinatorMembership(Guid OrganizationId, string StoreName, CoordinatorRole Role);

/// <summary>The binding code with a given hash: its id and organization, nothing else.</summary>
public sealed record CoordinatorCodeMatch(Guid CodeId, Guid OrganizationId);

/// <summary>The authorization link with a given token hash (#377): its id and organization, nothing else.</summary>
public sealed record CoordinatorLinkMatch(Guid LinkId, Guid OrganizationId);

/// <summary>
/// The 統籌助理's cross-organization reads (store-coordinator design decision C, #372, #377). The
/// platform's shared LINE account does not know which shop a message is for until it knows who sent it,
/// and an owner opening an authorization link is anonymous until the link is found, so — like
/// <see cref="SmartAgri.Infrastructure.Assistants.PublicAssistantLookup"/> for an anonymous visitor — nobody's
/// organization exists yet when these run, and this is the one place in <c>apps/api/src</c> allowed to
/// switch the organization filter off for coordinator data (a source-scanning test enforces it).
/// </summary>
/// <remarks>
/// It only reads (with the store names of the shops' settings, #373). Everything else — checking and
/// using a code, adding a member, issuing a code — runs in a context acting for the organization found
/// here (<c>FixedOrganizationContext</c>), under the usual filter and write guard.
/// </remarks>
public sealed class CoordinatorLookup
{
    private readonly AppDbContext _dbContext;

    public CoordinatorLookup(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>Every shop <paramref name="lineUserId"/> is bound to, by store name (the shop settings',
    /// else the organization's); empty when none.</summary>
    public async Task<IReadOnlyList<CoordinatorMembership>> FindMembershipsAsync(string lineUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(lineUserId);
        var memberships = await _dbContext.CoordinatorMembers
            .IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .AsNoTracking()
            .Where(member => member.LineUserId == lineUserId)
            .Join(
                _dbContext.Organizations,
                member => member.OrganizationId,
                organization => organization.Id,
                (member, organization) => new { member.OrganizationId, organization.Name, member.Role })
            .LeftJoin(
                _dbContext.CoordinatorShopSettings.IgnoreQueryFilters([AppDbContext.OrganizationFilter]),
                member => member.OrganizationId,
                settings => settings.OrganizationId,
                (member, settings) => new
                {
                    member.OrganizationId,
                    Name = settings == null ? member.Name : settings.StoreName,
                    member.Role,
                })
            .ToListAsync(cancellationToken);
        return
        [
            .. memberships
                .OrderBy(membership => membership.Name, StringComparer.Ordinal)
                .ThenBy(membership => membership.OrganizationId)
                .Select(membership => new CoordinatorMembership(membership.OrganizationId, membership.Name, membership.Role)),
        ];
    }

    /// <summary>The binding code whose <see cref="BindingCodes.Hash"/> is <paramref name="codeHash"/>, in
    /// any organization, used or not; <see langword="null"/> when there is none.</summary>
    public async Task<CoordinatorCodeMatch?> FindCodeAsync(string codeHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeHash);
        return await _dbContext.CoordinatorBindingCodes
            .IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .AsNoTracking()
            .Where(code => code.CodeHash == codeHash)
            .Select(code => new CoordinatorCodeMatch(code.Id, code.OrganizationId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>The authorization link whose <see cref="AuthorizationLinkTokens.Hash"/> is <paramref name="tokenHash"/>,
    /// in any organization, used or not; <see langword="null"/> when there is none (#377).</summary>
    public async Task<CoordinatorLinkMatch?> FindAuthorizationLinkAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenHash);
        return await _dbContext.CoordinatorAuthorizationLinks
            .IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .AsNoTracking()
            .Where(link => link.TokenHash == tokenHash)
            .Select(link => new CoordinatorLinkMatch(link.Id, link.OrganizationId))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
