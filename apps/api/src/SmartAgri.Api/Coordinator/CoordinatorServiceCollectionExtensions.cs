using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Jobs;
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
    /// <see cref="CoordinatorBindings"/>, <see cref="CoordinatorShops"/> (#373), the commands of #374
    /// (<see cref="CoordinatorCommands"/>, <see cref="CoordinatorConversationMemory"/>,
    /// <see cref="CoordinatorCommandInterpreter"/>, <see cref="CoordinatorCommandHandler"/>), carrying them out (#375:
    /// <see cref="CoordinatorCommandActions"/>, the executors of later slices through <see cref="CoordinatorActionExecutors"/>
    /// and <see cref="ExecutorActionModes"/>, <see cref="ExecutingCommandRunner"/>, <see cref="CoordinatorReports"/>,
    /// <see cref="CoordinatorReplyTokens"/>, <see cref="CoordinatorActionButtons"/> and the
    /// <see cref="CoordinatorRunActionJob.Kind"/> job handler) and <see cref="CoordinatorEventHandler"/>. Requires
    /// <c>AddLineMessaging</c> (the LINE client), <c>AddChat</c>, <c>AddOrganizationTokenUsage</c> and
    /// <c>AddBackgroundJobs</c>. Nothing reads the options while the pipeline is built.
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
        services.AddScoped<CoordinatorShops>();
        services.AddScoped<CoordinatorCommands>();
        services.AddSingleton<CoordinatorConversationMemory>();
        services.AddScoped<CoordinatorCommandInterpreter>();
        services.AddScoped<CoordinatorCommandActions>();
        services.AddScoped<CoordinatorActionExecutors>();
        services.TryAddScoped<ICoordinatorActionModes, ExecutorActionModes>();
        services.AddSingleton<CoordinatorReplyTokens>();
        services.AddScoped<CoordinatorReplySender>();
        services.AddScoped<CoordinatorReports>();
        services.TryAddScoped<ICoordinatorCommandRunner, ExecutingCommandRunner>();
        services.AddScoped<CoordinatorActionButtons>();
        services.AddJobHandler<CoordinatorRunActionHandler>(CoordinatorRunActionJob.Kind);
        services.AddScoped<CoordinatorCommandHandler>();
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
