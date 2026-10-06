using SmartAgri.Api.Jobs;
using SmartAgri.Application.Organizations;

namespace SmartAgri.Api.Organizations;

public static class RetentionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the conversation retention (M6-4, #241): <see cref="RetentionCleanupService"/>, the
    /// <see cref="RetentionCleanupJob.Kind"/> handler, its metrics and the startup
    /// <see cref="RetentionCleanupReconciler"/>. Requires <c>AddBackgroundJobs</c> and the
    /// <c>Statistics</c> options.
    /// </summary>
    public static IServiceCollection AddConversationRetention(this IServiceCollection services)
    {
        services.AddScoped<RetentionCleanupService>();
        services.AddSingleton<RetentionCleanupMetrics>();
        services.AddSingleton<RetentionCleanupReconciler>();
        services.AddHostedService(provider => provider.GetRequiredService<RetentionCleanupReconciler>());
        services.AddJobHandler<RetentionCleanupHandler>(RetentionCleanupJob.Kind);
        return services;
    }
}
