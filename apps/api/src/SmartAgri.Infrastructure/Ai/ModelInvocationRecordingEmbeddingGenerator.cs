using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SmartAgri.Application.Ai;
using SmartAgri.Domain;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Observability;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Infrastructure.Ai;

/// <summary>
/// <c>Microsoft.Extensions.AI</c> middleware every embedding call goes through (M2 plan,
/// Slice 7; llm-providers and observability ADRs): writes one <see cref="ModelInvocation"/> per
/// call — organization, account, assistant, purpose, provider, model, duration, input tokens when
/// the provider reports them, success — and emits one client span with <c>gen_ai.*</c>
/// attributes plus <c>smartagri.organization_id</c>, and the GenAI duration and token metrics.
/// No input or output ever goes into any of them.
/// </summary>
/// <remarks>
/// <para>
/// Fails closed: a call without a <see cref="ModelInvocationAttribution"/>, or made where there
/// is no current organization, is refused before it reaches the provider, and a successful call
/// that cannot be recorded throws rather than return unaudited vectors. A failed call is recorded
/// (<c>Succeeded = false</c>) and its own exception rethrown.
/// </para>
/// <para>
/// One instance per organization context (a DI scope, or one per organization in <c>reindex</c>);
/// the provider's client it wraps is a shared singleton, so disposing this never disposes it.
/// </para>
/// </remarks>
public sealed class ModelInvocationRecordingEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    private readonly EmbeddingProvider _provider;
    private readonly IModelInvocationRecorder _recorder;
    private readonly IOrganizationContext _organization;
    private readonly TimeProvider _clock;
    private readonly ModelCallMetrics? _metrics;
    private readonly ILogger _logger;

    public ModelInvocationRecordingEmbeddingGenerator(
        EmbeddingProvider provider,
        IModelInvocationRecorder recorder,
        IOrganizationContext organization,
        TimeProvider clock,
        ModelCallMetrics? metrics,
        ILogger<ModelInvocationRecordingEmbeddingGenerator> logger)
        : base((provider ?? throw new ArgumentNullException(nameof(provider))).Generator)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _provider = provider;
        _recorder = recorder;
        _organization = organization;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
    }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var attribution = ModelInvocationAttribution.From(options)
            ?? throw new InvalidOperationException(
                "Every model call must say what it is for: pass a ModelInvocationAttribution in the options (ModelInvocationAttribution.ToEmbeddingOptions).");
        var organizationId = _organization.OrganizationId
            ?? throw new InvalidOperationException("A model call is recorded against the current organization, and there is none.");
        var inputs = values as IReadOnlyCollection<string> ?? [.. values];

        using var activity = SmartAgriActivitySource.Instance.StartActivity($"{GenAiTelemetry.EmbeddingsOperation} {_provider.Model}", ActivityKind.Client);
        var tags = new TagList
        {
            { GenAiTelemetry.OperationName, GenAiTelemetry.EmbeddingsOperation },
            { GenAiTelemetry.ProviderName, _provider.TelemetryName },
            { GenAiTelemetry.RequestModel, _provider.Model },
        };
        if (activity is not null)
        {
            foreach (var tag in tags)
            {
                activity.SetTag(tag.Key, tag.Value);
            }

            activity.SetTag(SmartAgriActivitySource.OrganizationIdTag, organizationId.ToString());
            activity.SetTag(GenAiTelemetry.Purpose, WireNames<ModelInvocationPurpose>.ToWire(attribution.Purpose));
            activity.SetTag(GenAiTelemetry.InputCount, inputs.Count);
            if (_provider.Endpoint is { } endpoint)
            {
                activity.SetTag(GenAiTelemetry.ServerAddress, endpoint.Host);
                activity.SetTag(GenAiTelemetry.ServerPort, endpoint.Port);
            }
        }

        var startedAt = _clock.GetUtcNow();
        var started = _clock.GetTimestamp();
        GeneratedEmbeddings<Embedding<float>>? generated = null;
        Exception? failure = null;
        try
        {
            generated = await base.GenerateAsync(inputs, ModelInvocationAttribution.Strip(options), cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var elapsed = _clock.GetElapsedTime(started);
        var inputTokens = generated?.Usage?.InputTokenCount;
        if (failure is not null)
        {
            var errorType = failure.GetType().FullName ?? failure.GetType().Name;
            tags.Add(GenAiTelemetry.ErrorType, errorType);
            activity?.SetTag(GenAiTelemetry.ErrorType, errorType);
            activity?.SetStatus(ActivityStatusCode.Error, failure.GetType().Name);
        }
        else if (activity is not null)
        {
            if (inputTokens is { } reported)
            {
                activity.SetTag(GenAiTelemetry.InputTokens, reported);
            }

            if (generated!.Count > 0 && generated[0].ModelId is { Length: > 0 } responseModel)
            {
                activity.SetTag(GenAiTelemetry.ResponseModel, responseModel);
            }
        }

        _metrics?.Duration.Record(elapsed.TotalSeconds, tags);
        if (inputTokens is { } tokens)
        {
            var tokenTags = tags;
            tokenTags.Add(GenAiTelemetry.TokenType, "input");
            _metrics?.TokenUsage.Record(tokens, tokenTags);
        }

        var invocation = ModelInvocation.Record(
            organizationId,
            attribution.AccountId,
            attribution.AssistantId,
            attribution.Purpose,
            _provider.Name,
            _provider.Model,
            inputTokens,
            (long)elapsed.TotalMilliseconds,
            succeeded: failure is null,
            startedAt);
        try
        {
            // Not cancelled with the caller: the call has been made either way.
            await _recorder.RecordAsync(invocation, CancellationToken.None);
        }
        catch (Exception recordingFailure) when (failure is not null)
        {
            _logger.LogError(recordingFailure, "A failed {Provider} embedding call could not be recorded.", _provider.Name);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return generated!;
    }

    /// <summary>Returns this middleware's own <see cref="EmbeddingGeneratorMetadata"/> when asked
    /// (so callers see the configured provider and model), otherwise what the wrapped client offers.</summary>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata)
            ? new EmbeddingGeneratorMetadata(_provider.Name, _provider.Endpoint, _provider.Model)
            : base.GetService(serviceType, serviceKey);
    }

    /// <summary>The wrapped client is the host's singleton; it outlives every wrapper.</summary>
    protected override void Dispose(bool disposing)
    {
    }
}
