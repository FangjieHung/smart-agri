using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the request-scoped <see cref="IOrganizationContext"/> (from the
    /// authenticated <c>org_id</c> claim) that <c>AppDbContext</c> filters and guards
    /// writes by, and <see cref="AccountLookup"/> for sign-in.
    /// </summary>
    public static IServiceCollection AddOrganizationTenancy(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IOrganizationContext, ClaimsOrganizationContext>();
        services.AddScoped<AccountLookup>();
        return services;
    }
}
