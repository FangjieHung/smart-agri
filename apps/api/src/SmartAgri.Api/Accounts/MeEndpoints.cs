using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Errors;
using SmartAgri.Domain.Accounts;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Accounts;

/// <summary><c>GET /api/v1/me</c> response. <see cref="Role"/> and
/// <see cref="Permissions"/> serialize as the frontend's kebab-case strings
/// (<c>account.model.ts</c>); permissions are in <c>ACCOUNT_PERMISSIONS</c> order.
/// <see cref="PasswordChangeRequired"/> is true while the account still has its one-time
/// password from <c>setup</c>: the SPA must then send the user to set a new password
/// (every other protected endpoint answers <c>403 password-change-required</c>).
/// <see cref="StatisticsTimeZone"/> is the server's <c>Statistics:TimeZone</c> as an IANA id (#177):
/// the SPA labels record dates with the calendar days of this zone, the same days the statistics
/// count in, instead of a constant of its own.</summary>
public sealed record MeResponse(
    Guid Id,
    string DisplayName,
    AccountRole Role,
    IReadOnlyList<AccountPermission> Permissions,
    MeOrganization Organization,
    bool PasswordChangeRequired,
    string StatisticsTimeZone);

public sealed record MeOrganization(Guid Id, string Name);

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/me", GetMeAsync)
            .RequireAuthorization()
            .AllowWhilePasswordChangeRequired()
            .Produces<MeResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);
        return endpoints;
    }

    /// <summary>
    /// The signed-in account, read fresh from the database (role and permissions are not
    /// taken from the token). <c>401</c> if the token's account no longer exists in the
    /// token's organization.
    /// </summary>
    internal static async Task<IResult> GetMeAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        IOptions<StatisticsOptions> statistics,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } accountId)
        {
            return ApiErrors.Unauthorized();
        }

        // Both queries run under the organization filter (org_id from the token).
        var account = await dbContext.Accounts
            .AsNoTracking()
            .Where(candidate => candidate.Id == accountId)
            .Select(candidate => new { candidate.Id, candidate.DisplayName, candidate.Role, candidate.OrganizationId, candidate.PasswordChangeRequired })
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return ApiErrors.Unauthorized();
        }

        var organization = await dbContext.Organizations
            .AsNoTracking()
            .Where(candidate => candidate.Id == account.OrganizationId)
            .Select(candidate => new MeOrganization(candidate.Id, candidate.Name))
            .SingleAsync(cancellationToken);

        var granted = await permissions.GetAsync(accountId, cancellationToken);

        return Results.Ok(new MeResponse(
            account.Id,
            account.DisplayName,
            account.Role,
            RequestAccountPermissions.Ordered(granted),
            organization,
            account.PasswordChangeRequired,
            statistics.Value.TryResolveIanaId()
                ?? throw new InvalidOperationException("Statistics:TimeZone was validated at startup.")));
    }
}
