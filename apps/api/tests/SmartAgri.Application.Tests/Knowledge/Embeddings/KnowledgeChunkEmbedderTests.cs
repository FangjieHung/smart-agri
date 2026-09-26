using Microsoft.Extensions.AI;
using Shouldly;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Ai;

namespace SmartAgri.Application.Tests.Knowledge.Embeddings;

public class KnowledgeChunkEmbedderTests
{
    private static readonly Guid Uploader = Guid.CreateVersion7();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Chunks_go_in_batches_of_the_configured_size_with_the_document_prefix_and_come_back_in_order()
    {
        var generator = new RecordingGenerator();
        var embedder = new KnowledgeChunkEmbedder(generator, new KnowledgeEmbeddingSettings("e5", "passage: ", "query: ", 2));

        var vectors = await embedder.EmbedDocumentsAsync(["一", "二", "三", "四", "五"], Uploader, CancellationToken);

        generator.Calls.Select(call => call.Inputs).ShouldBe(
        [
            ["passage: 一", "passage: 二"],
            ["passage: 三", "passage: 四"],
            ["passage: 五"],
        ]);
        vectors.Select(vector => vector[0]).ShouldBe([.. new[] { "passage: 一", "passage: 二", "passage: 三", "passage: 四", "passage: 五" }.Select(RecordingGenerator.FirstValue)]);
        generator.Calls.ShouldAllBe(call => call.Attribution == new ModelInvocationAttribution(ModelInvocationPurpose.EmbedDocument, Uploader, null));
        embedder.Model.ShouldBe("e5");
    }

    [Fact]
    public async Task Nothing_to_embed_makes_no_call()
    {
        var generator = new RecordingGenerator();

        (await new KnowledgeChunkEmbedder(generator, Settings()).EmbedDocumentsAsync([], Uploader, CancellationToken)).ShouldBeEmpty();

        generator.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_question_is_one_call_with_the_query_prefix_attributed_to_the_asker_and_assistant()
    {
        var generator = new RecordingGenerator();
        var assistant = Guid.CreateVersion7();

        var vector = await new KnowledgeChunkEmbedder(generator, Settings()).EmbedQueryAsync("幾天內可退貨？", Uploader, assistant, CancellationToken);

        var call = generator.Calls.ShouldHaveSingleItem();
        call.Inputs.ShouldBe(["query: 幾天內可退貨？"]);
        call.Attribution.ShouldBe(new ModelInvocationAttribution(ModelInvocationPurpose.EmbedQuery, Uploader, assistant));
        vector[0].ShouldBe(RecordingGenerator.FirstValue("query: 幾天內可退貨？"));
    }

    [Fact]
    public async Task Any_failure_of_the_call_becomes_the_owners_retry_message_with_the_cause_inside()
    {
        var cause = new HttpRequestException("429 Too Many Requests");
        var embedder = new KnowledgeChunkEmbedder(new RecordingGenerator { Throw = cause }, Settings());

        var exception = await Should.ThrowAsync<KnowledgeEmbeddingException>(() => embedder.EmbedDocumentsAsync(["文字"], Uploader, CancellationToken));

        exception.Message.ShouldBe(KnowledgeProcessingIssues.EmbeddingUnavailable);
        exception.ProviderNotConfigured.ShouldBeFalse();
        exception.InnerException.ShouldBeSameAs(cause);
    }

    [Fact]
    public async Task No_provider_says_an_administrator_must_configure_one()
    {
        var embedder = new KnowledgeChunkEmbedder(new RecordingGenerator { Throw = new EmbeddingProviderNotConfiguredException() }, Settings());

        var exception = await Should.ThrowAsync<KnowledgeEmbeddingException>(() => embedder.EmbedDocumentsAsync(["文字"], Uploader, CancellationToken));

        exception.Message.ShouldBe(KnowledgeProcessingIssues.EmbeddingNotConfigured);
        exception.ProviderNotConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancellation_is_not_turned_into_a_failure()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var embedder = new KnowledgeChunkEmbedder(new RecordingGenerator { Throw = new OperationCanceledException(cancelled.Token) }, Settings());

        await Should.ThrowAsync<OperationCanceledException>(() => embedder.EmbedDocumentsAsync(["文字"], Uploader, cancelled.Token));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("zeros")]
    [InlineData("nan")]
    [InlineData("mixed-dimensions")]
    public async Task An_unusable_answer_is_a_failure_too(string answer)
    {
        var embedder = new KnowledgeChunkEmbedder(new RecordingGenerator { Answer = answer }, Settings());

        var exception = await Should.ThrowAsync<KnowledgeEmbeddingException>(() => embedder.EmbedDocumentsAsync(["一", "二"], Uploader, CancellationToken));

        exception.Message.ShouldBe(KnowledgeProcessingIssues.EmbeddingUnavailable);
        exception.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Settings_refuse_batches_the_providers_do_not_accept()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new KnowledgeEmbeddingSettings("m", "", "", 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new KnowledgeEmbeddingSettings("m", "", "", KnowledgeEmbeddingSettings.MaxBatchSize + 1));
        new KnowledgeEmbeddingSettings("m", "", "", KnowledgeEmbeddingSettings.MaxBatchSize).BatchSize.ShouldBe(2048);
    }

    private static KnowledgeEmbeddingSettings Settings() => new("e5", "passage: ", "query: ", KnowledgeEmbeddingSettings.DefaultBatchSize);

    private sealed record Call(string[] Inputs, ModelInvocationAttribution? Attribution);

    /// <summary>Answers each input with a 3-dimensional vector whose first value identifies it.</summary>
    private sealed class RecordingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<Call> Calls { get; } = [];

        public Exception? Throw { get; init; }

        public string? Answer { get; init; }

        public static float FirstValue(string input) => input.Length + input[^1];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var inputs = values.ToArray();
            Calls.Add(new Call(inputs, ModelInvocationAttribution.From(options)));
            if (Throw is not null)
            {
                return Task.FromException<GeneratedEmbeddings<Embedding<float>>>(Throw);
            }

            var vectors = inputs.Select((input, index) => Answer switch
            {
                "zeros" => new float[3],
                "nan" => [float.NaN, 1, 1],
                "mixed-dimensions" when index == 1 => [1, 1],
                _ => new[] { FirstValue(input), 1, 1 },
            });
            if (Answer == "missing")
            {
                vectors = vectors.Take(inputs.Length - 1);
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(vectors.Select(vector => new Embedding<float>(vector))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
