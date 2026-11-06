namespace SmartAgri.Api.Ordering;

public static class OrderingServiceCollectionExtensions
{
    /// <summary>
    /// Registers online ordering (online-ordering design, Slice O1, #397): <see cref="OrderingShops"/>, which the
    /// <c>ordering-shop</c> command imports and shows a shop's ordering settings and menu with, and the anonymous ordering
    /// API (#398, <see cref="OrderingPublicEndpoints"/>). The admin's order handling follows (#399).
    /// </summary>
    public static IServiceCollection AddOnlineOrdering(this IServiceCollection services)
    {
        services.AddScoped<OrderingShops>();
        services.AddScoped<SmartAgri.Infrastructure.Ordering.OrderingLookup>();
        services.AddScoped<OrderingOrders>();
        services.AddScoped<OrderingPublicFlow>();
        return services;
    }
}
