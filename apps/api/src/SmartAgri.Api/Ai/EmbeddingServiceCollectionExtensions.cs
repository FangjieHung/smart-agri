using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Knowledge;

namespace SmartAgri.Api.Ai;

public static class EmbeddingServiceCollectionExtensions
{
    /// <summary>
    /// Registers embeddings and vector search (M2 plan, Slice 7) from the <c>Ai:Embedding</c>
    /// section (<see cref="EmbeddingOptions"/>, validated on start — <c>Fake</c> outside
    /// Development/Testing refuses to start):
    /// <list type="bullet">
    /// <item>the provider's client (<see cref="EmbeddingProvider"/>, singleton);</item>
    /// <item>per scope, <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>: that client wrapped
    /// in <see cref="ModelInvocationRecordingEmbeddingGenerator"/> for the scope's organization
    /// (the only way code reaches a model), and <see cref="KnowledgeChunkEmbedder"/>;</item>
    /// <item>per scope, <see cref="VectorStoreCollection{TKey,TRecord}"/> of
    /// <see cref="KnowledgeChunk"/> on the scope's <see cref="AppDbContext"/>, searching the
    /// configured model's vectors.</item>
    /// </list>
    /// Requires <c>AddOrganizationTenancy</c> and the <see cref="AppDbContext"/>.
    /// </summary>
    public static IServiceCollection AddEmbeddings(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmbeddingOptions>, EmbeddingOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(provider => EmbeddingProvider.Create(provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value));
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value.ToSettings());
        services.AddSingleton<ModelCallMetrics>();
        services.AddHostedService<EmbeddingConfigurationReporter>();

        services.AddScoped<IModelInvocationRecorder, EfModelInvocationRecorder>();
        services.AddScoped(provider => CreateGenerator(
            provider,
            provider.GetRequiredService<IOrganizationContext>(),
            provider.GetRequiredService<IModelInvocationRecorder>()));
        services.AddScoped<KnowledgeChunkEmbedder>();
        services.AddScoped<VectorStoreCollection<Guid, KnowledgeChunk>>(provider => new KnowledgeChunkVectorCollection(
            provider.GetRequiredService<AppDbContext>(),
            provider.GetRequiredService<KnowledgeEmbeddingSettings>().Model));
        return services;
    }

    /// <summary>
    /// The embedding generator code calls, for <paramref name="organization"/>: the configured
    /// client wrapped in the recording middleware, or — with no provider configured — the client
    /// that refuses every call (nothing reaches a model, so there is nothing to record).
    /// <c>reindex</c> builds one per organization with this.
    /// </summary>
    internal static IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(
        IServiceProvider services,
        IOrganizationContext organization,
        IModelInvocationRecorder recorder)
    {
        var provider = services.GetRequiredService<EmbeddingProvider>();
        return provider.IsConfigured
            ? new ModelInvocationRecordingEmbeddingGenerator(
                provider,
                recorder,
                organization,
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<ModelCallMetrics>(),
                services.GetRequiredService<ILogger<ModelInvocationRecordingEmbeddingGenerator>>())
            : provider.Generator;
    }

    private sealed class EmbeddingOptionsValidator : IValidateOptions<EmbeddingOptions>
    {
        private readonly IHostEnvironment _environment;

        public EmbeddingOptionsValidator(IHostEnvironment environment)
        {
            _environment = environment;
        }

        public ValidateOptionsResult Validate(string? name, EmbeddingOptions options) =>
            options.Validate(_environment.EnvironmentName) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }

    /// <summary>Says once, at startup, which embedding model this deployment uses — or that it
    /// has none, so an operator sees why uploads end up failed.</summary>
    private sealed class EmbeddingConfigurationReporter : IHostedService
    {
        private readonly EmbeddingProvider _provider;
        private readonly ILogger<EmbeddingConfigurationReporter> _logger;

        public EmbeddingConfigurationReporter(EmbeddingProvider provider, ILogger<EmbeddingConfigurationReporter> logger)
        {
            _provider = provider;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_provider.IsConfigured)
            {
                _logger.LogInformation("Embeddings: provider {Provider}, model {Model}.", _provider.Name, _provider.Model);
            }
            else
            {
                _logger.LogWarning(
                    "No embedding provider is configured (Ai:Embedding:Provider): uploaded documents cannot be processed until one is set.");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
