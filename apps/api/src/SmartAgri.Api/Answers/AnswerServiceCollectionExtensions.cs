using SmartAgri.Application.Answers;
using SmartAgri.Infrastructure.Answers;
using SmartAgri.Infrastructure.Assistants;

namespace SmartAgri.Api.Answers;

public static class AnswerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the answer pipeline (M3 Slice 5): per scope, <see cref="GroundedAnswerService"/>,
    /// the <see cref="IAnswerKnowledgeBases"/> it re-checks sources with, and (M3.5 Slice 6) the
    /// <see cref="IAnswerOutcomeRecorder"/> it logs every confirmed reply through; once per host,
    /// <see cref="GroundedAnswerMetrics"/>. Requires <c>AddKnowledge</c> (the retriever) and
    /// <c>AddChat</c> (the chat client). Conversations (#77) and wizard trial answers (#78)
    /// inject <see cref="GroundedAnswerService"/>.
    /// </summary>
    public static IServiceCollection AddGroundedAnswers(this IServiceCollection services)
    {
        services.AddSingleton<GroundedAnswerMetrics>();
        services.AddScoped<IAnswerKnowledgeBases, EfAnswerKnowledgeBases>();
        services.AddScoped<IAnswerOutcomeRecorder, EfAnswerOutcomeRecorder>();
        services.AddScoped<GroundedAnswerService>();
        return services;
    }
}
