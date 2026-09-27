using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Knowledge.Extraction;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Observability;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// Embedding during processing, against real PostgreSQL (M2 plan, Slice 7; ticket #41): every
/// chunk gets a vector of the configured model, every model call leaves one content-free audit
/// row and one span, and a model that fails is retried by the queue and finally fails the version
/// with the plan's message. Each test builds its own organization; hosts derived from the
/// fixture's (same database, same clock) change the batch size or the provider.
/// </summary>
/// <remarks>Some tests move the shared clock forward past retry backoffs; they read results from
/// the database afterwards, never with the (by then expired) access token.</remarks>
[Trait("Category", TestCategories.Docker)]
public sealed class KnowledgeEmbeddingTests : IClassFixture<AuthHostFixture>
{
    private readonly AuthHostFixture _host;

    public KnowledgeEmbeddingTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Acceptance: vectors of the configured model, one audit row per batch, no text ---------

    [Fact]
    public async Task Every_chunk_gets_a_vector_of_the_configured_model_with_one_content_free_audit_row_and_span_per_batch()
    {
        var spans = new ConcurrentQueue<Activity>();
        await using var host = Derive(builder =>
        {
            builder.UseSetting("Ai:Embedding:BatchSize", "2");
            builder.ConfigureServices(services => services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new SpanCollector(spans))));
        });
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await owner.UploadAsync(KnowledgeFixtures.DeliveryAndPricesXlsx);

        await RunJobsAsync(host);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.KnowledgeDocumentVersions.SingleAsync(version => version.Id == versionId, CancellationToken))
            .ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Ready);
        var chunks = await dbContext.KnowledgeChunks.AsNoTracking().Where(chunk => chunk.VersionId == versionId).ToListAsync(CancellationToken);
        chunks.Count.ShouldBeGreaterThan(3);
        chunks.ShouldAllBe(chunk => !chunk.Excluded && chunk.EmbeddingModel == AuthHostFixture.EmbeddingModel);
        chunks.ShouldAllBe(chunk => chunk.Embedding != null && chunk.Embedding.Length == FakeEmbeddingGenerator.Dimensions);

        // Exactly what processing embedded, so reindex and search agree with it.
        var units = await dbContext.KnowledgeExtractedUnits.AsNoTracking().Where(unit => unit.VersionId == versionId).ToListAsync(CancellationToken);
        var sample = chunks.OrderBy(chunk => chunk.UnitOrdinal).ThenBy(chunk => chunk.Ordinal).First();
        sample.Embedding.ShouldBe(new FakeEmbeddingGenerator(AuthHostFixture.EmbeddingModel).Embed(
            Application.Knowledge.Embeddings.KnowledgeEmbeddingText.For(units.Single(unit => unit.Ordinal == sample.UnitOrdinal).LocationKind, sample.LocationLabel, sample.Text)));

        var invocations = await dbContext.ModelInvocations.AsNoTracking().ToListAsync(CancellationToken);
        invocations.Count.ShouldBe((chunks.Count + 1) / 2, "one call per batch of 2");
        invocations.ShouldAllBe(invocation =>
            invocation.OrganizationId == owner.Organization.Id
            && invocation.AccountId == owner.AccountId
            && invocation.AssistantId == null
            && invocation.Purpose == ModelInvocationPurpose.EmbedDocument
            && invocation.Provider == "fake"
            && invocation.Model == AuthHostFixture.EmbeddingModel
            && invocation.Succeeded
            && invocation.InputTokens > 0);
        invocations.Sum(invocation => invocation.InputTokens!.Value).ShouldBeGreaterThan((long)chunks.Sum(chunk => chunk.Text.Length), "the location labels are embedded too");

        // No document text anywhere in the table: its columns, and every stored value.
        (await dbContext.Database.SqlQueryRaw<string>(
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'ModelInvocations'")
            .ToListAsync(CancellationToken))
            .ShouldBe(["Id", "OrganizationId", "AccountId", "AssistantId", "Purpose", "Provider", "Model", "InputTokens", "DurationMs", "Succeeded", "At"], ignoreOrder: true);
        var rows = await dbContext.Database.SqlQueryRaw<string>(
                "SELECT row_to_json(m)::text AS \"Value\" FROM \"ModelInvocations\" m WHERE m.\"OrganizationId\" = {0}", owner.Organization.Id)
            .ToListAsync(CancellationToken);
        rows.Count.ShouldBe(invocations.Count);
        // Every piece of the chunks' text with a Chinese character in it (digits alone could
        // match a timestamp or an id by chance).
        var fragments = chunks
            .SelectMany(chunk => chunk.Text.Split('\n', '|'))
            .Select(fragment => fragment.Trim())
            .Where(fragment => fragment.Any(character => char.GetUnicodeCategory(character) == UnicodeCategory.OtherLetter))
            .Distinct()
            .ToList();
        fragments.ShouldContain("台北市");
        foreach (var row in rows)
        {
            fragments.ShouldNotContain(fragment => row.Contains(fragment), row);
        }

        // One client span per call, for this organization, with gen_ai attributes.
        var embeddingSpans = spans.Where(span => span.GetTagItem(SmartAgriActivitySource.OrganizationIdTag) as string == owner.Organization.Id.ToString()
            && span.GetTagItem(GenAiTelemetry.OperationName) as string == "embeddings").ToList();
        embeddingSpans.Count.ShouldBe(invocations.Count);
        embeddingSpans.ShouldAllBe(span => span.DisplayName == $"embeddings {AuthHostFixture.EmbeddingModel}"
            && (string?)span.GetTagItem(GenAiTelemetry.ProviderName) == "smartagri.fake"
            && (string?)span.GetTagItem(GenAiTelemetry.RequestModel) == AuthHostFixture.EmbeddingModel
            && span.GetTagItem(GenAiTelemetry.InputTokens) != null);
    }

    [Fact]
    public async Task Excluding_and_including_a_chunk_again_keeps_its_vector_without_calling_the_model()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await owner.UploadAsync(KnowledgeFixtures.ReturnPolicyPdf);
        await RunJobsAsync(_host.Factory);
        var (documentId, chunk) = await FirstChunkAsync(owner, versionId);
        var calls = await InvocationCountAsync(owner);
        var path = $"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/documents/{documentId}/versions/{versionId}/chunks/{chunk.Id}/exclusion";

        (await owner.Spa.PutAsync(path, owner.Token, new { excluded = true })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Spa.PutAsync(path, owner.Token, new { excluded = false })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, after) = await FirstChunkAsync(owner, versionId);
        after.Embedding.ShouldBe(chunk.Embedding);
        after.EmbeddingModel.ShouldBe(chunk.EmbeddingModel);
        (await InvocationCountAsync(owner)).ShouldBe(calls);
    }

    // --- Acceptance: transient failure then success; persistent failure -------------------

    [Fact]
    public async Task A_model_that_fails_twice_is_retried_by_the_queue_and_the_version_ends_ready()
    {
        var model = new ScriptedModel(failures: 2);
        await using var host = Derive(builder => builder.ConfigureServices(services => services.AddSingleton(model.Provider)));
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await owner.UploadAsync(KnowledgeFixtures.ReturnPolicyPdf);

        await RunJobsWithRetriesAsync(host);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var version = await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(row => row.Id == versionId, CancellationToken);
        version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Ready);
        version.Issue.ShouldBeNull();
        var job = await dbContext.BackgroundJobs.AsNoTracking().SingleAsync(CancellationToken);
        (job.Status, job.Attempts).ShouldBe((BackgroundJobStatus.Succeeded, 3));

        var chunks = await dbContext.KnowledgeChunks.AsNoTracking().Where(chunk => chunk.VersionId == versionId).ToListAsync(CancellationToken);
        chunks.Count.ShouldBe(3, "the failed attempts left nothing behind");
        chunks.ShouldAllBe(chunk => chunk.Embedding != null && chunk.EmbeddingModel == AuthHostFixture.EmbeddingModel);
        (await dbContext.ModelInvocations.AsNoTracking().OrderBy(row => row.At).ThenBy(row => row.Id).Select(row => row.Succeeded).ToListAsync(CancellationToken))
            .ShouldBe([false, false, true], "every call is recorded, failed ones too");
    }

    [Fact]
    public async Task A_model_that_keeps_failing_fails_the_version_with_the_plans_message_after_the_last_attempt()
    {
        var model = new ScriptedModel(failures: int.MaxValue);
        await using var host = Derive(builder => builder.ConfigureServices(services => services.AddSingleton(model.Provider)));
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await owner.UploadAsync(KnowledgeFixtures.ReturnPolicyPdf);

        await RunJobsWithRetriesAsync(host);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var version = await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(row => row.Id == versionId, CancellationToken);
        version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Failed);
        version.Issue.ShouldBe("嵌入模型暫時無法使用，請稍後重試");
        var job = await dbContext.BackgroundJobs.AsNoTracking().SingleAsync(CancellationToken);
        (job.Status, job.Attempts).ShouldBe((BackgroundJobStatus.Failed, job.MaxAttempts));
        job.MaxAttempts.ShouldBeGreaterThan(1);
        job.LastError.ShouldBe(KnowledgeProcessingIssues.EmbeddingUnavailable);

        (await dbContext.KnowledgeChunks.CountAsync(chunk => chunk.VersionId == versionId, CancellationToken)).ShouldBe(0);
        var invocations = await dbContext.ModelInvocations.AsNoTracking().ToListAsync(CancellationToken);
        invocations.Count.ShouldBe(job.MaxAttempts);
        invocations.ShouldAllBe(invocation => !invocation.Succeeded && invocation.InputTokens == null && invocation.AccountId == owner.AccountId);
    }

    [Fact]
    public async Task Without_an_embedding_provider_the_app_runs_and_the_version_fails_asking_for_an_administrator()
    {
        await using var host = Derive(builder => builder.UseSetting("Ai:Embedding:Provider", string.Empty));
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await owner.UploadAsync(KnowledgeFixtures.ReturnPolicyPdf);

        await RunJobsWithRetriesAsync(host);

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var version = await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(row => row.Id == versionId, CancellationToken);
        version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Failed);
        version.Issue.ShouldBe(KnowledgeProcessingIssues.EmbeddingNotConfigured);
        (await dbContext.BackgroundJobs.AsNoTracking().SingleAsync(CancellationToken)).Attempts.ShouldBeGreaterThan(1);
        (await dbContext.ModelInvocations.CountAsync(CancellationToken)).ShouldBe(0, "no model was called");
    }

    // --- Helpers -------------------------------------------------------------------------

    private WebApplicationFactory<Program> Derive(Action<IWebHostBuilder> configure) => _host.Factory.WithWebHostBuilder(configure);

    private static Task RunJobsAsync(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    /// <summary>Runs every job to the end, moving the clock past each retry's backoff.</summary>
    private async Task RunJobsWithRetriesAsync(WebApplicationFactory<Program> host)
    {
        for (var round = 0; round < 8; round++)
        {
            await RunJobsAsync(host);
            _host.Clock.Advance(TimeSpan.FromHours(1));
        }
    }

    private async Task<(Guid DocumentId, KnowledgeChunk Chunk)> FirstChunkAsync(KnowledgeTestOwner owner, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var chunk = await dbContext.KnowledgeChunks.AsNoTracking()
            .Where(row => row.VersionId == versionId)
            .OrderBy(row => row.UnitOrdinal).ThenBy(row => row.Ordinal)
            .FirstAsync(CancellationToken);
        return (chunk.DocumentId, chunk);
    }

    private async Task<int> InvocationCountAsync(KnowledgeTestOwner owner)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.ModelInvocations.CountAsync(CancellationToken);
    }

    /// <summary>The <c>Fake</c> model, failing its first <c>failures</c> calls like an overloaded provider.</summary>
    private sealed class ScriptedModel : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly FakeEmbeddingGenerator _fake = new(AuthHostFixture.EmbeddingModel);
        private int _remainingFailures;

        public ScriptedModel(int failures)
        {
            _remainingFailures = failures;
            Provider = new EmbeddingProvider(this, "fake", "smartagri.fake", AuthHostFixture.EmbeddingModel, endpoint: null);
        }

        public EmbeddingProvider Provider { get; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Interlocked.Decrement(ref _remainingFailures) >= 0
                ? Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new HttpRequestException("429 Too Many Requests", null, HttpStatusCode.TooManyRequests))
                : _fake.GenerateAsync(values, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Keeps finished spans; spans end on any thread, so the sink is thread-safe.</summary>
    private sealed class SpanCollector(ConcurrentQueue<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => sink.Enqueue(data);
    }
}
