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
/// <remarks>
/// Two moments in authentication need an organization before the request's principal
/// carries one, and use <see cref="Pin"/> for the rest of the request:
/// <list type="bullet">
/// <item>sign-in, once <c>AccountLookup</c> has found the account by organization code
/// (Identity then records failed attempts, lockout and security stamps on that
/// account's row, which the save interceptor only allows for its own organization);</item>
/// <item>authenticating the Identity sign-in cookie, from the <c>org_id</c> claim inside
/// that (server-encrypted) cookie, so Identity's security-stamp check can load the
/// account.</item>
/// </list>
/// Pinning never widens anything: it selects exactly one organization, and it refuses to
/// contradict an organization already established for the request.
/// </remarks>
public sealed class ClaimsOrganizationContext : IOrganizationContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    private Guid? _pinnedOrganizationId;

    public ClaimsOrganizationContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? OrganizationId => _pinnedOrganizationId ?? Resolve(_httpContextAccessor.HttpContext?.User);

    /// <summary>
    /// Makes <paramref name="organizationId"/> the current organization for the rest of
    /// this request (this instance is request scoped). Only authentication code may call
    /// this, and only with an organization it has just established from an account row or
    /// a server-issued credential — never from client input as such.
    /// </summary>
    /// <exception cref="InvalidOperationException">A different organization is already
    /// current for this request (pinned, or from the authenticated principal).</exception>
    internal void Pin(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization id cannot be empty.", nameof(organizationId));
        }

        var current = OrganizationId;
        if (current is { } existing && existing != organizationId)
        {
            throw new InvalidOperationException(
                "This request already acts for a different organization; refusing to switch.");
        }

        _pinnedOrganizationId = organizationId;
    }

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
