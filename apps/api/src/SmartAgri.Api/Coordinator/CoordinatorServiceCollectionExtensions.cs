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
    /// <see cref="CoordinatorRunActionJob.Kind"/> job handler), LINE 群發 (#376: <see cref="CoordinatorConnections"/>,
    /// <see cref="CoordinatorLineEstimateCache"/>, <see cref="CoordinatorLineBroadcasting"/>, the
    /// <see cref="CoordinatorLineBroadcastExecutor"/> and the card's <see cref="CoordinatorLineBroadcastNotes"/>) and
    /// <see cref="CoordinatorEventHandler"/>, and the FB and Google authorization links (#377:
    /// <see cref="CoordinatorFacebookOptions"/> and <see cref="CoordinatorGoogleOptions"/> validated on start, the
    /// <see cref="CoordinatorOAuthProviders"/> registered by later slices — none here, so neither is configured —,
    /// <see cref="CoordinatorAuthorizationLinks"/>, <see cref="CoordinatorOAuthStates"/>, <see cref="CoordinatorOAuthSelections"/>,
    /// <see cref="CoordinatorAuthorizationFlow"/> and <see cref="CoordinatorReauthorization"/>). Requires <c>AddLineMessaging</c> (the LINE client), <c>AddChat</c>,
    /// <c>AddOrganizationTokenUsage</c>, <c>AddBackgroundJobs</c>, the secret protector and Data Protection, and
    /// <c>PublicChannelsOptions</c>. Nothing reads the options while the pipeline is built.
    /// </summary>
    public static IServiceCollection AddStoreCoordinator(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CoordinatorLineOptions>()
            .Bind(configuration.GetSection(CoordinatorLineOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CoordinatorLineOptions>, CoordinatorLineOptionsValidator>();
        services.AddOptions<CoordinatorFacebookOptions>()
            .Bind(configuration.GetSection(CoordinatorFacebookOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CoordinatorFacebookOptions>, CoordinatorFacebookOptionsValidator>();
        services.AddOptions<CoordinatorGoogleOptions>()
            .Bind(configuration.GetSection(CoordinatorGoogleOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CoordinatorGoogleOptions>, CoordinatorGoogleOptionsValidator>();

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
        services.AddScoped<CoordinatorConnections>();
        services.AddSingleton<CoordinatorLineEstimateCache>();
        services.AddScoped<CoordinatorLineBroadcasting>();
        services.AddScoped<CoordinatorLineBroadcastExecutor>();
        services.AddScoped<ICoordinatorActionExecutor>(provider => provider.GetRequiredService<CoordinatorLineBroadcastExecutor>());
        services.AddScoped<CoordinatorActionExecutors>();
        services.TryAddScoped<ICoordinatorActionModes, ExecutorActionModes>();
        services.TryAddScoped<ICoordinatorActionNotes, CoordinatorLineBroadcastNotes>();
        services.AddSingleton<CoordinatorReplyTokens>();
        services.AddScoped<CoordinatorReplySender>();
        services.AddScoped<CoordinatorReports>();
        services.TryAddScoped<ICoordinatorCommandRunner, ExecutingCommandRunner>();
        services.AddScoped<CoordinatorActionButtons>();
        services.AddJobHandler<CoordinatorRunActionHandler>(CoordinatorRunActionJob.Kind);
        services.AddScoped<CoordinatorCommandHandler>();
        services.AddScoped<CoordinatorEventHandler>();
        services.AddScoped<CoordinatorOAuthProviders>();
        services.AddScoped<CoordinatorAuthorizationLinks>();
        services.AddSingleton<CoordinatorOAuthStates>();
        services.AddSingleton<CoordinatorOAuthSelections>();
        services.AddScoped<CoordinatorAuthorizationFlow>();
        services.TryAddScoped<ICoordinatorReauthorization, CoordinatorReauthorization>();
        services.AddHostedService<CoordinatorWebhookProcessor>();
        return services;
    }

    private sealed class CoordinatorLineOptionsValidator : IValidateOptions<CoordinatorLineOptions>
    {
        public ValidateOptionsResult Validate(string? name, CoordinatorLineOptions options) =>
            options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }

    private sealed class CoordinatorFacebookOptionsValidator : IValidateOptions<CoordinatorFacebookOptions>
    {
        public ValidateOptionsResult Validate(string? name, CoordinatorFacebookOptions options) =>
            options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }

    private sealed class CoordinatorGoogleOptionsValidator : IValidateOptions<CoordinatorGoogleOptions>
    {
        public ValidateOptionsResult Validate(string? name, CoordinatorGoogleOptions options) =>
            options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
