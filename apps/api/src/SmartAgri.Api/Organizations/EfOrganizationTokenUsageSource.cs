using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Organizations;

/// <summary>
/// <see cref="IOrganizationTokenUsageSource"/> over PostgreSQL. A singleton (the usage cache is),
/// so each read takes a scope of its own for the context options and acts for the organization it
/// is asked about, through a context of its own: the caller's request context, if any, is never
/// borrowed.
/// </summary>
internal sealed class EfOrganizationTokenUsageSource : IOrganizationTokenUsageSource
{
    private readonly IServiceScopeFactory _scopes;

    public EfOrganizationTokenUsageSource(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public async Task<long?> GetMonthlyTokenLimitAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        await using var dbContext = CreateContext(scope, organizationId);
        return await dbContext.Organizations.AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.MonthlyTokenLimit)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<long> SumChatTokensAsync(
        Guid organizationId, DateTimeOffset startInclusive, DateTimeOffset endExclusive, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        await using var dbContext = CreateContext(scope, organizationId);
        return await ChatTokens(dbContext, startInclusive, endExclusive).SumAsync(cancellationToken);
    }

    /// <summary>The query behind <see cref="SumChatTokensAsync"/>: the organization filter and the
    /// <c>(OrganizationId, At)</c> index narrow it to the month, then the purpose filters the rows
    /// that are summed; a null count is 0.</summary>
    internal static IQueryable<long> ChatTokens(AppDbContext dbContext, DateTimeOffset startInclusive, DateTimeOffset endExclusive)
    {
        var purposes = OrganizationTokenUsageRules.CountedPurposes;
        return dbContext.ModelInvocations.AsNoTracking()
            .Where(invocation => invocation.At >= startInclusive && invocation.At < endExclusive)
            .Where(invocation => purposes.Contains(invocation.Purpose))
            .Select(invocation => (invocation.InputTokens ?? 0) + (invocation.OutputTokens ?? 0));
    }

    private static AppDbContext CreateContext(AsyncServiceScope scope, Guid organizationId) =>
        new(
            scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(),
            new FixedOrganizationContext(organizationId));
}
