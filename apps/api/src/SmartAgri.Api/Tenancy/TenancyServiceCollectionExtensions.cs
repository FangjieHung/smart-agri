using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped <see cref="IOrganizationContext"/> that <c>AppDbContext</c>
    /// filters and guards writes by, and <see cref="AccountLookup"/> for sign-in. In a
    /// request it comes from the authenticated <c>org_id</c> claim
    /// (<see cref="ClaimsOrganizationContext"/>); in a scope the job runner has entered for
    /// a background job (<see cref="JobOrganizationScope"/>), it is that job's organization.
    /// </summary>
    public static IServiceCollection AddOrganizationTenancy(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ClaimsOrganizationContext>();
        services.AddScoped<JobOrganizationScope>();
        services.AddScoped<IOrganizationContext>(provider =>
            provider.GetRequiredService<JobOrganizationScope>().OrganizationContext
            ?? provider.GetRequiredService<ClaimsOrganizationContext>());
        services.AddScoped<AccountLookup>();
        return services;
    }
}
