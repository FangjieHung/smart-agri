using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Organizations;

/// <summary><c>GET /api/v1/organization/usage</c>'s response (M5a plan §3 F, issue #195).</summary>
/// <param name="Month">The current month in <c>Statistics:TimeZone</c>, <c>YYYY-MM</c>.</param>
/// <param name="UsedTokens">Input + output tokens of the organization's chat-model calls this month
/// (embedding calls are not counted).</param>
/// <param name="LimitTokens">The organization's monthly limit, or the deployment default.</param>
/// <param name="State"><c>normal</c> under 80%, <c>near</c> from 80%, <c>exceeded</c> from 100%.</param>
public sealed record OrganizationUsageView(string Month, long UsedTokens, long LimitTokens, TokenUsageState State);

/// <summary>
/// <c>GET /api/v1/organization/usage</c>: the organization's token usage this month against its
/// limit, for the admin's publishing pages and banner. Needs <c>manage-publishing</c>; the numbers
/// are the organization's as a whole (they come from the same cached figure that suspends the
/// website channel), so they can be up to 30 seconds old.
/// </summary>
public static class OrganizationUsageEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationUsageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/organization/usage", GetAsync)
            .RequireAuthorization()
            .RequirePermission(AccountPermission.ManagePublishing, ForbiddenReason.Publishing)
            .Produces<OrganizationUsageView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        return endpoints;
    }

    internal static async Task<IResult> GetAsync(
        IOrganizationContext organization, OrganizationTokenUsage tokenUsage, CancellationToken cancellationToken)
    {
        if (organization.OrganizationId is not { } organizationId)
        {
            return ApiErrors.Unauthorized();
        }

        var usage = await tokenUsage.GetAsync(organizationId, cancellationToken);
        return Results.Ok(new OrganizationUsageView(usage.Month, usage.UsedTokens, usage.LimitTokens, usage.State));
    }
}
