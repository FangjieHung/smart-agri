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
    /// Registers the chat (generation) model from the <c>Ai:Chat</c> section
    /// (<see cref="ChatModelOptions"/>, validated on start — <c>Fake</c> outside
    /// Development/Testing refuses to start), the same shape as <c>AddEmbeddings</c>:
    /// <list type="bullet">
    /// <item>the provider's client (<see cref="ChatClientProvider"/>, singleton);</item>
    /// <item>per scope, <see cref="IChatClient"/>: that client wrapped in
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
        services.AddHostedService<ChatConfigurationReporter>();

        services.AddScoped(provider => CreateClient(
            provider,
            provider.GetRequiredService<IOrganizationContext>(),
            provider.GetRequiredService<IModelInvocationRecorder>()));
        return services;
    }

    /// <summary>
    /// The chat client code calls, for <paramref name="organization"/>: the configured client
    /// wrapped in the recording middleware, or — with no provider configured — the client that
    /// refuses every call (nothing reaches a model, so there is nothing to record).
    /// </summary>
    internal static IChatClient CreateClient(
        IServiceProvider services,
        IOrganizationContext organization,
        IModelInvocationRecorder recorder)
    {
        var provider = services.GetRequiredService<ChatClientProvider>();
        return provider.IsConfigured
            ? new ModelInvocationRecordingChatClient(
                provider,
                recorder,
                organization,
                services.GetRequiredService<TimeProvider>(),
                services.GetService<ModelCallMetrics>(),
                services.GetRequiredService<ILogger<ModelInvocationRecordingChatClient>>())
            : provider.Client;
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

    /// <summary>Says once, at startup, which chat model this deployment uses — or that it has
    /// none, so an operator sees why conversations cannot be answered.</summary>
    private sealed class ChatConfigurationReporter : IHostedService
    {
        private readonly ChatClientProvider _provider;
        private readonly ILogger<ChatConfigurationReporter> _logger;

        public ChatConfigurationReporter(ChatClientProvider provider, ILogger<ChatConfigurationReporter> logger)
        {
            _provider = provider;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_provider.IsConfigured)
            {
                _logger.LogInformation("Chat: provider {Provider}, model {Model}.", _provider.Name, _provider.Model);
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
