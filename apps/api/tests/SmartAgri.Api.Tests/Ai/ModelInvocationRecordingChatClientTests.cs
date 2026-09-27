using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmartAgri.Application.Ai;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Observability;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Ai;

/// <summary>
/// The chat recording middleware's rules (M3 plan, Slice 4; the chat-side twin of
/// <see cref="ModelInvocationRecordingEmbeddingGeneratorTests"/>, whose structure this mirrors):
/// one audit row and one span per call — streaming or not — never any content, and no call at
/// all without an attribution and an organization. No host, no database: rows go to an
/// in-memory recorder.
/// </summary>
public sealed class ModelInvocationRecordingChatClientTests : IDisposable
{
    private const string SecretText = "機密：客戶王小明的退款帳號 012-3456789";

    private static readonly Guid Account = Guid.CreateVersion7();
    private static readonly Guid Assistant = Guid.CreateVersion7();

    private readonly Guid _organization = Guid.CreateVersion7();
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _spans = new();

    public ModelInvocationRecordingChatClientTests()
    {
        // Other test classes may start SmartAgri spans at the same time: keep only this
        // instance's (its organization is unique).
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SmartAgriActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem(SmartAgriActivitySource.OrganizationIdTag) as string == _organization.ToString())
                {
                    _spans.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task A_non_streaming_call_writes_one_attributed_row_and_one_gen_ai_span_without_any_content()
    {
        var inner = new StubChatClient { InputTokens = 42, OutputTokens = 17 };
        var recorder = new MemoryRecorder();
        using var client = Create(inner, recorder);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, SecretText)], Attribution(), CancellationToken);

        response.Text.ShouldBe("stub answer");
        var row = recorder.Rows.ShouldHaveSingleItem();
        row.OrganizationId.ShouldBe(_organization);
        (row.AccountId, row.AssistantId, row.Purpose).ShouldBe(((Guid?)Account, (Guid?)Assistant, ModelInvocationPurpose.GenerateAnswer));
        (row.Provider, row.Model, row.InputTokens, row.OutputTokens, row.Succeeded).ShouldBe(("stub", "stub-model", (long?)42, (long?)17, true));
        row.DurationMs.ShouldBeGreaterThanOrEqualTo(0);

        var span = _spans.ShouldHaveSingleItem();
        span.DisplayName.ShouldBe("chat stub-model");
        span.Kind.ShouldBe(ActivityKind.Client);
        var tags = span.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value);
        tags[GenAiTelemetry.OperationName].ShouldBe("chat");
        tags[GenAiTelemetry.ProviderName].ShouldBe("stub.telemetry");
        tags[GenAiTelemetry.RequestModel].ShouldBe("stub-model");
        tags[GenAiTelemetry.ResponseModel].ShouldBe("stub-model-2026");
        tags[GenAiTelemetry.InputTokens].ShouldBe(42L);
        tags[GenAiTelemetry.OutputTokens].ShouldBe(17L);
        tags[GenAiTelemetry.Purpose].ShouldBe("generate-answer");
        tags[GenAiTelemetry.ServerAddress].ShouldBe("stub.example");
        tags[SmartAgriActivitySource.OrganizationIdTag].ShouldBe(_organization.ToString());
        tags.Values.OfType<string>().ShouldNotContain(value => value.Contains("王小明") || value.Contains("stub answer"));
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task A_streaming_call_writes_one_row_with_usage_taken_from_the_usage_content()
    {
        var inner = new StubChatClient { InputTokens = 10, OutputTokens = 5 };
        var recorder = new MemoryRecorder();
        using var client = Create(inner, recorder);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "文字")], Attribution(), CancellationToken))
        {
            updates.Add(update);
        }

        string.Concat(updates.Select(update => update.Text)).ShouldBe("stub answer");
        var row = recorder.Rows.ShouldHaveSingleItem();
        (row.InputTokens, row.OutputTokens, row.Succeeded).ShouldBe(((long?)10, (long?)5, true));

        var span = _spans.ShouldHaveSingleItem();
        span.GetTagItem(GenAiTelemetry.InputTokens).ShouldBe(10L);
        span.GetTagItem(GenAiTelemetry.OutputTokens).ShouldBe(5L);
    }

    [Fact]
    public async Task Streaming_usage_the_provider_never_sends_stays_unknown_never_estimated()
    {
        var recorder = new MemoryRecorder();
        using var client = Create(new StubChatClient { ReportUsage = false }, recorder);

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "文字")], Attribution(), CancellationToken))
        {
        }

        var row = recorder.Rows.ShouldHaveSingleItem();
        (row.InputTokens, row.OutputTokens).ShouldBe(((long?)null, (long?)null));
        var span = _spans.ShouldHaveSingleItem();
        span.GetTagItem(GenAiTelemetry.InputTokens).ShouldBeNull();
        span.GetTagItem(GenAiTelemetry.OutputTokens).ShouldBeNull();
    }

    [Fact]
    public async Task The_attribution_never_reaches_the_provider()
    {
        var inner = new StubChatClient();
        using var client = Create(inner, new MemoryRecorder());
        var options = Attribution();
        options.MaxOutputTokens = 256;

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "文字")], options, CancellationToken);

        var seen = inner.Options.ShouldHaveSingleItem().ShouldNotBeNull();
        seen.MaxOutputTokens.ShouldBe(256);
        seen.AdditionalProperties.ShouldBeNull();
        ModelInvocationAttribution.From(options).ShouldNotBeNull("the caller's options are not changed");
    }

    [Fact]
    public async Task A_failed_non_streaming_call_is_recorded_as_failed_and_its_own_error_is_rethrown()
    {
        var failure = new HttpRequestException("503 Service Unavailable");
        var recorder = new MemoryRecorder();
        using var client = Create(new StubChatClient { Throw = failure }, recorder);

        (await Should.ThrowAsync<HttpRequestException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "文字")], Attribution(), CancellationToken)))
            .ShouldBeSameAs(failure);

        var row = recorder.Rows.ShouldHaveSingleItem();
        (row.Succeeded, row.InputTokens, row.OutputTokens).ShouldBe((false, (long?)null, (long?)null));
        var span = _spans.ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem(GenAiTelemetry.ErrorType).ShouldBe(typeof(HttpRequestException).FullName);
    }

    [Fact]
    public async Task A_failed_streaming_call_is_recorded_as_failed_and_its_own_error_is_rethrown()
    {
        var failure = new InvalidOperationException("boom midway");
        var recorder = new MemoryRecorder();
        using var client = Create(new StubChatClient { ThrowAfterFirstChunk = failure }, recorder);

        var received = new List<ChatResponseUpdate>();
        (await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "文字")], Attribution(), CancellationToken))
            {
                received.Add(update);
            }
        })).ShouldBeSameAs(failure);

        received.ShouldHaveSingleItem();
        var row = recorder.Rows.ShouldHaveSingleItem();
        row.Succeeded.ShouldBeFalse();
        var span = _spans.ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_streaming_call_cancelled_mid_stream_is_recorded_as_failed()
    {
        using var cancellation = new CancellationTokenSource();
        var recorder = new MemoryRecorder();
        using var client = Create(new StubChatClient { ThrowAfterFirstChunk = new OperationCanceledException() }, recorder);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "文字")], Attribution(), CancellationToken))
            {
            }
        });

        recorder.Rows.ShouldHaveSingleItem().Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failed_call_that_cannot_be_recorded_still_throws_its_own_error()
    {
        var failure = new HttpRequestException("503");
        using var client = Create(new StubChatClient { Throw = failure }, new MemoryRecorder { Throw = new InvalidOperationException("database down") });

        (await Should.ThrowAsync<HttpRequestException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "文字")], Attribution(), CancellationToken)))
            .ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task A_successful_call_that_cannot_be_recorded_returns_nothing()
    {
        using var client = Create(new StubChatClient(), new MemoryRecorder { Throw = new InvalidOperationException("database down") });

        (await Should.ThrowAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "文字")], Attribution(), CancellationToken)))
            .Message.ShouldBe("database down");
    }

    [Fact]
    public async Task No_call_is_made_without_an_attribution_or_an_organization()
    {
        var inner = new StubChatClient();
        var recorder = new MemoryRecorder();

        using (var client = Create(inner, recorder))
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "文字")], cancellationToken: CancellationToken)))
                .Message.ShouldContain("ModelInvocationAttribution");
            await Should.ThrowAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "文字")], new ChatOptions(), CancellationToken));
        }

        using (var client = Create(inner, recorder, FixedOrganizationContext.None))
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "文字")], Attribution(), CancellationToken)))
                .Message.ShouldContain("organization");
        }

        inner.Options.ShouldBeEmpty();
        recorder.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_streaming_call_is_made_without_an_attribution()
    {
        var inner = new StubChatClient();
        using var client = Create(inner, new MemoryRecorder());

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "文字")], cancellationToken: CancellationToken))
            {
            }
        });

        exception.Message.ShouldContain("ModelInvocationAttribution");
        inner.Options.ShouldBeEmpty();
    }

    [Fact]
    public async Task Disposing_a_wrapper_leaves_the_shared_client_usable()
    {
        var inner = new StubChatClient();
        var provider = Provider(inner);

        Create(provider, new MemoryRecorder()).Dispose();
        using var next = Create(provider, new MemoryRecorder());

        inner.Disposed.ShouldBeFalse();
        (await next.GetResponseAsync([new ChatMessage(ChatRole.User, "文字")], Attribution(), CancellationToken)).Text.ShouldBe("stub answer");
    }

    [Fact]
    public void It_reports_the_configured_provider_and_model()
    {
        using var client = Create(new StubChatClient(), new MemoryRecorder());

        var metadata = client.GetService<ChatClientMetadata>().ShouldNotBeNull();

        (metadata.ProviderName, metadata.DefaultModelId).ShouldBe(("stub", "stub-model"));
    }

    private static ChatOptions Attribution(ModelInvocationPurpose purpose = ModelInvocationPurpose.GenerateAnswer) =>
        new ModelInvocationAttribution(purpose, Account, Assistant).ToChatOptions();

    private static ChatClientProvider Provider(StubChatClient inner) =>
        new(inner, "stub", "stub.telemetry", "stub-model", new Uri("https://stub.example/v1"));

    private ModelInvocationRecordingChatClient Create(StubChatClient inner, MemoryRecorder recorder, FixedOrganizationContext? organization = null) =>
        Create(Provider(inner), recorder, organization);

    private ModelInvocationRecordingChatClient Create(ChatClientProvider provider, MemoryRecorder recorder, FixedOrganizationContext? organization = null) =>
        new(provider, recorder, organization ?? new FixedOrganizationContext(_organization), TimeProvider.System, metrics: null,
            NullLogger<ModelInvocationRecordingChatClient>.Instance);

    private sealed class MemoryRecorder : IModelInvocationRecorder
    {
        public ConcurrentQueue<ModelInvocation> Rows { get; } = new();

        public Exception? Throw { get; init; }

        public Task RecordAsync(ModelInvocation invocation, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                return Task.FromException(Throw);
            }

            Rows.Enqueue(invocation);
            return Task.CompletedTask;
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public List<ChatOptions?> Options { get; } = [];

        public long? InputTokens { get; init; } = 7;

        public long? OutputTokens { get; init; } = 3;

        public bool ReportUsage { get; init; } = true;

        public Exception? Throw { get; init; }

        public Exception? ThrowAfterFirstChunk { get; init; }

        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            if (Throw is not null)
            {
                return Task.FromException<ChatResponse>(Throw);
            }

            var message = new ChatMessage(ChatRole.Assistant, "stub answer");
            var response = new ChatResponse(message)
            {
                ModelId = "stub-model-2026",
                Usage = ReportUsage ? new UsageDetails { InputTokenCount = InputTokens, OutputTokenCount = OutputTokens } : null,
            };
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "stub answer") { ModelId = "stub-model-2026" };
            await Task.Yield();

            if (ThrowAfterFirstChunk is not null)
            {
                throw ThrowAfterFirstChunk;
            }

            if (ReportUsage)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = InputTokens, OutputTokenCount = OutputTokens })]);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }
}
