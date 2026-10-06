using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Errors;
using SmartAgri.Domain.Accounts;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Authorization;

/// <summary>
/// Request-scoped: the caller's role, read from <c>Accounts</c> — never from the access token's
/// <c>role</c> claim — so a role changed in the database takes effect on the very next request with
/// the same token (M6 plan §3 C, decision A). One query per request however often it is asked. The
/// read runs under the organization filter: an account of another organization is not seen, so it
/// is never a manager here.
/// </summary>
/// <remarks>
/// Endpoints guard with <see cref="OrganizationAdminPolicy.RequireOrganizationAdmin{TBuilder}"/>;
/// code that only needs to know (a view's <c>canChange</c>, M7's "the case's owner or a manager")
/// asks <see cref="IsOrganizationAdminAsync(ClaimsPrincipal, CancellationToken)"/>.
/// </remarks>
public sealed class RequestAccountRole
{
    private readonly AppDbContext _dbContext;
    private readonly Dictionary<Guid, AccountRole?> _cache = [];

    public RequestAccountRole(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>The account's role now, or <see langword="null"/> when it is not an account of the
    /// current organization.</summary>
    public async Task<AccountRole?> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!_cache.TryGetValue(accountId, out var role))
        {
            role = await _dbContext.Accounts
                .AsNoTracking()
                .Where(account => account.Id == accountId)
                .Select(account => (AccountRole?)account.Role)
                .SingleOrDefaultAsync(cancellationToken);
            _cache[accountId] = role;
        }

        return role;
    }

    /// <summary>Whether <paramref name="accountId"/> is the organization's manager (<c>smb-admin</c>).</summary>
    public async Task<bool> IsOrganizationAdminAsync(Guid accountId, CancellationToken cancellationToken) =>
        await GetAsync(accountId, cancellationToken) == AccountRole.SmbAdmin;

    /// <summary>Whether the signed-in <paramref name="user"/> is the organization's manager;
    /// <see langword="false"/> when not signed in.</summary>
    public Task<bool> IsOrganizationAdminAsync(ClaimsPrincipal user, CancellationToken cancellationToken) =>
        AccountClaims.GetAccountId(user) is { } accountId
            ? IsOrganizationAdminAsync(accountId, cancellationToken)
            : Task.FromResult(false);
}

/// <summary>
/// The one manager check (M6 plan §3 C, decision A): "manager only" is the role <c>smb-admin</c>, not
/// a permission — <c>manage-assistants</c> is also given to colleagues who build assistants, and they
/// must not change organization settings. Used by the chat model (M6-2), retention and purges
/// (M6-4/5) and M7's case teams and types.
/// </summary>
public static class OrganizationAdminPolicy
{
    /// <summary>The authorization policy's name.</summary>
    public const string Name = "organization-admin";

    public static AuthorizationBuilder AddOrganizationAdminPolicy(this AuthorizationBuilder builder) =>
        builder.AddPolicy(Name, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(OrganizationAdminRequirement.Instance));

    /// <summary>
    /// Only the organization's manager may call this endpoint. Anyone else — an internal employee,
    /// an external customer, an account of another organization — gets
    /// <see cref="ApiErrors.Forbidden"/> for <paramref name="reason"/>: the same bytes the endpoint
    /// must return for a resource that does not exist or belongs to another organization. Not
    /// signed in stays a bodiless <c>401</c>, and the password-change gate still answers first. An
    /// authorization policy (like <see cref="PermissionPolicies.RequirePermission{TBuilder}"/>), so
    /// it runs before the body is read: a non-manager learns nothing from a malformed body.
    /// </summary>
    public static TBuilder RequireOrganizationAdmin<TBuilder>(this TBuilder builder, ForbiddenReason reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(reason);
        return builder
            .RequireAuthorization(Name)
            .WithMetadata(new ForbiddenReasonMetadata(reason));
    }
}

/// <summary>The requirement behind <see cref="OrganizationAdminPolicy"/>.</summary>
public sealed class OrganizationAdminRequirement : IAuthorizationRequirement
{
    public static readonly OrganizationAdminRequirement Instance = new();

    private OrganizationAdminRequirement()
    {
    }
}

/// <summary>Succeeds when the caller's account (<c>sub</c>) is currently <c>smb-admin</c> in the
/// token's organization, read from the database (<see cref="RequestAccountRole"/>).</summary>
public sealed class OrganizationAdminAuthorizationHandler : AuthorizationHandler<OrganizationAdminRequirement>
{
    private readonly RequestAccountRole _roles;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public OrganizationAdminAuthorizationHandler(RequestAccountRole roles, IHttpContextAccessor httpContextAccessor)
    {
        _roles = roles;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OrganizationAdminRequirement requirement)
    {
        var cancellationToken = _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        if (await _roles.IsOrganizationAdminAsync(context.User, cancellationToken))
        {
            context.Succeed(requirement);
        }
    }
}
