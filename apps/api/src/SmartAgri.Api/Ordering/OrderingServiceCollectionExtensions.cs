namespace SmartAgri.Api.Ordering;

public static class OrderingServiceCollectionExtensions
{
    /// <summary>
    /// Registers online ordering (online-ordering design, Slice O1, #397): <see cref="OrderingShops"/>, which the
    /// <c>ordering-shop</c> command imports and shows a shop's ordering settings and menu with. Later slices add the public
    /// ordering page (#398) and the admin's order handling (#399).
    /// </summary>
    public static IServiceCollection AddOnlineOrdering(this IServiceCollection services)
    {
        services.AddScoped<OrderingShops>();
        return services;
    }
}
