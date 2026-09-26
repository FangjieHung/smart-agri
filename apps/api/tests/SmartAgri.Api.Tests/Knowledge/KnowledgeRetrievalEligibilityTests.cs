using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Knowledge;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// Which chunks are retrievable, asserted through the real vector search path (M2 plan §3 and
/// Slice 8; ticket #42): <see cref="KnowledgeChunkVectorCollection.SearchAsync{TInput}"/> with
/// <see cref="RetrievableChunks.InKnowledgeBase"/> as its filter — exactly what the retrieval
/// preview (#43) will run — against real PostgreSQL and pgvector, with the <c>Fake</c> model's
/// vectors. Documents are uploaded, approved, disabled and enabled through the API; the test
/// clock moves time forward for effective dates.
/// </summary>
/// <remarks>Tests in this class share the clock: the one that moves it signs in again
/// afterwards, and every test signs in when it starts.</remarks>
[Trait("Category", TestCategories.Docker)]
public sealed class KnowledgeRetrievalEligibilityTests : IClassFixture<AuthHostFixture>
{
    private const string BasePath = "/api/v1/knowledge-bases";
    private const string OtherModel = "fake-other";
    private const string Question = "收到商品幾天內可以退貨？";

    private readonly AuthHostFixture _host;

    public KnowledgeRetrievalEligibilityTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Acceptance: every combination, step by step, through vector search -------------------

    [Fact]
    public async Task Vector_search_finds_only_the_current_effective_version_of_an_enabled_document_at_every_step()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (documentId, v1) = await UploadAsync(owner, "退貨政策.md", Policy(1, "七"));

        // Another document, approved at once, is retrievable throughout: the rule is per document.
        var (_, other) = await UploadAsync(owner, "配送時間.md", Encoding.UTF8.GetBytes("# 配送時間\n\n## 本島\n\n下單後兩個工作天內送達。\n"));
        await RunJobsAsync();
        (await ApproveAsync(owner, [other])).StatusCode.ShouldBe(HttpStatusCode.OK);

        Guid v2 = default, v3 = default;
        IReadOnlyList<Guid> beforeDisable = [];
        var steps = new (string Name, Func<Task> Act, int[] Retrievable, string[] States, bool InEffect)[]
        {
            ("v1 processed ready but pending review: not retrievable", () => Task.CompletedTask,
                [], ["pending-review"], false),
            ("v1 approved (effective now): retrievable", async () =>
                (await ApproveAsync(owner, [v1])).StatusCode.ShouldBe(HttpStatusCode.OK),
                [1], ["effective"], true),
            ("a v1 chunk excluded: never retrievable", async () =>
                await ExcludeFirstChunkAsync(owner, documentId, v1),
                [1], ["effective"], true),
            ("v2 uploaded and processed, pending review: v1 still used", async () =>
            {
                v2 = await UploadVersionAsync(owner, documentId, "退貨政策-2026 修訂.md", Policy(2, "十"));
                await RunJobsAsync();
                await ExcludeFirstChunkAsync(owner, documentId, v2);
            }, [1], ["pending-review", "effective"], true),
            ("v2 approved effective tomorrow: v1 still used today", async () =>
            {
                var tomorrow = _host.Clock.GetUtcNow().AddDays(1).ToOffset(TimeSpan.FromHours(8)).ToString("O");
                (await ApproveAsync(owner, [v2], tomorrow)).StatusCode.ShouldBe(HttpStatusCode.OK);
            }, [1], ["scheduled", "effective"], true),
            ("a day later: v2 in effect, v1 archived and not retrievable", async () =>
            {
                _host.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
                owner = await owner.SignInAgainAsync();
            }, [2], ["effective", "archived"], true),
            ("disabled: nothing of the document is retrievable", async () =>
            {
                beforeDisable = [.. (await RetrievableAsync(owner)).Select(chunk => chunk.Id)];
                (await owner.Spa.PostAsync(DocumentPath(owner, documentId) + "/disable", owner.Token, new { reason = "價格錯誤，暫停使用" }))
                    .StatusCode.ShouldBe(HttpStatusCode.OK);
            }, [], ["effective", "archived"], false),
            ("enabled: back to exactly what it was before", async () =>
                (await owner.Spa.PostAsync(DocumentPath(owner, documentId) + "/enable", owner.Token, new { }))
                    .StatusCode.ShouldBe(HttpStatusCode.OK),
                [2], ["effective", "archived"], true),
            ("v3 uploaded and approved at once: only v3", async () =>
            {
                v3 = await UploadVersionAsync(owner, documentId, "退貨政策.md", Policy(3, "十四"));
                await RunJobsAsync();
                (await ApproveAsync(owner, [v3])).StatusCode.ShouldBe(HttpStatusCode.OK);
            }, [3], ["effective", "archived", "archived"], true),
        };

        foreach (var (name, act, retrievable, states, inEffect) in steps)
        {
            await act();

            var versionIds = new[] { v1, v2, v3 }.Take(states.Length).ToArray();
            var expected = await RetrievableChunksOfAsync(owner, [.. retrievable.Select(number => versionIds[number - 1]), other]);
            var found = await RetrievableAsync(owner);

            found.Select(chunk => chunk.Id).ShouldBe(expected, ignoreOrder: true, name);
            found.ShouldAllBe(chunk => !chunk.Excluded && chunk.EmbeddingModel == AuthHostFixture.EmbeddingModel, name);
            if (name.StartsWith("enabled", StringComparison.Ordinal))
            {
                found.Select(chunk => chunk.Id).ShouldBe(beforeDisable, ignoreOrder: true, name);
            }

            // The rule as a plain query agrees with the vector search: the filter is all there is.
            (await RuleAsQueryAsync(owner)).ShouldBe(expected, ignoreOrder: true, name);

            // The API tells the owner the same: per version, and per document in the list counts.
            var detail = await DocumentDetailAsync(owner, documentId);
            detail.GetProperty("versions").EnumerateArray().Select(version => version.GetProperty("state").GetString()).ShouldBe(states, name);
            var row = detail.GetProperty("document");
            row.GetProperty("inEffect").GetBoolean().ShouldBe(inEffect, name);
            row.GetProperty("effectiveVersionNumber").ValueKind.ShouldBe(
                states.Contains("effective") ? JsonValueKind.Number : JsonValueKind.Null, name);
            var summary = JsonDocument.Parse(await (await owner.Spa.GetAsync($"{BasePath}/{owner.KnowledgeBaseId}", owner.Token)).Content.ReadAsStringAsync(CancellationToken))
                .RootElement.GetProperty("summary");
            summary.GetProperty("inEffectCount").GetInt32().ShouldBe(inEffect ? 2 : 1, name);
        }
    }

    [Fact]
    public async Task Disabling_then_enabling_restores_exactly_the_chunks_retrievable_before()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (documentId, versionId) = await UploadAsync(owner, "退貨政策.md", Policy(1, "七"));
        await RunJobsAsync();
        (await ApproveAsync(owner, [versionId])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ExcludeFirstChunkAsync(owner, documentId, versionId);
        var before = (await RetrievableAsync(owner)).Select(chunk => chunk.Id).ToList();
        before.ShouldNotBeEmpty();

        (await owner.Spa.PostAsync(DocumentPath(owner, documentId) + "/disable", owner.Token, new { reason = "內容待確認" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RetrievableAsync(owner)).ShouldBeEmpty("an emergency disable stops retrieval with the save that records it");
        (await owner.Spa.PostAsync(DocumentPath(owner, documentId) + "/enable", owner.Token, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RetrievableAsync(owner)).Select(chunk => chunk.Id).ShouldBe(before, ignoreOrder: true);
    }

    [Fact]
    public async Task Vectors_of_another_embedding_model_are_never_retrievable()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (_, versionId) = await UploadAsync(owner, "退貨政策.md", Policy(1, "七"));
        await RunJobsAsync();
        (await ApproveAsync(owner, [versionId])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var chunks = await ChunksAsync(owner, versionId);
        chunks.Count.ShouldBeGreaterThan(1);

        // As if one chunk were still embedded by another model (e.g. halfway through reindex).
        var stale = chunks[^1].Id;
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            await dbContext.KnowledgeChunks.Where(chunk => chunk.Id == stale)
                .ExecuteUpdateAsync(set => set.SetProperty(chunk => chunk.EmbeddingModel, OtherModel), CancellationToken);
        }

        var found = await RetrievableAsync(owner);
        found.Select(chunk => chunk.Id).ShouldBe(chunks.Where(chunk => chunk.Id != stale).Select(chunk => chunk.Id), ignoreOrder: true);

        // A deployment configured with the other model finds only that chunk; the rule and the
        // collection must name the same model, or nothing matches.
        (await RetrievableAsync(owner, OtherModel, OtherModel)).Select(chunk => chunk.Id).ShouldBe([stale]);
        (await RetrievableAsync(owner, AuthHostFixture.EmbeddingModel, OtherModel)).ShouldBeEmpty();
        (await RetrievableAsync(owner, OtherModel, AuthHostFixture.EmbeddingModel)).ShouldBeEmpty();
    }

    // --- Acceptance: a batch with a failed version is refused whole -----------------------------

    [Fact]
    public async Task A_batch_with_one_failed_version_gets_422_and_every_version_stays_pending_review()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (_, a) = await UploadAsync(owner, "退貨政策.md", Policy(1, "七"));
        var (_, b) = await UploadAsync(owner, "換貨政策.md", Encoding.UTF8.GetBytes("# 換貨政策\n\n## 期限\n\n收到商品後七天內可以換貨。\n"));
        var (_, failed) = await UploadAsync(owner, "損毀.pdf", TestFiles.Pdf("損毀"));
        await RunJobsAsync();
        await AssertStatusesAsync(owner, (a, KnowledgeDocumentStatus.Ready), (b, KnowledgeDocumentStatus.Ready), (failed, KnowledgeDocumentStatus.Failed));

        var refused = await ApproveAsync(owner, [a, failed, b]);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await JsonAsync(refused);
        body.GetProperty("reason").GetString().ShouldBe("versions-not-approvable");
        body.GetProperty("message").GetString()!.ShouldStartWith("有 1 個版本不能確認生效");
        body.GetProperty("errors").EnumerateObject().Select(error => (error.Name, error.Value[0].GetString()))
            .ShouldBe([("versionIds[1]", KnowledgeReviewRules.VersionFailedMessage)]);
        await AssertAllPendingAsync(owner, a, b, failed);

        // Without the failed one the same batch goes through, one activity row per version.
        var approved = await ApproveAsync(owner, [a, b]);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(CancellationToken));
        (await JsonAsync(approved)).EnumerateArray().Select(version => (version.GetProperty("id").GetGuid(), version.GetProperty("state").GetString()))
            .ShouldBe([(a, "effective"), (b, "effective")]);
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var rows = await dbContext.KnowledgeActivities.AsNoTracking()
            .Where(activity => activity.Action == KnowledgeActivityAction.VersionApproved)
            .ToListAsync(CancellationToken);
        rows.Select(activity => activity.VersionId).ShouldBe([a, b], ignoreOrder: true);
        rows.ShouldAllBe(activity => activity.ActorAccountId == owner.AccountId && activity.Detail == null);
    }

    [Fact]
    public async Task Queued_already_approved_and_foreign_versions_are_refused_together_naming_each_position()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (_, ready) = await UploadAsync(owner, "退貨政策.md", Policy(1, "七"));
        await RunJobsAsync();
        var (_, approved) = await UploadAsync(owner, "換貨政策.md", Encoding.UTF8.GetBytes("# 換貨政策\n\n七天內可以換貨。\n"));
        await RunJobsAsync();
        (await ApproveAsync(owner, [approved])).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The owner's other knowledge base, and another organization: neither is "in this one".
        var otherKnowledgeBase = await CreateKnowledgeBaseAsync(owner, "另一個知識庫");
        var ownOtherVersion = await UploadToAsync(owner, otherKnowledgeBase, "別處.md", Encoding.UTF8.GetBytes("# 別處\n\n另一個知識庫的內容。\n"));
        var stranger = await KnowledgeTestOwner.CreateAsync(_host, "別的組織");
        var (_, foreignVersion) = await UploadAsync(stranger, "退貨政策.md", Policy(1, "七"));
        await RunJobsAsync();

        // Not processed yet: no job runs after this upload.
        var (_, queued) = await UploadAsync(owner, "排隊中.md", Encoding.UTF8.GetBytes("# 排隊中\n\n尚未處理。\n"));

        var response = await owner.Spa.PostAsync(
            $"{BasePath}/{owner.KnowledgeBaseId}/versions/approve",
            owner.Token,
            new { versionIds = new[] { ready.ToString(), queued.ToString(), approved.ToString(), ownOtherVersion.ToString(), foreignVersion.ToString(), "不是 GUID", ready.ToString() } });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(error => (error.Name, error.Value[0].GetString()))
            .ShouldBe(
            [
                ("versionIds[1]", KnowledgeReviewRules.VersionStillProcessingMessage),
                ("versionIds[2]", KnowledgeReviewRules.VersionAlreadyApprovedMessage),
                ("versionIds[3]", KnowledgeReviewRules.VersionNotFoundMessage),
                ("versionIds[4]", KnowledgeReviewRules.VersionNotFoundMessage),
                ("versionIds[5]", KnowledgeReviewRules.VersionNotFoundMessage),
            ]);
        await AssertAllPendingAsync(owner, ready, queued);
        await AssertAllPendingAsync(owner, ownOtherVersion);
        await AssertAllPendingAsync(stranger, foreignVersion);
    }

    [Fact]
    public async Task The_request_shape_is_checked_first_and_an_effective_time_a_little_in_the_past_counts_as_now()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (_, versionId) = await UploadAsync(owner, "退貨政策.md", Policy(1, "七"));
        await RunJobsAsync();
        var path = $"{BasePath}/{owner.KnowledgeBaseId}/versions/approve";
        var now = _host.Clock.GetUtcNow();

        foreach (var (body, field, message) in new (object Body, string Field, string Message)[]
        {
            (new { }, "versionIds", KnowledgeReviewRules.VersionIdsRequiredMessage),
            (new { versionIds = Array.Empty<string>() }, "versionIds", KnowledgeReviewRules.VersionIdsRequiredMessage),
            (new { versionIds = new[] { versionId.ToString() }, effectiveFrom = now.AddMinutes(-2).ToString("O") }, "effectiveFrom", KnowledgeReviewRules.EffectiveFromInPastMessage),
            (new { versionIds = new[] { versionId.ToString() }, effectiveFrom = "2026-12-01T00:00:00" }, "effectiveFrom", KnowledgeReviewRules.EffectiveFromInvalidMessage),
        })
        {
            var refused = await owner.Spa.PostAsync(path, owner.Token, body);
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await JsonAsync(refused)).GetProperty("errors").GetProperty(field)[0].GetString().ShouldBe(message);
        }

        await AssertAllPendingAsync(owner, versionId);

        // 30 seconds behind the server (a slow browser clock): taken as now, never stored earlier
        // than the approval itself.
        var skewed = await owner.Spa.PostAsync(path, owner.Token, new { versionIds = new[] { versionId.ToString() }, effectiveFrom = _host.Clock.GetUtcNow().AddSeconds(-30).ToString("O") });
        skewed.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var version = await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(row => row.Id == versionId, CancellationToken);
        version.ReviewState.ShouldBe(KnowledgeReviewState.Approved);
        version.EffectiveFrom.ShouldBe(version.ApprovedAt);
        version.ApprovedByAccountId.ShouldBe(owner.AccountId);
        (await RetrievableAsync(owner)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Two_concurrent_approvals_of_the_same_version_approve_it_once()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var (_, versionId) = await UploadAsync(owner, "退貨政策.md", Policy(1, "七"));
        await RunJobsAsync();

        var responses = await Task.WhenAll(ApproveAsync(owner, [versionId]), ApproveAsync(owner, [versionId]));

        responses.Select(response => response.StatusCode).ShouldBe([HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity], ignoreOrder: true);
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        (await dbContext.KnowledgeActivities.CountAsync(activity => activity.Action == KnowledgeActivityAction.VersionApproved, CancellationToken)).ShouldBe(1);
    }

    // --- Helpers -------------------------------------------------------------------------------

    /// <summary>A small Markdown policy: a few sections, so a few chunks; different per version.</summary>
    private static byte[] Policy(int version, string days) => Encoding.UTF8.GetBytes(
        $"# 退貨政策（第 {version} 版）\n\n適用於安心商行線上商店。\n\n" +
        $"## 退貨期限\n\n收到商品後{days}天內可以申請退貨。\n\n" +
        $"## 退貨運費\n\n第 {version} 版：商品瑕疵由本店負擔運費，其餘由顧客負擔。\n\n" +
        "## 聯絡方式\n\n請來信客服信箱並註明訂單編號。\n");

    private Task RunJobsAsync() => _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    private static string DocumentPath(KnowledgeTestOwner owner, Guid documentId) =>
        $"{BasePath}/{owner.KnowledgeBaseId}/documents/{documentId}";

    private static async Task<(Guid DocumentId, Guid VersionId)> UploadAsync(KnowledgeTestOwner owner, string fileName, byte[] content)
    {
        var response = await owner.PostFileAsync($"{BasePath}/{owner.KnowledgeBaseId}/documents", fileName, content);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var view = await JsonAsync(response);
        return (view.GetProperty("id").GetGuid(), view.GetProperty("latestVersionId").GetGuid());
    }

    private static async Task<Guid> UploadToAsync(KnowledgeTestOwner owner, Guid knowledgeBaseId, string fileName, byte[] content)
    {
        var response = await owner.PostFileAsync($"{BasePath}/{knowledgeBaseId}/documents", fileName, content);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await JsonAsync(response)).GetProperty("latestVersionId").GetGuid();
    }

    private static async Task<Guid> UploadVersionAsync(KnowledgeTestOwner owner, Guid documentId, string fileName, byte[] content)
    {
        var response = await owner.PostFileAsync(DocumentPath(owner, documentId) + "/versions", fileName, content);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await JsonAsync(response)).GetProperty("latestVersionId").GetGuid();
    }

    private static async Task<Guid> CreateKnowledgeBaseAsync(KnowledgeTestOwner owner, string name)
    {
        var response = await owner.Spa.PostAsync(BasePath, owner.Token, new { name, purpose = "" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> ApproveAsync(KnowledgeTestOwner owner, Guid[] versionIds, string? effectiveFrom = null) =>
        owner.Spa.PostAsync(
            $"{BasePath}/{owner.KnowledgeBaseId}/versions/approve",
            owner.Token,
            new { versionIds = versionIds.Select(id => id.ToString()).ToArray(), effectiveFrom });

    private async Task ExcludeFirstChunkAsync(KnowledgeTestOwner owner, Guid documentId, Guid versionId)
    {
        var chunk = (await ChunksAsync(owner, versionId))[0];
        (await owner.Spa.PutAsync(
                $"{DocumentPath(owner, documentId)}/versions/{versionId}/chunks/{chunk.Id}/exclusion", owner.Token, new { excluded = true }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> DocumentDetailAsync(KnowledgeTestOwner owner, Guid documentId)
    {
        var response = await owner.Spa.GetAsync(DocumentPath(owner, documentId), owner.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await JsonAsync(response);
    }

    /// <summary>The retrieval preview's search (#43) for the owner's knowledge base, now: every
    /// match of the eligibility filter, whatever its score.</summary>
    private async Task<List<KnowledgeChunk>> RetrievableAsync(
        KnowledgeTestOwner owner,
        string collectionModel = AuthHostFixture.EmbeddingModel,
        string ruleModel = AuthHostFixture.EmbeddingModel)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        using var collection = new KnowledgeChunkVectorCollection(dbContext, collectionModel);
        var options = new VectorSearchOptions<KnowledgeChunk>
        {
            Filter = RetrievableChunks.InKnowledgeBase(owner.KnowledgeBaseId, _host.Clock.GetUtcNow(), ruleModel),
        };
        return
        [
            .. (await collection.SearchAsync(new FakeEmbeddingGenerator(collectionModel).Embed(Question), 1000, options, CancellationToken)
                .ToListAsync(CancellationToken))
                .Select(result => result.Record),
        ];
    }

    private async Task<List<Guid>> RuleAsQueryAsync(KnowledgeTestOwner owner)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeChunks
            .Where(RetrievableChunks.InKnowledgeBase(owner.KnowledgeBaseId, _host.Clock.GetUtcNow(), AuthHostFixture.EmbeddingModel))
            .Select(chunk => chunk.Id)
            .ToListAsync(CancellationToken);
    }

    /// <summary>What should be retrievable of these versions, independently of the rule: their
    /// chunks the owner has not excluded.</summary>
    private async Task<List<Guid>> RetrievableChunksOfAsync(KnowledgeTestOwner owner, Guid[] versionIds)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeChunks
            .Where(chunk => versionIds.Contains(chunk.VersionId) && !chunk.Excluded)
            .Select(chunk => chunk.Id)
            .ToListAsync(CancellationToken);
    }

    private async Task<List<KnowledgeChunk>> ChunksAsync(KnowledgeTestOwner owner, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeChunks.AsNoTracking()
            .Where(chunk => chunk.VersionId == versionId)
            .OrderBy(chunk => chunk.UnitOrdinal).ThenBy(chunk => chunk.Ordinal)
            .ToListAsync(CancellationToken);
    }

    private async Task AssertStatusesAsync(KnowledgeTestOwner owner, params (Guid VersionId, KnowledgeDocumentStatus Status)[] expected)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        foreach (var (versionId, status) in expected)
        {
            (await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(version => version.Id == versionId, CancellationToken))
                .ProcessingStatus.ShouldBe(status);
        }
    }

    private async Task AssertAllPendingAsync(KnowledgeTestOwner owner, params Guid[] versionIds)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        var versions = await dbContext.KnowledgeDocumentVersions.AsNoTracking().Where(version => versionIds.Contains(version.Id)).ToListAsync(CancellationToken);
        versions.Count.ShouldBe(versionIds.Length);
        versions.ShouldAllBe(version =>
            version.ReviewState == KnowledgeReviewState.PendingReview
            && version.EffectiveFrom == null
            && version.ApprovedByAccountId == null
            && version.ApprovedAt == null);
        (await dbContext.KnowledgeActivities.CountAsync(
                activity => activity.Action == KnowledgeActivityAction.VersionApproved && versionIds.Contains(activity.VersionId!.Value),
                CancellationToken))
            .ShouldBe(0);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement.Clone();
}
