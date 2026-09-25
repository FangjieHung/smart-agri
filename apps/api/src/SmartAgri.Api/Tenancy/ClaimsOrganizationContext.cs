using System.Security.Claims;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tenancy;

/// <summary>
/// The current request's organization, taken from the authenticated principal's
/// <see cref="OrganizationClaimTypes.OrganizationId"/> claim. Anything else — no request,
/// an unauthenticated principal, a missing, malformed or empty id, or more than one
/// distinct id — is "no organization", under which organization-filtered queries return
/// nothing and organization-scoped writes are refused. It never falls back to "all".
/// </summary>
public sealed class ClaimsOrganizationContext : IOrganizationContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ClaimsOrganizationContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? OrganizationId => Resolve(_httpContextAccessor.HttpContext?.User);

    internal static Guid? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        Guid? organizationId = null;
        foreach (var claim in principal.FindAll(OrganizationClaimTypes.OrganizationId))
        {
            if (!Guid.TryParseExact(claim.Value, "D", out var parsed) || parsed == Guid.Empty)
            {
                return null;
            }

            if (organizationId is { } seen && seen != parsed)
            {
                return null;
            }

            organizationId = parsed;
        }

        return organizationId;
    }
}
