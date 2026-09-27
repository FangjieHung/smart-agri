using System.Diagnostics;
using System.Runtime.CompilerServices;
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
/// <c>Microsoft.Extensions.AI</c> middleware every chat (generation) call goes through (M3 plan,
/// Slice 4; llm-providers and observability ADRs) — the chat-side twin of
/// <see cref="ModelInvocationRecordingEmbeddingGenerator"/>, whose doc comment this mirrors:
/// writes one <see cref="ModelInvocation"/> per call — organization, account, assistant, purpose
/// (<see cref="ModelInvocationPurpose.GenerateAnswer"/>), provider, model, duration, input and
/// output tokens when the provider reports them, success — and emits one client span with
/// <c>gen_ai.*</c> attributes plus <c>smartagri.organization_id</c>, and the GenAI duration and
/// token metrics. No input or output ever goes into any of them.
/// </summary>
/// <remarks>
/// <para>
/// Fails closed: a call without a <see cref="ModelInvocationAttribution"/>, or made where there
/// is no current organization, is refused before it reaches the provider, and a successful call
/// that cannot be recorded throws rather than return an unaudited answer. A failed call — a
/// provider error, or the caller's <see cref="CancellationToken"/> firing mid-stream — is
/// recorded (<c>Succeeded = false</c>) and its own exception rethrown.
/// </para>
/// <para>
/// Streaming's output tokens come only from a <see cref="UsageContent"/> update the provider
/// actually sends; when none arrives (the call fails before one, or a provider never sends one)
/// <see cref="ModelInvocation.OutputTokens"/> is <see langword="null"/> — never estimated from
/// the text seen so far. One caveat: if the caller disposes the streaming enumerable early
/// (stops enumerating without the token firing an <see cref="OperationCanceledException"/> —
/// e.g. a client that simply stops awaiting <c>MoveNextAsync</c>), the recording code after the
/// loop never runs, same as any other C# iterator's code after an early-abandoned <c>yield</c>
/// loop: nothing is recorded for that call. Every caller in this codebase drives the stream to
/// completion or to a thrown <see cref="OperationCanceledException"/> (the request's own
/// <see cref="CancellationToken"/>), so this is not expected to happen in practice; it is called
/// out here because it is the one gap this middleware cannot close by itself.
/// </para>
/// <para>
/// One instance per organization context (a DI scope); the provider's client it wraps is a
/// shared singleton, so disposing this never disposes it.
/// </para>
/// </remarks>
public sealed class ModelInvocationRecordingChatClient : DelegatingChatClient
{
    private readonly ChatClientProvider _provider;
    private readonly IModelInvocationRecorder _recorder;
    private readonly IOrganizationContext _organization;
    private readonly TimeProvider _clock;
    private readonly ModelCallMetrics? _metrics;
    private readonly ILogger _logger;

    public ModelInvocationRecordingChatClient(
        ChatClientProvider provider,
        IModelInvocationRecorder recorder,
        IOrganizationContext organization,
        TimeProvider clock,
        ModelCallMetrics? metrics,
        ILogger<ModelInvocationRecordingChatClient> logger)
        : base((provider ?? throw new ArgumentNullException(nameof(provider))).Client)
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

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var (attribution, organizationId) = RequireAttributionAndOrganization(options);

        var activity = StartActivity(organizationId, attribution, out var tags);
        try
        {
            var startedAt = _clock.GetUtcNow();
            var started = _clock.GetTimestamp();
            ChatResponse? generated = null;
            Exception? failure = null;
            try
            {
                generated = await base.GetResponseAsync(messages, ModelInvocationAttribution.Strip(options), cancellationToken);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            var elapsed = _clock.GetElapsedTime(started);
            var inputTokens = generated?.Usage?.InputTokenCount;
            var outputTokens = generated?.Usage?.OutputTokenCount;
            if (failure is null && generated!.ModelId is { Length: > 0 } responseModel)
            {
                activity?.SetTag(GenAiTelemetry.ResponseModel, responseModel);
            }

            await FinishAsync(activity, tags, organizationId, attribution, startedAt, elapsed, inputTokens, outputTokens, failure);

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return generated!;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var (attribution, organizationId) = RequireAttributionAndOrganization(options);

        var activity = StartActivity(organizationId, attribution, out var tags);
        try
        {
            var startedAt = _clock.GetUtcNow();
            var started = _clock.GetTimestamp();
            long? inputTokens = null;
            long? outputTokens = null;
            Exception? failure = null;

            var enumerator = base.GetStreamingResponseAsync(messages, ModelInvocationAttribution.Strip(options), cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    ChatResponseUpdate current;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                        {
                            break;
                        }

                        current = enumerator.Current;
                    }
                    catch (Exception exception)
                    {
                        // Includes OperationCanceledException from a cancelled request: a
                        // cancelled stream is still a call that was made, and is still recorded
                        // (Succeeded = false). See the type doc for the one case this cannot
                        // catch: the caller abandoning enumeration without cancelling.
                        failure = exception;
                        break;
                    }

                    foreach (var content in current.Contents)
                    {
                        if (content is UsageContent usage)
                        {
                            inputTokens = usage.Details.InputTokenCount ?? inputTokens;
                            outputTokens = usage.Details.OutputTokenCount ?? outputTokens;
                        }
                    }

                    if (current.ModelId is { Length: > 0 } responseModel)
                    {
                        activity?.SetTag(GenAiTelemetry.ResponseModel, responseModel);
                    }

                    yield return current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            var elapsed = _clock.GetElapsedTime(started);
            await FinishAsync(activity, tags, organizationId, attribution, startedAt, elapsed, inputTokens, outputTokens, failure);

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally
        {
            activity?.Dispose();
        }
    }

    /// <summary>Returns this middleware's own <see cref="ChatClientMetadata"/> when asked (so
    /// callers see the configured provider and model), otherwise what the wrapped client offers.</summary>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata(_provider.Name, _provider.Endpoint, _provider.Model)
            : base.GetService(serviceType, serviceKey);
    }

    /// <summary>The wrapped client is the host's singleton; it outlives every wrapper.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    private (ModelInvocationAttribution Attribution, Guid OrganizationId) RequireAttributionAndOrganization(ChatOptions? options)
    {
        var attribution = ModelInvocationAttribution.From(options)
            ?? throw new InvalidOperationException(
                "Every model call must say what it is for: pass a ModelInvocationAttribution in the options (ModelInvocationAttribution.ToChatOptions).");
        var organizationId = _organization.OrganizationId
            ?? throw new InvalidOperationException("A model call is recorded against the current organization, and there is none.");
        return (attribution, organizationId);
    }

    private Activity? StartActivity(Guid organizationId, ModelInvocationAttribution attribution, out TagList tags)
    {
        var activity = SmartAgriActivitySource.Instance.StartActivity($"{GenAiTelemetry.ChatOperation} {_provider.Model}", ActivityKind.Client);
        tags = new TagList
        {
            { GenAiTelemetry.OperationName, GenAiTelemetry.ChatOperation },
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
            if (_provider.Endpoint is { } endpoint)
            {
                activity.SetTag(GenAiTelemetry.ServerAddress, endpoint.Host);
                activity.SetTag(GenAiTelemetry.ServerPort, endpoint.Port);
            }
        }

        return activity;
    }

    private async Task FinishAsync(
        Activity? activity,
        TagList tags,
        Guid organizationId,
        ModelInvocationAttribution attribution,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        long? inputTokens,
        long? outputTokens,
        Exception? failure)
    {
        if (failure is not null)
        {
            var errorType = failure.GetType().FullName ?? failure.GetType().Name;
            tags.Add(GenAiTelemetry.ErrorType, errorType);
            activity?.SetTag(GenAiTelemetry.ErrorType, errorType);
            activity?.SetStatus(ActivityStatusCode.Error, failure.GetType().Name);
        }
        else
        {
            if (inputTokens is { } input)
            {
                activity?.SetTag(GenAiTelemetry.InputTokens, input);
            }

            if (outputTokens is { } output)
            {
                activity?.SetTag(GenAiTelemetry.OutputTokens, output);
            }
        }

        _metrics?.Duration.Record(elapsed.TotalSeconds, tags);
        if (inputTokens is { } inputCount)
        {
            var inputTags = tags;
            inputTags.Add(GenAiTelemetry.TokenType, "input");
            _metrics?.TokenUsage.Record(inputCount, inputTags);
        }

        if (outputTokens is { } outputCount)
        {
            var outputTags = tags;
            outputTags.Add(GenAiTelemetry.TokenType, "output");
            _metrics?.TokenUsage.Record(outputCount, outputTags);
        }

        var invocation = ModelInvocation.Record(
            organizationId,
            attribution.AccountId,
            attribution.AssistantId,
            attribution.Purpose,
            _provider.Name,
            _provider.Model,
            inputTokens,
            outputTokens,
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
            _logger.LogError(recordingFailure, "A failed {Provider} chat call could not be recorded.", _provider.Name);
        }
    }
}
