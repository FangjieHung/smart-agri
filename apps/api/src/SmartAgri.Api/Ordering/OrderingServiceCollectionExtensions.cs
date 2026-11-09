using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SmartAgri.Api.Ordering;

public static class OrderingServiceCollectionExtensions
{
    /// <summary>
    /// Registers online ordering (online-ordering design, Slice O1, #397): <see cref="OrderingShops"/>, which the
    /// <c>ordering-shop</c> command imports and shows a shop's ordering settings and menu with, and the anonymous ordering
    /// API (#398, <see cref="OrderingPublicEndpoints"/>), and the admin's order handling (#399, <see cref="OrderingStaffEndpoints"/>)
    /// with its <see cref="IOrderingOrderOutcomes"/> seam (nothing yet; #400 replaces it).
    /// </summary>
    public static IServiceCollection AddOnlineOrdering(this IServiceCollection services)
    {
        services.AddScoped<OrderingShops>();
        services.AddScoped<SmartAgri.Infrastructure.Ordering.OrderingLookup>();
        services.AddScoped<OrderingOrders>();
        services.AddScoped<OrderingPublicFlow>();
        services.AddScoped<OrderingStaffOrders>();
        services.TryAddScoped<IOrderingOrderOutcomes, NoOrderingOrderOutcomes>();
        return services;
    }
}
