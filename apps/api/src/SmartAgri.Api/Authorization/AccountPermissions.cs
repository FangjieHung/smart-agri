using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Accounts;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Authorization;

/// <summary>Reads an account's granted permissions from the store.</summary>
public interface IAccountPermissionSource
{
    Task<IReadOnlySet<AccountPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads permissions from <c>AccountPermissions</c>. Permissions are never carried in the
/// access token (M1 plan §3): reading them per request makes a change on the team panel
/// take effect immediately, with the same token. The query runs under the organization
/// filter, i.e. only rows of the token's <c>org_id</c> are visible — a token of
/// organization A naming an account of organization B sees no permissions.
/// </summary>
public sealed class DatabaseAccountPermissionSource : IAccountPermissionSource
{
    private readonly AppDbContext _dbContext;

    public DatabaseAccountPermissionSource(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlySet<AccountPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var granted = await _dbContext.AccountPermissions
            .AsNoTracking()
            .Where(grant => grant.AccountId == accountId)
            .Select(grant => grant.Permission)
            .ToListAsync(cancellationToken);

        return granted.ToHashSet();
    }
}

/// <summary>
/// Request-scoped cache in front of <see cref="IAccountPermissionSource"/>: several policy
/// checks and <c>/me</c> in one request cost one query, while the next request reads
/// fresh values.
/// </summary>
public sealed class RequestAccountPermissions
{
    private readonly IAccountPermissionSource _source;
    private readonly Dictionary<Guid, IReadOnlySet<AccountPermission>> _cache = [];

    public RequestAccountPermissions(IAccountPermissionSource source)
    {
        _source = source;
    }

    public async Task<IReadOnlySet<AccountPermission>> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!_cache.TryGetValue(accountId, out var permissions))
        {
            permissions = await _source.GetPermissionsAsync(accountId, cancellationToken);
            _cache[accountId] = permissions;
        }

        return permissions;
    }

    /// <summary>The permissions in <see cref="AccountPermission"/> declaration order (the
    /// frontend's <c>ACCOUNT_PERMISSIONS</c> order).</summary>
    public static IReadOnlyList<AccountPermission> Ordered(IReadOnlySet<AccountPermission> permissions) =>
        [.. Enum.GetValues<AccountPermission>().Where(permissions.Contains)];
}
