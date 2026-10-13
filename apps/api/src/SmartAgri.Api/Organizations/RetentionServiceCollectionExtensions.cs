using SmartAgri.Api.Jobs;
using SmartAgri.Application.Organizations;

namespace SmartAgri.Api.Organizations;

public static class RetentionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the conversation retention (M6-4, #241): <see cref="RetentionCleanupService"/>, the
    /// <see cref="RetentionCleanupJob.Kind"/> handler, its metrics and the startup
    /// <see cref="RetentionCleanupReconciler"/>. Requires <c>AddBackgroundJobs</c> and the
    /// <c>Statistics</c> options. Also the daily <see cref="HandoffRetentionSweep"/> of handled 轉真人
    /// notifications (#362), which uses the same service and calendar for every organization.
    /// </summary>
    public static IServiceCollection AddConversationRetention(this IServiceCollection services)
    {
        services.AddScoped<RetentionCleanupService>();
        services.AddSingleton<RetentionCleanupMetrics>();
        services.AddSingleton<RetentionCleanupReconciler>();
        services.AddHostedService(provider => provider.GetRequiredService<RetentionCleanupReconciler>());
        services.AddJobHandler<RetentionCleanupHandler>(RetentionCleanupJob.Kind);
        services.AddSingleton<HandoffRetentionSweep>();
        services.AddHostedService(provider => provider.GetRequiredService<HandoffRetentionSweep>());
        return services;
    }
}
