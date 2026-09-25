using System.Security.Claims;
using SmartAgri.Api.Tenancy;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAgri.Api.Authentication;

/// <summary>
/// The only claims an access token carries (M1 plan §3): <c>sub</c> (account id),
/// <c>org_id</c> and <c>role</c>. Permissions are deliberately absent — they are read
/// from the database on every request.
/// </summary>
public static class AccountClaims
{
    public const string Subject = Claims.Subject;

    public const string OrganizationId = OrganizationClaimTypes.OrganizationId;

    public const string Role = Claims.Role;

    /// <summary>
    /// The account id in the authenticated principal's single <c>sub</c> claim, or
    /// <see langword="null"/> when unauthenticated, missing, malformed or ambiguous.
    /// </summary>
    public static Guid? GetAccountId(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var subjects = principal.FindAll(Subject).Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToList();
        return subjects is [var subject] && Guid.TryParseExact(subject, "D", out var accountId) && accountId != Guid.Empty
            ? accountId
            : null;
    }
}
