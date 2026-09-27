using System.Diagnostics.Metrics;
using SmartAgri.Domain.Observability;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>
/// Span attribute and metric names for model calls, from the OpenTelemetry GenAI semantic
/// conventions (<c>gen_ai.*</c>), plus SmartAgri's own. Never content, never account names
/// (observability ADR).
/// </summary>
public static class GenAiTelemetry
{
    public const string OperationName = "gen_ai.operation.name";
    public const string ProviderName = "gen_ai.provider.name";
    public const string RequestModel = "gen_ai.request.model";
    public const string ResponseModel = "gen_ai.response.model";
    public const string InputTokens = "gen_ai.usage.input_tokens";
    public const string TokenType = "gen_ai.token.type";
    public const string ServerAddress = "server.address";
    public const string ServerPort = "server.port";
    public const string ErrorType = "error.type";

    /// <summary><c>embed-document</c> or <c>embed-query</c>.</summary>
    public const string Purpose = "smartagri.model_invocation.purpose";

    /// <summary>How many texts one call embedded.</summary>
    public const string InputCount = "smartagri.model_invocation.input_count";

    public const string EmbeddingsOperation = "embeddings";

    public const string DurationInstrument = "gen_ai.client.operation.duration";
    public const string TokenUsageInstrument = "gen_ai.client.token.usage";
}

/// <summary>The two GenAI client histograms (duration, token usage), created once per host from
/// its <see cref="IMeterFactory"/>.</summary>
public sealed class ModelCallMetrics
{
    public ModelCallMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(SmartAgriMeter.Name);
        Duration = meter.CreateHistogram<double>(
            GenAiTelemetry.DurationInstrument,
            unit: "s",
            description: "Duration of model calls.");
        TokenUsage = meter.CreateHistogram<long>(
            GenAiTelemetry.TokenUsageInstrument,
            unit: "{token}",
            description: "Tokens used by model calls, as the provider reported them.");
    }

    public Histogram<double> Duration { get; }

    public Histogram<long> TokenUsage { get; }
}
