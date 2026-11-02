using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Coordinator;
using SmartAgri.Infrastructure.Coordinator;

namespace SmartAgri.Api.Coordinator;

public static class CoordinatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the 統籌助理 (store-coordinator design §5 Slice 1, #372): <see cref="CoordinatorLineOptions"/>
    /// from <c>Coordinator:Line</c> (validated on start: both values or neither), its webhook's
    /// <see cref="CoordinatorWebhookDeduplicator"/>, <see cref="CoordinatorWebhookQueue"/> and the
    /// <see cref="CoordinatorWebhookProcessor"/> that drains it, <see cref="CoordinatorBotIdentity"/>,
    /// <see cref="CoordinatorBindingThrottle"/>, <see cref="CoordinatorLookup"/>,
    /// <see cref="CoordinatorBindings"/> and <see cref="CoordinatorEventHandler"/>. Requires
    /// <c>AddLineMessaging</c> (the LINE client). Nothing reads the options while the pipeline is built.
    /// </summary>
    public static IServiceCollection AddStoreCoordinator(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CoordinatorLineOptions>()
            .Bind(configuration.GetSection(CoordinatorLineOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CoordinatorLineOptions>, CoordinatorLineOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new CoordinatorWebhookDeduplicator(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<CoordinatorWebhookQueue>();
        services.AddSingleton<CoordinatorBotIdentity>();
        services.AddSingleton(provider => new CoordinatorBindingThrottle(provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<CoordinatorLookup>();
        services.AddScoped<CoordinatorBindings>();
        services.AddScoped<CoordinatorEventHandler>();
        services.AddHostedService<CoordinatorWebhookProcessor>();
        return services;
    }

    private sealed class CoordinatorLineOptionsValidator : IValidateOptions<CoordinatorLineOptions>
    {
        public ValidateOptionsResult Validate(string? name, CoordinatorLineOptions options) =>
            options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
