using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Knowledge.Extraction;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// <c>POST /api/v1/knowledge-bases/{id}/retrieval-preview</c> against real PostgreSQL (M2 plan
/// Slice 9; ticket #43). Most tests run on <see cref="RetrievalHostFixture.Scripted"/>, whose
/// embedding model gives every text a vector the test chose (<see cref="ScriptedVectors"/>), so
/// every score is known: 0.95 for the pending version's return clause, 0.9 for the archived
/// version's, 0.8 for the effective version's (page 2 of <c>return-policy.pdf</c>) and 0.1 for
/// everything else. Documents go through the real upload, processing, approval and disable
/// endpoints; each test has an organization of its own.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class KnowledgeRetrievalPreviewTests : IClassFixture<KnowledgeRetrievalPreviewTests.RetrievalHostFixture>
{
    private const string BasePath = "/api/v1/knowledge-bases";
    private const string ReturnQuestion = "收到商品幾天內可退貨？";
    private const string UnrelatedQuestion = "營業時間是幾點？";

    private readonly RetrievalHostFixture _host;

    public KnowledgeRetrievalPreviewTests(RetrievalHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Acceptance: the effective version's page first; pending only when asked -------------

    [Fact]
    public async Task The_top_passage_is_the_effective_version_2s_page_2_and_pending_passages_appear_only_when_included_and_labelled()
    {
        var owner = await ScriptedOwnerAsync();
        var policy = await SeedReturnPolicyAsync(owner);

        var preview = await PreviewAsync(owner, ReturnQuestion);

        var top = preview.Passages[0];
        (top.DocumentId, top.DocumentName, top.VersionNumber, top.VersionState, top.LocationLabel).ShouldBe(
            (policy.DocumentId, "退換貨政策.md", 2, "effective", "第 2 頁"));
        top.VersionId.ShouldBe(policy.V2);
        top.Excerpt.ShouldContain("收到商品後七天內可申請退貨");
        top.Excerpt.ShouldBe(await ChunkTextAsync(owner, top.ChunkId), "a short passage is shown whole");
        top.Score.ShouldBe(0.8, 1e-4);
        preview.Passages.ShouldAllBe(passage => passage.VersionNumber == 2 && passage.VersionState == "effective");
        preview.Passages.Skip(1).Select(passage => passage.LocationLabel).ShouldBe(["第 1 頁", "第 3 頁"], ignoreOrder: true, "then the other pages, at 0.1");
        preview.Passages.Select(passage => passage.Score).ShouldBeInOrder(SortDirection.Descending);
        (preview.Threshold, preview.BelowThreshold).ShouldBe((KnowledgeRetrievalSettings.DefaultMinScore, false), "Retrieval:MinScore from appsettings.json");

        var withPending = await PreviewAsync(owner, ReturnQuestion, includePending: true);

        var pending = withPending.Passages[0];
        (pending.VersionNumber, pending.VersionState, pending.VersionId).ShouldBe((3, "pending-review", policy.V3));
        pending.Score.ShouldBe(0.95, 1e-4);
        withPending.Passages.Select(passage => (passage.VersionNumber, passage.VersionState)).Distinct()
            .ShouldBe([(3, "pending-review"), (2, "effective")], ignoreOrder: true);
        withPending.Passages.ShouldContain(passage => passage.ChunkId == top.ChunkId && passage.VersionState == "effective");
        withPending.Passages.ShouldNotContain(passage => passage.VersionNumber == 1, "archived, although it scores 0.9");
    }

    [Fact]
    public async Task All_scores_below_the_threshold_is_below_threshold_without_an_error_and_the_passages_are_still_returned()
    {
        var owner = await ScriptedOwnerAsync();
        await SeedReturnPolicyAsync(owner);
        var delivery = await owner.UploadAsync(KnowledgeFixtures.DeliveryAndPricesXlsx);
        await RunJobsAsync();
        (await ApproveAsync(owner, delivery)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var preview = await PreviewAsync(owner, UnrelatedQuestion);

        preview.BelowThreshold.ShouldBeTrue();
        preview.Threshold.ShouldBe(KnowledgeRetrievalSettings.DefaultMinScore);
        preview.Passages.Count.ShouldBe(KnowledgeRetrievalSettings.DefaultTop, "Retrieval:Top from appsettings.json");
        preview.Passages.ShouldAllBe(passage => passage.Score < KnowledgeRetrievalSettings.DefaultMinScore);
        preview.Passages[0].Score.ShouldBe(0.16, 1e-4, "page 2 is still the nearest");
        preview.Passages.Select(passage => passage.Score).ShouldBeInOrder(SortDirection.Descending);

        var more = await PreviewAsync(owner, UnrelatedQuestion, top: KnowledgeRetrievalSettings.MaxTop);
        more.Passages.Count.ShouldBeGreaterThan(KnowledgeRetrievalSettings.DefaultTop);
        more.BelowThreshold.ShouldBeTrue();
    }

    // --- Acceptance: another organization gets the same 403 as a knowledge base that does not exist

    [Fact]
    public async Task Another_organization_a_non_owner_and_a_missing_knowledge_base_get_the_same_403_before_the_body_is_read()
    {
        var owner = await ScriptedOwnerAsync();
        var stranger = await ScriptedOwnerAsync("別的組織");
        await _host.CreateAccountAsync(owner.Organization, "staff", StaffPassword, AccountRole.SmbAdmin, "同事", AccountPermission.ManageDataSources);
        var colleague = CreateScriptedSpaClient();
        var colleagueToken = (await colleague.SignInAsync(owner.Organization.Code, "staff", StaffPassword)).AccessToken;
        var path = PreviewPath(owner.KnowledgeBaseId);
        var body = new { question = ReturnQuestion };

        var missing = await ResponseFingerprint.FromAsync(await stranger.Spa.PostAsync(PreviewPath(Guid.NewGuid()), stranger.Token, body));
        missing.Status.ShouldBe(HttpStatusCode.Forbidden);
        JsonDocument.Parse(missing.Body).RootElement.GetProperty("reason").GetString().ShouldBe("knowledge-base");

        foreach (var (spa, token, request) in new (SpaClient, string, object)[]
        {
            (stranger.Spa, stranger.Token, body),
            (stranger.Spa, stranger.Token, new { question = " " }),
            (colleague, colleagueToken, body),
        })
        {
            var refused = await ResponseFingerprint.FromAsync(await spa.PostAsync(path, token, request));
            (refused.Status, refused.ContentType, refused.SetsCookie).ShouldBe((missing.Status, missing.ContentType, missing.SetsCookie));
            refused.Body.ShouldBe(missing.Body, "byte for byte");
        }

        (await owner.Spa.PostAsync(path, null, body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await QueryInvocationsAsync(owner)).ShouldBe(0);
        (await QueryInvocationsAsync(stranger)).ShouldBe(0, "nothing is embedded for a refused request");
    }

    // --- The question's model call, validation and embedding failures ---------------------------

    [Fact]
    public async Task The_question_is_one_embed_query_call_recorded_for_the_caller()
    {
        var owner = await ScriptedOwnerAsync();
        await SeedReturnPolicyAsync(owner);
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var before = await dbContext.ModelInvocations.AsNoTracking().Select(invocation => invocation.Id).ToListAsync(CancellationToken);

        await PreviewAsync(owner, ReturnQuestion);

        var added = await dbContext.ModelInvocations.AsNoTracking().Where(invocation => !before.Contains(invocation.Id)).ToListAsync(CancellationToken);
        var call = added.ShouldHaveSingleItem();
        (call.Purpose, call.AccountId, call.AssistantId, call.Model, call.Succeeded).ShouldBe(
            (ModelInvocationPurpose.EmbedQuery, (Guid?)owner.AccountId, (Guid?)null, AuthHostFixture.EmbeddingModel, true));
        call.InputTokens.ShouldBe((long?)ReturnQuestion.Length);
    }

    [Fact]
    public async Task A_blank_or_too_long_question_or_a_top_out_of_range_is_422_and_embeds_nothing()
    {
        var owner = await ScriptedOwnerAsync();
        var path = PreviewPath(owner.KnowledgeBaseId);

        foreach (var (body, field, message) in new (object Body, string Field, string Message)[]
        {
            (new { }, "question", KnowledgeRetrievalRules.QuestionRequiredMessage),
            (new { question = "  \n " }, "question", KnowledgeRetrievalRules.QuestionRequiredMessage),
            (new { question = new string('問', 501) }, "question", KnowledgeRetrievalRules.QuestionTooLongMessage),
            (new { question = ReturnQuestion, top = 0 }, "top", KnowledgeRetrievalRules.TopOutOfRangeMessage),
            (new { question = ReturnQuestion, top = KnowledgeRetrievalSettings.MaxTop + 1 }, "top", KnowledgeRetrievalRules.TopOutOfRangeMessage),
        })
        {
            var response = await owner.Spa.PostAsync(path, owner.Token, body);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, field);
            (await JsonAsync(response)).GetProperty("errors").GetProperty(field)[0].GetString().ShouldBe(message);
        }

        (await QueryInvocationsAsync(owner)).ShouldBe(0);

        // 500 characters is fine; so is an empty knowledge base (nothing found, below threshold).
        var longest = await PreviewAsync(owner, new string('問', 500));
        (longest.Passages.Count, longest.BelowThreshold).ShouldBe((0, true));
    }

    [Fact]
    public async Task A_failing_embedding_model_is_503_embedding_unavailable_and_the_failed_call_is_recorded()
    {
        var owner = await ScriptedOwnerAsync();
        _host.Vectors.FailNextCalls(1);

        var response = await owner.Spa.PostAsync(PreviewPath(owner.KnowledgeBaseId), owner.Token, new { question = ReturnQuestion });

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await JsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe(KnowledgeRetrievalEndpoints.EmbeddingUnavailableReason);
        body.GetProperty("message").GetString().ShouldBe(KnowledgeProcessingIssues.EmbeddingUnavailable);
        body.GetProperty("status").GetInt32().ShouldBe(503);
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var call = (await dbContext.ModelInvocations.AsNoTracking().ToListAsync(CancellationToken)).ShouldHaveSingleItem();
        (call.Purpose, call.Succeeded, call.AccountId).ShouldBe((ModelInvocationPurpose.EmbedQuery, false, (Guid?)owner.AccountId));

        // The next request goes through.
        (await PreviewAsync(owner, ReturnQuestion)).BelowThreshold.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_an_embedding_provider_it_is_503_embedding_not_configured()
    {
        await using var unconfigured = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Ai:Embedding:Provider", string.Empty));
        var owner = await KnowledgeTestOwner.CreateAsync(_host, via: unconfigured);

        var response = await owner.Spa.PostAsync(PreviewPath(owner.KnowledgeBaseId), owner.Token, new { question = ReturnQuestion });

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await JsonAsync(response);
        body.GetProperty("reason").GetString().ShouldBe(KnowledgeRetrievalEndpoints.EmbeddingNotConfiguredReason);
        body.GetProperty("message").GetString().ShouldBe(KnowledgeProcessingIssues.EmbeddingNotConfigured);
    }

    // --- Excluded chunks, disabled documents and archived versions never appear ---------------

    [Fact]
    public async Task Excluded_chunks_disabled_documents_and_archived_versions_never_appear_with_or_without_pending_versions()
    {
        var owner = await ScriptedOwnerAsync();
        var policy = await SeedReturnPolicyAsync(owner);

        // The best effective match, excluded by the owner.
        var page2 = await ChunkIdAsync(owner, policy.V2, "第 2 頁");
        (await owner.Spa.PutAsync(
                $"{BasePath}/{owner.KnowledgeBaseId}/documents/{policy.DocumentId}/versions/{policy.V2}/chunks/{page2}/exclusion", owner.Token, new { excluded = true }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // A document whose approved version and pending version both match well, disabled.
        var (disabledId, disabledV1) = await UploadAsync(owner, "舊版價目與退貨.md", Markdown("舊版價目與退貨", "收到商品後十天內可申請退貨（舊版價目）。"));
        await RunJobsAsync();
        (await ApproveAsync(owner, disabledV1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await UploadVersionAsync(owner, disabledId, "舊版價目與退貨.md", Markdown("舊版價目與退貨（修訂）", "收到商品後十四天內可申請退貨（修訂價目）。"));
        await RunJobsAsync();
        (await owner.Spa.PostAsync($"{BasePath}/{owner.KnowledgeBaseId}/documents/{disabledId}/disable", owner.Token, new { reason = "價目錯誤" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var effectiveOnly = await PreviewAsync(owner, ReturnQuestion, top: KnowledgeRetrievalSettings.MaxTop);
        var withPending = await PreviewAsync(owner, ReturnQuestion, includePending: true, top: KnowledgeRetrievalSettings.MaxTop);

        effectiveOnly.Passages.Select(passage => passage.ChunkId)
            .ShouldBe(await IncludedChunkIdsAsync(owner, policy.V2), ignoreOrder: true);
        withPending.Passages.Select(passage => passage.ChunkId)
            .ShouldBe([.. await IncludedChunkIdsAsync(owner, policy.V2), .. await IncludedChunkIdsAsync(owner, policy.V3)], ignoreOrder: true);
        foreach (var preview in new[] { effectiveOnly, withPending })
        {
            preview.Passages.ShouldNotContain(passage => passage.ChunkId == page2);
            preview.Passages.ShouldNotContain(passage => passage.DocumentId == disabledId);
            preview.Passages.ShouldNotContain(passage => passage.VersionId == policy.V1);
        }
    }

    // --- Plan Slice 7's deferred check, at retrieval level --------------------------------------

    [Fact]
    public async Task After_the_embedding_model_changes_retrieval_finds_nothing_old_until_reindex()
    {
        // The Fake model this time (the fixture's own host), so reindex can re-embed.
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (_, versionId) = await UploadAsync(owner, "退貨政策.md", Markdown("退貨政策", "收到商品後七天內可申請退貨。"));
        await _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        (await ApproveAsync(owner, versionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await PreviewAsync(owner, ReturnQuestion, top: KnowledgeRetrievalSettings.MaxTop);
        before.Passages.ShouldNotBeEmpty();

        await using var switched = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Ai:Embedding:Model", "fake-other"));
        var switchedOwner = await owner.SignInViaAsync(switched);

        var stale = await PreviewAsync(switchedOwner, ReturnQuestion, top: KnowledgeRetrievalSettings.MaxTop);
        (stale.Passages.Count, stale.BelowThreshold).ShouldBe((0, true));

        var error = new StringWriter();
        (await ReindexCommand.RunAsync(switched.Services, ["--organization", owner.Organization.Code], TextWriter.Null, error, CancellationToken))
            .ShouldBe(ReindexCommand.ExitSuccess, error.ToString());

        var after = await PreviewAsync(switchedOwner, ReturnQuestion, top: KnowledgeRetrievalSettings.MaxTop);
        after.Passages.Select(passage => passage.ChunkId).ShouldBe(before.Passages.Select(passage => passage.ChunkId), ignoreOrder: true);
        after.Passages.ShouldAllBe(passage => passage.VersionState == "effective");
        (await PreviewAsync(owner, ReturnQuestion)).Passages.ShouldBeEmpty("the old model's host now finds none of the re-embedded vectors");

        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.ModelInvocations.CountAsync(invocation => invocation.Purpose == ModelInvocationPurpose.EmbedQuery && invocation.Model == "fake-other", CancellationToken))
            .ShouldBe(2, "the question is embedded by the configured model, found or not");
    }

    // --- Helpers --------------------------------------------------------------------------------

    private const string StaffPassword = "Retrieval-Staff-Pass-1!";

    private sealed record Preview(IReadOnlyList<Passage> Passages, double Threshold, bool BelowThreshold);

    private sealed record Passage(
        Guid DocumentId,
        string DocumentName,
        int VersionNumber,
        string VersionState,
        string LocationLabel,
        string Excerpt,
        double Score,
        Guid VersionId,
        Guid ChunkId);

    private sealed record ReturnPolicy(Guid DocumentId, Guid V1, Guid V2, Guid V3);

    private static string PreviewPath(Guid knowledgeBaseId) => $"{BasePath}/{knowledgeBaseId}/retrieval-preview";

    private Task<KnowledgeTestOwner> ScriptedOwnerAsync(string name = "安心商行") => KnowledgeTestOwner.CreateAsync(_host, name, _host.Scripted);

    private SpaClient CreateScriptedSpaClient() =>
        new(_host.Scripted.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    private Task RunJobsAsync() => _host.Scripted.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    /// <summary>「退換貨政策.md」: version 1 (十天, approved, then archived), version 2 =
    /// <c>return-policy.pdf</c> (七天 on page 2, approved: in effect), version 3 (十四天, pending).</summary>
    private async Task<ReturnPolicy> SeedReturnPolicyAsync(KnowledgeTestOwner owner)
    {
        var (documentId, v1) = await UploadAsync(owner, "退換貨政策.md", Markdown("退換貨政策（第 1 版）", "收到商品後十天內可申請退貨。"));
        await RunJobsAsync();
        (await ApproveAsync(owner, v1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var v2 = await UploadVersionAsync(owner, documentId, "退換貨政策-第2版.pdf", KnowledgeFixtures.Read(KnowledgeFixtures.ReturnPolicyPdf));
        await RunJobsAsync();
        (await ApproveAsync(owner, v2)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var v3 = await UploadVersionAsync(owner, documentId, "退換貨政策-第3版.md", Markdown("退換貨政策（第 3 版）", "收到商品後十四天內可申請退貨。"));
        await RunJobsAsync();
        return new ReturnPolicy(documentId, v1, v2, v3);
    }

    private static byte[] Markdown(string title, string returnClause) => Encoding.UTF8.GetBytes(
        $"# {title}\n\n適用於安心商行線上商店。\n\n## 退貨期限\n\n{returnClause}\n\n## 聯絡方式\n\n請來信客服信箱並註明訂單編號。\n");

    private static async Task<Preview> PreviewAsync(KnowledgeTestOwner owner, string question, bool? includePending = null, int? top = null)
    {
        var response = await owner.Spa.PostAsync(PreviewPath(owner.KnowledgeBaseId), owner.Token, new { question, includePending, top });
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<Preview>(body, JsonSerializerOptions.Web)!;
    }

    private static async Task<(Guid DocumentId, Guid VersionId)> UploadAsync(KnowledgeTestOwner owner, string fileName, byte[] content)
    {
        var response = await owner.PostFileAsync($"{BasePath}/{owner.KnowledgeBaseId}/documents", fileName, content);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var view = await JsonAsync(response);
        return (view.GetProperty("id").GetGuid(), view.GetProperty("latestVersionId").GetGuid());
    }

    private static async Task<Guid> UploadVersionAsync(KnowledgeTestOwner owner, Guid documentId, string fileName, byte[] content)
    {
        var response = await owner.PostFileAsync($"{BasePath}/{owner.KnowledgeBaseId}/documents/{documentId}/versions", fileName, content);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await JsonAsync(response)).GetProperty("latestVersionId").GetGuid();
    }

    private static Task<HttpResponseMessage> ApproveAsync(KnowledgeTestOwner owner, Guid versionId) =>
        owner.Spa.PostAsync($"{BasePath}/{owner.KnowledgeBaseId}/versions/approve", owner.Token, new { versionIds = new[] { versionId.ToString() } });

    private async Task<Guid> ChunkIdAsync(KnowledgeTestOwner owner, Guid versionId, string locationLabel)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeChunks.Where(chunk => chunk.VersionId == versionId && chunk.LocationLabel == locationLabel)
            .Select(chunk => chunk.Id).SingleAsync(CancellationToken);
    }

    private async Task<string> ChunkTextAsync(KnowledgeTestOwner owner, Guid chunkId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeChunks.Where(chunk => chunk.Id == chunkId).Select(chunk => chunk.Text).SingleAsync(CancellationToken);
    }

    private async Task<List<Guid>> IncludedChunkIdsAsync(KnowledgeTestOwner owner, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeChunks.Where(chunk => chunk.VersionId == versionId && !chunk.Excluded)
            .Select(chunk => chunk.Id).ToListAsync(CancellationToken);
    }

    private async Task<int> QueryInvocationsAsync(KnowledgeTestOwner owner)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.ModelInvocations.CountAsync(invocation => invocation.Purpose == ModelInvocationPurpose.EmbedQuery, CancellationToken);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();

    /// <summary><see cref="AuthHostFixture"/> plus <see cref="Scripted"/>: the same Api and
    /// database with <see cref="ScriptedVectors"/> as the embedding model (still named
    /// <see cref="AuthHostFixture.EmbeddingModel"/>).</summary>
    public sealed class RetrievalHostFixture : AuthHostFixture
    {
        private WebApplicationFactory<Program>? _scripted;

        public ScriptedVectors Vectors { get; } = new();

        public WebApplicationFactory<Program> Scripted => _scripted ?? throw new InvalidOperationException("Not initialized.");

        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();
            _scripted = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(Vectors.Provider)));
        }

        public override async ValueTask DisposeAsync()
        {
            if (_scripted is not null)
            {
                await _scripted.DisposeAsync();
            }

            await base.DisposeAsync();
        }
    }

    /// <summary>
    /// An embedding model whose vectors the tests chose. Against <see cref="ReturnQuestion"/>'s
    /// (1, 0, 0, 0): a text with 「十四天內可申請退貨」 scores 0.95, 「十天內…」 0.9, 「七天內…」 0.8, anything
    /// else 0.1. <see cref="UnrelatedQuestion"/> scores 0.19, 0.18, 0.16 and 0.12 against the same.
    /// Other questions get the "anything else" vector.
    /// </summary>
    public sealed class ScriptedVectors : IEmbeddingGenerator<string, Embedding<float>>
    {
        private static readonly (string Text, float[] Vector)[] Questions =
        [
            (ReturnQuestion, [1, 0, 0, 0]),
            (UnrelatedQuestion, [0.2f, 0, 0.1f, 0.97468f]),
        ];

        private static readonly (string Fragment, float[] Vector)[] Passages =
        [
            ("十四天內可申請退貨", [0.95f, 0.31225f, 0, 0]),
            ("十天內可申請退貨", [0.9f, 0.43589f, 0, 0]),
            ("七天內可申請退貨", [0.8f, 0.6f, 0, 0]),
        ];

        private static readonly float[] Anything = [0.1f, 0, 0.99499f, 0];

        private int _failures;

        public ScriptedVectors()
        {
            Provider = new EmbeddingProvider(this, "fake", "smartagri.fake", AuthHostFixture.EmbeddingModel, endpoint: null);
        }

        public EmbeddingProvider Provider { get; }

        /// <summary>Makes the next <paramref name="count"/> calls fail like an unavailable provider.</summary>
        public void FailNextCalls(int count) => Interlocked.Exchange(ref _failures, count);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _failures) >= 0)
            {
                return Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                    new HttpRequestException("503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable));
            }

            Interlocked.Exchange(ref _failures, 0);
            var inputs = values.ToList();
            var embeddings = new GeneratedEmbeddings<Embedding<float>>(inputs.Select(input => new Embedding<float>(VectorOf(input))))
            {
                Usage = new UsageDetails { InputTokenCount = inputs.Sum(input => (long)input.Length) },
            };
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static float[] VectorOf(string text) =>
            Questions.FirstOrDefault(question => question.Text == text).Vector
            ?? Passages.FirstOrDefault(passage => text.Contains(passage.Fragment, StringComparison.Ordinal)).Vector
            ?? Anything;
    }
}
