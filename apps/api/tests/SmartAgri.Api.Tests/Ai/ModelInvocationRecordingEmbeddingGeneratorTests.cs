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
/// The recording middleware's rules (M2 plan, Slice 7): one audit row and one span per call,
/// never any content, and no call at all without an attribution and an organization. No host,
/// no database: rows go to an in-memory recorder.
/// </summary>
public sealed class ModelInvocationRecordingEmbeddingGeneratorTests : IDisposable
{
    private const string SecretText = "機密：客戶王小明的退款帳號 012-3456789";

    private static readonly Guid Account = Guid.CreateVersion7();

    private readonly Guid _organization = Guid.CreateVersion7();
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _spans = new();

    public ModelInvocationRecordingEmbeddingGeneratorTests()
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
    public async Task A_call_writes_one_attributed_row_and_one_gen_ai_span_without_any_content()
    {
        var inner = new StubGenerator { InputTokens = 42 };
        var recorder = new MemoryRecorder();
        using var generator = Create(inner, recorder);

        var result = await generator.GenerateAsync([SecretText, "第二段"], Attribution(), CancellationToken);

        result.Count.ShouldBe(2);
        var row = recorder.Rows.ShouldHaveSingleItem();
        row.OrganizationId.ShouldBe(_organization);
        (row.AccountId, row.AssistantId, row.Purpose).ShouldBe(((Guid?)Account, (Guid?)null, ModelInvocationPurpose.EmbedDocument));
        (row.Provider, row.Model, row.InputTokens, row.Succeeded).ShouldBe(("stub", "stub-model", (long?)42, true));
        row.DurationMs.ShouldBeGreaterThanOrEqualTo(0);

        var span = _spans.ShouldHaveSingleItem();
        span.DisplayName.ShouldBe("embeddings stub-model");
        span.Kind.ShouldBe(ActivityKind.Client);
        var tags = span.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value);
        tags[GenAiTelemetry.OperationName].ShouldBe("embeddings");
        tags[GenAiTelemetry.ProviderName].ShouldBe("stub.telemetry");
        tags[GenAiTelemetry.RequestModel].ShouldBe("stub-model");
        tags[GenAiTelemetry.ResponseModel].ShouldBe("stub-model-2026");
        tags[GenAiTelemetry.InputTokens].ShouldBe(42L);
        tags[GenAiTelemetry.Purpose].ShouldBe("embed-document");
        tags[GenAiTelemetry.InputCount].ShouldBe(2);
        tags[GenAiTelemetry.ServerAddress].ShouldBe("stub.example");
        tags[SmartAgriActivitySource.OrganizationIdTag].ShouldBe(_organization.ToString());
        tags.Values.OfType<string>().ShouldNotContain(value => value.Contains("王小明") || value.Contains("第二段"));
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task The_attribution_never_reaches_the_provider()
    {
        var inner = new StubGenerator();
        using var generator = Create(inner, new MemoryRecorder());
        var options = Attribution();
        options.Dimensions = 256;

        await generator.GenerateAsync(["文字"], options, CancellationToken);

        var seen = inner.Options.ShouldHaveSingleItem().ShouldNotBeNull();
        seen.Dimensions.ShouldBe(256);
        seen.AdditionalProperties.ShouldBeNull();
        ModelInvocationAttribution.From(options).ShouldNotBeNull("the caller's options are not changed");
    }

    [Fact]
    public async Task Usage_the_provider_does_not_report_stays_unknown()
    {
        var recorder = new MemoryRecorder();
        using var generator = Create(new StubGenerator { InputTokens = null }, recorder);

        await generator.GenerateAsync(["文字"], Attribution(ModelInvocationPurpose.EmbedQuery), CancellationToken);

        recorder.Rows.ShouldHaveSingleItem().InputTokens.ShouldBeNull();
        _spans.ShouldHaveSingleItem().GetTagItem(GenAiTelemetry.InputTokens).ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_call_is_recorded_as_failed_and_its_own_error_is_rethrown()
    {
        var failure = new HttpRequestException("503 Service Unavailable");
        var recorder = new MemoryRecorder();
        using var generator = Create(new StubGenerator { Throw = failure }, recorder);

        (await Should.ThrowAsync<HttpRequestException>(() => generator.GenerateAsync(["文字"], Attribution(), CancellationToken))).ShouldBeSameAs(failure);

        var row = recorder.Rows.ShouldHaveSingleItem();
        (row.Succeeded, row.InputTokens).ShouldBe((false, (long?)null));
        var span = _spans.ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem(GenAiTelemetry.ErrorType).ShouldBe(typeof(HttpRequestException).FullName);
    }

    [Fact]
    public async Task A_failed_call_that_cannot_be_recorded_still_throws_its_own_error()
    {
        var failure = new HttpRequestException("503");
        using var generator = Create(new StubGenerator { Throw = failure }, new MemoryRecorder { Throw = new InvalidOperationException("database down") });

        (await Should.ThrowAsync<HttpRequestException>(() => generator.GenerateAsync(["文字"], Attribution(), CancellationToken))).ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task A_successful_call_that_cannot_be_recorded_returns_nothing()
    {
        using var generator = Create(new StubGenerator(), new MemoryRecorder { Throw = new InvalidOperationException("database down") });

        (await Should.ThrowAsync<InvalidOperationException>(() => generator.GenerateAsync(["文字"], Attribution(), CancellationToken)))
            .Message.ShouldBe("database down");
    }

    [Fact]
    public async Task No_call_is_made_without_an_attribution_or_an_organization()
    {
        var inner = new StubGenerator();
        var recorder = new MemoryRecorder();

        using (var generator = Create(inner, recorder))
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => generator.GenerateAsync(["文字"], cancellationToken: CancellationToken)))
                .Message.ShouldContain("ModelInvocationAttribution");
            await Should.ThrowAsync<InvalidOperationException>(() => generator.GenerateAsync(["文字"], new EmbeddingGenerationOptions(), CancellationToken));
        }

        using (var generator = Create(inner, recorder, FixedOrganizationContext.None))
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => generator.GenerateAsync(["文字"], Attribution(), CancellationToken)))
                .Message.ShouldContain("organization");
        }

        inner.Options.ShouldBeEmpty();
        recorder.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Disposing_a_wrapper_leaves_the_shared_client_usable()
    {
        var inner = new StubGenerator();
        var provider = Provider(inner);

        Create(provider, new MemoryRecorder()).Dispose();
        using var next = Create(provider, new MemoryRecorder());

        inner.Disposed.ShouldBeFalse();
        (await next.GenerateAsync(["文字"], Attribution(), CancellationToken)).Count.ShouldBe(1);
    }

    [Fact]
    public void It_reports_the_configured_provider_and_model()
    {
        using var generator = Create(new StubGenerator(), new MemoryRecorder());

        var metadata = generator.GetService<EmbeddingGeneratorMetadata>().ShouldNotBeNull();

        (metadata.ProviderName, metadata.DefaultModelId).ShouldBe(("stub", "stub-model"));
    }

    private static EmbeddingGenerationOptions Attribution(ModelInvocationPurpose purpose = ModelInvocationPurpose.EmbedDocument) =>
        new ModelInvocationAttribution(purpose, Account, null).ToEmbeddingOptions();

    private static EmbeddingProvider Provider(StubGenerator inner) =>
        new(inner, "stub", "stub.telemetry", "stub-model", new Uri("https://stub.example/v1"));

    private ModelInvocationRecordingEmbeddingGenerator Create(StubGenerator inner, MemoryRecorder recorder, FixedOrganizationContext? organization = null) =>
        Create(Provider(inner), recorder, organization);

    private ModelInvocationRecordingEmbeddingGenerator Create(EmbeddingProvider provider, MemoryRecorder recorder, FixedOrganizationContext? organization = null) =>
        new(provider, recorder, organization ?? new FixedOrganizationContext(_organization), TimeProvider.System, metrics: null,
            NullLogger<ModelInvocationRecordingEmbeddingGenerator>.Instance);

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

    private sealed class StubGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<EmbeddingGenerationOptions?> Options { get; } = [];

        public long? InputTokens { get; init; } = 7;

        public Exception? Throw { get; init; }

        public bool Disposed { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            if (Throw is not null)
            {
                return Task.FromException<GeneratedEmbeddings<Embedding<float>>>(Throw);
            }

            var embeddings = new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1, 0 }) { ModelId = "stub-model-2026" }))
            {
                Usage = InputTokens is null ? null : new UsageDetails { InputTokenCount = InputTokens },
            };
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }
}
