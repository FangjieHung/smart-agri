using Microsoft.Extensions.Options;
using SmartAgri.Api.Jobs;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Infrastructure.Knowledge.Extraction;

namespace SmartAgri.Api.Knowledge;

public static class KnowledgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers what the knowledge endpoints need beyond the database: <see cref="KnowledgeOptions"/>
    /// from the <c>Knowledge</c> section (validated on start), the text extractors, and the
    /// handler of <see cref="ProcessKnowledgeVersionJob.Kind"/> jobs. Requires <c>AddBackgroundJobs</c>.
    /// </summary>
    public static IServiceCollection AddKnowledge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KnowledgeOptions>()
            .Bind(configuration.GetSection(KnowledgeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<KnowledgeOptions>, KnowledgeOptionsValidator>();

        // Text extraction (Slice 6): exactly one extractor per KnowledgeFileFormat. Stateless.
        services.AddSingleton<IDocumentTextExtractor, PdfTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, DocxTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, XlsxTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();
        services.AddJobHandler<ProcessKnowledgeVersionHandler>(ProcessKnowledgeVersionJob.Kind);
        return services;
    }

    private sealed class KnowledgeOptionsValidator : IValidateOptions<KnowledgeOptions>
    {
        public ValidateOptionsResult Validate(string? name, KnowledgeOptions options) =>
            options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
