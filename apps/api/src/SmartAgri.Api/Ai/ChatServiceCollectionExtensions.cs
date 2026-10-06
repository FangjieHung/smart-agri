using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Ai;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Ai;

public static class ChatServiceCollectionExtensions
{
    /// <summary>
    /// Registers the chat (generation) models from the <c>Ai:Chat</c> section
    /// (<see cref="ChatModelOptions"/>, validated on start — <c>Fake</c> outside
    /// Development/Testing refuses to start, in <c>Ai:Chat</c> or any entry of <c>Ai:Chat:Models</c>),
    /// the same shape as <c>AddEmbeddings</c>:
    /// <list type="bullet">
    /// <item>the deployment default's client (<see cref="ChatClientProvider"/>, singleton, from
    /// <c>Ai:Chat</c> itself; the evaluation commands use it directly);</item>
    /// <item>every model the deployment offers (<see cref="ChatModelCatalog"/>, singleton: that
    /// default, then one client per entry of <c>Ai:Chat:Models</c>);</item>
    /// <item>per scope, which of them the scope's organization uses
    /// (<see cref="IOrganizationChatModelResolver"/>);</item>
    /// <item>per scope, <see cref="IChatClient"/>: the organization's model's client wrapped in
    /// <see cref="ModelInvocationRecordingChatClient"/> for the scope's organization (the only
    /// way code reaches a model) — or, unconfigured, the client that throws
    /// <see cref="ChatGenerationException"/> before reaching any provider.</item>
    /// </list>
    /// Requires <c>AddOrganizationTenancy</c> and <c>AddEmbeddings</c> (shares
    /// <see cref="ModelCallMetrics"/> and <see cref="IModelInvocationRecorder"/> with it).
    /// </summary>
    public static IServiceCollection AddChat(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ChatModelOptions>()
            .Bind(configuration.GetSection(ChatModelOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ChatModelOptions>, ChatModelOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(provider => ChatClientProvider.Create(provider.GetRequiredService<IOptions<ChatModelOptions>>().Value));
        services.AddSingleton(provider => ChatModelCatalog.Create(
            provider.GetRequiredService<IOptions<ChatModelOptions>>().Value,
            provider.GetRequiredService<ChatClientProvider>()));
        services.AddHostedService<ChatConfigurationReporter>();

        services.AddScoped<IOrganizationChatModelResolver, OrganizationChatModelResolver>();
        services.AddScoped(provider => CreateClient(
            provider,
            provider.GetRequiredService<IOrganizationContext>(),
            provider.GetRequiredService<IModelInvocationRecorder>()));
        return services;
    }

    /// <summary>
    /// The chat client code calls, for <paramref name="organization"/>: the client of the model the
    /// organization resolves to (<see cref="IOrganizationChatModelResolver"/>, asked before the first
    /// call) wrapped in the recording middleware, or — with no provider configured — the client that
    /// refuses every call (nothing reaches a model, so there is nothing to record).
    /// </summary>
    internal static IChatClient CreateClient(
        IServiceProvider services,
        IOrganizationContext organization,
        IModelInvocationRecorder recorder)
    {
        var catalog = services.GetRequiredService<ChatModelCatalog>();
        if (!catalog.IsConfigured)
        {
            return catalog.DeploymentDefault.Provider.Client;
        }

        var clock = services.GetRequiredService<TimeProvider>();
        var metrics = services.GetService<ModelCallMetrics>();
        var logger = services.GetRequiredService<ILogger<ModelInvocationRecordingChatClient>>();
        return new OrganizationChatClient(
            services.GetRequiredService<IOrganizationChatModelResolver>(),
            entry => new ModelInvocationRecordingChatClient(entry.Provider, recorder, organization, clock, metrics, logger));
    }

    private sealed class ChatModelOptionsValidator : IValidateOptions<ChatModelOptions>
    {
        private readonly IHostEnvironment _environment;

        public ChatModelOptionsValidator(IHostEnvironment environment)
        {
            _environment = environment;
        }

        public ValidateOptionsResult Validate(string? name, ChatModelOptions options) =>
            options.Validate(_environment.EnvironmentName) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }

    /// <summary>Says once, at startup, which chat models this deployment offers (id, provider and
    /// model of each, the default first; never a key) — or that it has none, so an operator sees
    /// why conversations cannot be answered.</summary>
    private sealed class ChatConfigurationReporter : IHostedService
    {
        private readonly ChatModelCatalog _catalog;
        private readonly ILogger<ChatConfigurationReporter> _logger;

        public ChatConfigurationReporter(ChatModelCatalog catalog, ILogger<ChatConfigurationReporter> logger)
        {
            _catalog = catalog;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_catalog.IsConfigured)
            {
                foreach (var entry in _catalog.Entries)
                {
                    _logger.LogInformation(
                        "Chat: model {Id}{Default}: provider {Provider}, model {Model}.",
                        entry.Id,
                        entry.IsDeploymentDefault ? " (deployment default)" : string.Empty,
                        entry.Provider.Name,
                        entry.Model);
                }
            }
            else
            {
                _logger.LogWarning(
                    "No chat provider is configured (Ai:Chat:Provider): conversations cannot be answered until one is set.");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
