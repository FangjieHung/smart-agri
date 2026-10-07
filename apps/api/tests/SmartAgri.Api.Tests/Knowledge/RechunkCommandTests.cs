using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Api.Tests.Knowledge.Extraction;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// <c>rechunk</c> against real PostgreSQL (#301): it cuts only versions of an older chunk format
/// again — the product guide's fee table into one chunk per row, keeping the owner's exclusion,
/// the FAQ (no tables) without a model call — only in the organization asked for, writes nothing
/// with <c>--dry-run</c>, and does nothing the second time.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class RechunkCommandTests : IClassFixture<AuthHostFixture>
{
    private const string FeesLabel = "2 退換貨 › 2.2 運費";
    private const string OldFeesText = "地區 | 運費 | 免運門檻\n本島 | 100 元 | 1,500 元\n離島 | 150 元 | 3,000 元";

    private readonly AuthHostFixture _host;

    public RechunkCommandTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_processing_writes_the_current_format_and_cuts_a_table_into_rows()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await owner.UploadAsync(KnowledgeFixtures.ProductGuideDocx);
        await RunJobsAsync();

        (await ChunkFormatAsync(owner, versionId)).ShouldBe(KnowledgeChunkFormat.Current);
        (await ChunksAsync(owner, versionId)).Where(chunk => chunk.LocationLabel == FeesLabel).Select(chunk => chunk.Text).ShouldBe(
        [
            "地區：本島\n運費：100 元\n免運門檻：1,500 元",
            "地區：離島\n運費：150 元\n免運門檻：3,000 元",
        ]);
    }

    [Fact]
    public async Task Rechunk_cuts_only_old_format_versions_of_the_organization_keeps_exclusions_and_the_second_run_does_nothing()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var guide = await owner.UploadAsync(KnowledgeFixtures.ProductGuideDocx);
        var faq = await owner.UploadAsync(KnowledgeFixtures.FaqMarkdown);
        var other = await KnowledgeTestOwner.CreateAsync(_host, "別的商行");
        var otherGuide = await other.UploadAsync(KnowledgeFixtures.ProductGuideDocx);
        await RunJobsAsync();

        // As processed before #301: the fee table was one chunk of its section, which the owner
        // excluded; the FAQ (no tables) has the chunks it would have had anyway.
        await MakeOldFormatAsync(owner, guide, excludeTable: true);
        await MakeOldFormatAsync(owner, faq);
        await MakeOldFormatAsync(other, otherGuide);
        var guideBefore = await ChunksAsync(owner, guide);
        var faqBefore = await ChunksAsync(owner, faq);
        var callsBefore = await EmbeddingCallsAsync(owner);

        // --dry-run: what it would do, and nothing written or called.
        var dryRun = new StringWriter();
        (await RechunkCommand.RunAsync(_host.Factory.Services, ["--organization", owner.Organization.Code, "--dry-run"], dryRun, TextWriter.Null, CancellationToken))
            .ShouldBe(RechunkCommand.ExitSuccess, dryRun.ToString());
        dryRun.ToString().ShouldContain($"組織 {owner.Organization.Code}（{owner.Organization.Name}）：2 個版本的切段格式較舊。");
        dryRun.ToString().ShouldContain("段落 7 → 8 個，重新嵌入 2 個，沿用 2 個排除。");
        dryRun.ToString().ShouldContain("段落不變（");
        dryRun.ToString().ShouldContain("預計重新嵌入 2 個段落");
        dryRun.ToString().ShouldContain("沒有寫入任何資料。");
        (await ChunkFormatAsync(owner, guide)).ShouldBe(KnowledgeChunkFormat.SectionTables);
        (await ChunksAsync(owner, guide)).Select(chunk => chunk.Id).ShouldBe(guideBefore.Select(chunk => chunk.Id));
        (await EmbeddingCallsAsync(owner)).ShouldBe(callsBefore);

        // The real run.
        var output = new StringWriter();
        var error = new StringWriter();
        (await RechunkCommand.RunAsync(_host.Factory.Services, [$"--organization={owner.Organization.Code}"], output, error, CancellationToken))
            .ShouldBe(RechunkCommand.ExitSuccess, output + error.ToString());
        output.ToString().ShouldContain($"完成：2 個版本改為切段格式 2，段落 {7 + faqBefore.Count} → {8 + faqBefore.Count} 個，重新嵌入 2 個段落；未處理 0 個。");

        (await ChunkFormatAsync(owner, guide)).ShouldBe(KnowledgeChunkFormat.Current);
        (await ChunkFormatAsync(owner, faq)).ShouldBe(KnowledgeChunkFormat.Current);
        var guideAfter = await ChunksAsync(owner, guide);
        var rows = guideAfter.Where(chunk => chunk.LocationLabel == FeesLabel).ToList();
        rows.Select(chunk => chunk.Text).ShouldBe(["地區：本島\n運費：100 元\n免運門檻：1,500 元", "地區：離島\n運費：150 元\n免運門檻：3,000 元"]);
        rows.ShouldAllBe(chunk => chunk.Excluded, "the rows of an excluded table stay excluded");
        rows.ShouldAllBe(chunk => chunk.EmbeddingModel == AuthHostFixture.EmbeddingModel);
        rows[0].Embedding.ShouldBe(new FakeEmbeddingGenerator(AuthHostFixture.EmbeddingModel)
            .Embed(KnowledgeEmbeddingText.For(KnowledgeUnitLocationKind.Section, FeesLabel, rows[0].Text)));
        guideAfter.Where(chunk => chunk.LocationLabel != FeesLabel).Select(chunk => chunk.Id)
            .ShouldBe(guideBefore.Where(chunk => chunk.LocationLabel != FeesLabel).Select(chunk => chunk.Id), "units without tables keep their chunks");
        (await ChunksAsync(owner, faq)).Select(chunk => chunk.Id).ShouldBe(faqBefore.Select(chunk => chunk.Id));

        // One model call (the two rows), the organization's, with no account.
        (await EmbeddingCallsAsync(owner)).ShouldBe(callsBefore + 1);
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            (await dbContext.ModelInvocations.CountAsync(invocation => invocation.Purpose == ModelInvocationPurpose.EmbedDocument && invocation.AccountId == null, CancellationToken))
                .ShouldBe(1, "processing embeds for the uploader; rechunk for nobody, like reindex");
        }

        // The other organization was not asked for: still the old format, chunks as they were.
        (await ChunkFormatAsync(other, otherGuide)).ShouldBe(KnowledgeChunkFormat.SectionTables);
        (await ChunksAsync(other, otherGuide)).Single(chunk => chunk.LocationLabel == FeesLabel).Text.ShouldBe(OldFeesText);

        // Idempotent: nothing left in the old format, nothing done, nothing called.
        var again = new StringWriter();
        (await RechunkCommand.RunAsync(_host.Factory.Services, ["--organization", owner.Organization.Code], again, TextWriter.Null, CancellationToken))
            .ShouldBe(RechunkCommand.ExitSuccess);
        again.ToString().ShouldContain("：0 個版本的切段格式較舊。");
        (await ChunksAsync(owner, guide)).Select(chunk => chunk.Id).ShouldBe(guideAfter.Select(chunk => chunk.Id));
        (await EmbeddingCallsAsync(owner)).ShouldBe(callsBefore + 1);
    }

    [Fact]
    public async Task Bad_arguments_and_an_unknown_organization_are_refused()
    {
        foreach (var args in new[] { ["--organization"], ["--dry-run=yes"], new[] { "--everything" } })
        {
            var error = new StringWriter();
            (await RechunkCommand.RunAsync(_host.Factory.Services, args, TextWriter.Null, error, CancellationToken)).ShouldBe(RechunkCommand.ExitUsage, string.Join(' ', args));
            error.ToString().ShouldContain("用法：rechunk");
        }

        var help = new StringWriter();
        (await RechunkCommand.RunAsync(_host.Factory.Services, ["--help"], help, TextWriter.Null, CancellationToken)).ShouldBe(RechunkCommand.ExitSuccess);
        help.ToString().ShouldStartWith("用法：rechunk");

        var unknown = new StringWriter();
        (await RechunkCommand.RunAsync(_host.Factory.Services, ["--organization", "no-such-org", "--dry-run"], TextWriter.Null, unknown, CancellationToken))
            .ShouldBe(RechunkCommand.ExitFailed);
        unknown.ToString().ShouldContain("找不到組織代碼「no-such-org」");
    }

    // --- Helpers -------------------------------------------------------------------------

    private Task RunJobsAsync() => _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    /// <summary>Marks the version as chunked before #301 and, where its section has the fee table,
    /// puts back the one chunk that section had then (excluded when asked).</summary>
    private async Task MakeOldFormatAsync(KnowledgeTestOwner owner, Guid versionId, bool excludeTable = false)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        await dbContext.KnowledgeDocumentVersions.Where(version => version.Id == versionId)
            .ExecuteUpdateAsync(set => set.SetProperty(version => version.ChunkFormat, KnowledgeChunkFormat.SectionTables), CancellationToken);
        var rows = await dbContext.KnowledgeChunks.Where(chunk => chunk.VersionId == versionId && chunk.LocationLabel == FeesLabel).ToListAsync(CancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        var unitOrdinal = rows[0].UnitOrdinal;
        await dbContext.KnowledgeChunks.Where(chunk => chunk.VersionId == versionId && chunk.UnitOrdinal == unitOrdinal).ExecuteDeleteAsync(CancellationToken);
        dbContext.ChangeTracker.Clear();
        var version = await dbContext.KnowledgeDocumentVersions.SingleAsync(candidate => candidate.Id == versionId, CancellationToken);
        var old = KnowledgeChunk.Create(version, unitOrdinal, 0, FeesLabel, OldFeesText);
        old.SetEmbedding(
            new FakeEmbeddingGenerator(AuthHostFixture.EmbeddingModel).Embed(KnowledgeEmbeddingText.For(KnowledgeUnitLocationKind.Section, FeesLabel, OldFeesText)),
            AuthHostFixture.EmbeddingModel);
        old.SetExcluded(excludeTable);
        dbContext.KnowledgeChunks.Add(old);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task<int> ChunkFormatAsync(KnowledgeTestOwner owner, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeDocumentVersions.Where(version => version.Id == versionId).Select(version => version.ChunkFormat).SingleAsync(CancellationToken);
    }

    private async Task<List<KnowledgeChunk>> ChunksAsync(KnowledgeTestOwner owner, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeChunks.AsNoTracking()
            .Where(chunk => chunk.VersionId == versionId)
            .OrderBy(chunk => chunk.UnitOrdinal).ThenBy(chunk => chunk.Ordinal)
            .ToListAsync(CancellationToken);
    }

    private async Task<int> EmbeddingCallsAsync(KnowledgeTestOwner owner)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.ModelInvocations.CountAsync(invocation => invocation.Purpose == ModelInvocationPurpose.EmbedDocument, CancellationToken);
    }
}
