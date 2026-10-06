using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Databases;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Application.Organizations;

namespace SmartAgri.Api.Organizations;

public static class OrganizationTokenUsageServiceCollectionExtensions
{
    /// <summary>Registers the singleton <see cref="OrganizationTokenUsage"/> (the 30 second cache
    /// lives in it), counting months in <c>Statistics:TimeZone</c> against
    /// <c>PublicChannels:DefaultMonthlyTokenLimit</c> when an organization has no limit of its own.</summary>
    public static IServiceCollection AddOrganizationTokenUsage(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IOrganizationTokenUsageSource, EfOrganizationTokenUsageSource>();
        services.AddSingleton(provider => new OrganizationTokenUsage(
            provider.GetRequiredService<IOrganizationTokenUsageSource>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<StatisticsOptions>>().Value.TryResolve()
                ?? throw new InvalidOperationException("Statistics:TimeZone was validated at startup."),
            provider.GetRequiredService<IOptions<PublicChannelsOptions>>().Value.EffectiveDefaultMonthlyTokenLimit));
        return services;
    }
}
