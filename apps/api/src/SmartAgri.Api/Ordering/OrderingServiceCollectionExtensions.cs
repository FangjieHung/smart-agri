using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartAgri.Api.Jobs;
using SmartAgri.Application.Ordering;

namespace SmartAgri.Api.Ordering;

public static class OrderingServiceCollectionExtensions
{
    /// <summary>
    /// Registers online ordering (online-ordering design, Slice O1, #397): <see cref="OrderingShops"/>, which the
    /// <c>ordering-shop</c> command imports and shows a shop's ordering settings and menu with, and the anonymous ordering
    /// API (#398, <see cref="OrderingPublicEndpoints"/>), and the admin's order handling (#399, <see cref="OrderingStaffEndpoints"/>)
    /// with its <see cref="IOrderingOrderOutcomes"/> seam, which Emails the customer (#400,
    /// <see cref="EmailOrderingOrderOutcomes"/>), and the <see cref="OrderingExpireOrderJob.Kind"/> and
    /// <see cref="OrderingOrderEmailJob.Kind"/> job handlers, and dine-in's tables and sessions (#402,
    /// <see cref="OrderingDineInTables"/>). Requires <c>AddBackgroundJobs</c> and the Email sender.
    /// </summary>
    public static IServiceCollection AddOnlineOrdering(this IServiceCollection services)
    {
        services.AddScoped<OrderingShops>();
        services.AddScoped<SmartAgri.Infrastructure.Ordering.OrderingLookup>();
        services.AddScoped<OrderingOrders>();
        services.AddScoped<OrderingPublicFlow>();
        services.AddScoped<OrderingStaffOrders>();
        services.AddScoped<OrderingDineInTables>();
        services.TryAddScoped<IOrderingOrderOutcomes, EmailOrderingOrderOutcomes>();
        services.AddJobHandler<OrderingExpireOrderHandler>(OrderingExpireOrderJob.Kind);
        services.AddJobHandler<OrderingOrderEmailHandler>(OrderingOrderEmailJob.Kind);
        return services;
    }
}
