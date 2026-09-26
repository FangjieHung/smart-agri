using Microsoft.Extensions.Options;
using SmartAgri.Api.Jobs;
using SmartAgri.Application.Knowledge;

namespace SmartAgri.Api.Knowledge;

public static class KnowledgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers what the knowledge endpoints need beyond the database: <see cref="KnowledgeOptions"/>
    /// from the <c>Knowledge</c> section (validated on start), and the handler of
    /// <see cref="ProcessKnowledgeVersionJob.Kind"/> jobs. Requires <c>AddBackgroundJobs</c>.
    /// </summary>
    public static IServiceCollection AddKnowledge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KnowledgeOptions>()
            .Bind(configuration.GetSection(KnowledgeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<KnowledgeOptions>, KnowledgeOptionsValidator>();

        // TEMPORARY (M2 Slice 5, #39): replaced by the real processing handler in Slice 6 (#40).
        services.AddJobHandler<PlaceholderProcessVersionHandler>(ProcessKnowledgeVersionJob.Kind);
        return services;
    }

    private sealed class KnowledgeOptionsValidator : IValidateOptions<KnowledgeOptions>
    {
        public ValidateOptionsResult Validate(string? name, KnowledgeOptions options) =>
            options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
