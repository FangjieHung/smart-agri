using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Knowledge.Evaluation;
using SmartAgri.Api.Seeding;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Seeding;

namespace SmartAgri.Api.Tests.Knowledge.Evaluation;

/// <summary>
/// The retrieval evaluation set against real PostgreSQL (M2 plan Slice 16; ticket #50):
/// <c>eval-retrieval</c> runs end to end with the <c>Fake</c> embedding model — import through the
/// upload, processing and approval pipeline, a search per question, a report with every section —
/// and <see cref="DemoKnowledgeSeeder"/> puts the same documents into 安心商行 idempotently, and
/// only when asked. The scores are not asserted beyond being well formed: <c>Fake</c> vectors
/// are hashes, and judging a real model is what the command is for (the runs themselves are
/// deferred to the end of M2, see docs/plans/2026-09-26-m2-owner-action-items.md).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class RetrievalEvaluationTests : IClassFixture<AuthHostFixture>, IDisposable
{
    private readonly AuthHostFixture _host;
    private readonly DirectoryInfo _reports = Directory.CreateTempSubdirectory("smartagri-eval-report-");

    public RetrievalEvaluationTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static RetrievalEvalSet Set { get; } = RetrievalEvalSet.Load(RetrievalEvalSet.DefaultDirectory);

    public void Dispose() => _reports.Delete(recursive: true);

    [Fact]
    public async Task Eval_retrieval_with_the_fake_model_runs_end_to_end_and_writes_a_report_with_every_section()
    {
        var reportPath = Path.Combine(_reports.FullName, "nested", "report.md");
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await EvalRetrievalCommand.RunAsync(_host.Factory.Services, ["--report", reportPath, "--timeout", "60"], output, error, CancellationToken);

        exit.ShouldBe(EvalRetrievalCommand.ExitSuccess, error.ToString());
        output.ToString().ShouldContain($"報告：{reportPath}");
        output.ToString().ShouldContain($"hit@5 ");
        var report = await File.ReadAllTextAsync(reportPath, CancellationToken);
        report.ShouldStartWith($"# 檢索評測：{AuthHostFixture.EmbeddingModel}（");
        foreach (var section in RetrievalEvalReport.Sections)
        {
            report.ShouldContain("\n" + section + "\n", Case.Sensitive);
        }

        report.ShouldContain("不可用來判斷檢索品質", Case.Sensitive, "the Fake warning");
        report.ShouldContain($"（{Set.Questions.Count} 題，其中 {Set.Questions.Count(question => question.ExpectsNothing)} 題應查無結果）", Case.Sensitive);
        report.ShouldContain("| 取回非有效版本的段落 | 0 |", Case.Sensitive, "the archived version never comes back");
        report.ShouldMatch(@"\| 前 5 名命中率（hit@5） \| \d+/\d+ = \d+\.\d% \|");
        report.ShouldMatch(@"\| 建議門檻 \| \*\*\d\.\d{3}\*\*（正確判斷 \d+/\d+ 題） \|");
        foreach (var question in Set.Questions)
        {
            report.ShouldContain($"| {question.Id} | {question.Category} | {question.Question} |", Case.Sensitive);
        }

        // The set went through the normal pipeline into an organization of its own.
        var organizationId = await OrganizationIdAsync(EvalRetrievalCommand.OrganizationCode);
        await ShouldHoldTheSetAsync(organizationId);
        await using (var dbContext = _host.Postgres.CreateDbContext(organizationId))
        {
            (await dbContext.ModelInvocations.CountAsync(invocation => invocation.Purpose == ModelInvocationPurpose.EmbedQuery, CancellationToken))
                .ShouldBe(Set.Questions.Count, "one recorded question embedding per question");
        }

        // Again: the organization is reset first, so the same set gives the same judgement.
        var againPath = Path.Combine(_reports.FullName, "again.md");
        (await EvalRetrievalCommand.RunAsync(_host.Factory.Services, ["--report", againPath], TextWriter.Null, error, CancellationToken))
            .ShouldBe(EvalRetrievalCommand.ExitSuccess, error.ToString());
        await ShouldHoldTheSetAsync(organizationId);
        Section(await File.ReadAllTextAsync(againPath, CancellationToken), "## 逐題結果").ShouldBe(Section(report, "## 逐題結果"));
        Section(await File.ReadAllTextAsync(againPath, CancellationToken), "## 結果摘要").ShouldBe(Section(report, "## 結果摘要"));
    }

    [Fact]
    public async Task The_demo_knowledge_seeder_puts_the_set_into_anxin_once_and_changes_nothing_when_run_again()
    {
        await using var seeding = _host.Factory.WithWebHostBuilder(builder => builder.UseSetting(DemoKnowledgeSeeder.ConfigurationKey, "true"));

        await SeedAsync(seeding.Services);
        var organizationId = await OrganizationIdAsync(DevelopmentSeedData.AnxinOrganizationCode);
        var first = await SnapshotAsync(organizationId);
        await ShouldHoldTheSetAsync(organizationId, ownerLoginName: DemoKnowledgeSeeder.AdminLoginName);

        await SeedAsync(seeding.Services);

        (await SnapshotAsync(organizationId)).ShouldBe(first, "the same knowledge bases, documents, versions, chunks, activities and jobs");
    }

    [Fact]
    public async Task Without_the_setting_seeding_adds_no_knowledge()
    {
        await SeedAsync(_host.Factory.Services);
        var organizationId = await OrganizationIdAsync(DevelopmentSeedData.AnxinOrganizationCode);
        var before = await SnapshotAsync(organizationId);

        await SeedAsync(_host.Factory.Services);

        (await SnapshotAsync(organizationId)).ShouldBe(before);
    }

    /// <summary>What <c>migrate</c> runs in Development.</summary>
    private static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.SeedDevelopmentDataAsync(CancellationToken);
    }

    private async Task<Guid> OrganizationIdAsync(string code)
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        return await dbContext.Organizations.Where(organization => organization.Code == code).Select(organization => organization.Id).SingleAsync(CancellationToken);
    }

    /// <summary>The organization has the set's knowledge bases and documents, every document in
    /// effect with its last version, the earlier ones archived — as the owner approved them.</summary>
    private async Task ShouldHoldTheSetAsync(Guid organizationId, string? ownerLoginName = null)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);
        var now = _host.Clock.GetUtcNow();
        var knowledgeBases = await dbContext.KnowledgeBases.AsNoTracking().ToListAsync(CancellationToken);
        knowledgeBases.Select(knowledgeBase => knowledgeBase.Name).ShouldBe(Set.KnowledgeBases.Select(knowledgeBase => knowledgeBase.Name), ignoreOrder: true);
        if (ownerLoginName is not null)
        {
            var ownerId = await dbContext.Accounts.Where(account => account.LoginName == ownerLoginName).Select(account => account.Id).SingleAsync(CancellationToken);
            knowledgeBases.ShouldAllBe(knowledgeBase => knowledgeBase.OwnerAccountId == ownerId);
        }

        var effective = await dbContext.KnowledgeDocumentVersions.AsNoTracking()
            .Where(RetrievableChunks.CurrentEffectiveVersion(now))
            .Select(version => version.Id)
            .ToListAsync(CancellationToken);
        foreach (var knowledgeBase in Set.KnowledgeBases)
        {
            var knowledgeBaseId = knowledgeBases.Single(candidate => candidate.Name == knowledgeBase.Name).Id;
            foreach (var document in knowledgeBase.Documents)
            {
                var stored = await dbContext.KnowledgeDocuments.AsNoTracking()
                    .SingleAsync(candidate => candidate.KnowledgeBaseId == knowledgeBaseId && candidate.Name == document.Name, CancellationToken);
                stored.DisabledAt.ShouldBeNull();
                var versions = await dbContext.KnowledgeDocumentVersions.AsNoTracking()
                    .Where(version => version.DocumentId == stored.Id)
                    .OrderBy(version => version.VersionNumber)
                    .ToListAsync(CancellationToken);
                versions.Select(version => (version.FileName, version.Sha256)).ShouldBe(document.Versions.Select(version => (version.FileName, version.Sha256)));
                versions.ShouldAllBe(version => version.ProcessingStatus == KnowledgeDocumentStatus.Ready && version.ReviewState == KnowledgeReviewState.Approved);
                versions.Select(version => KnowledgeVersionStates.Of(version.ReviewState, version.EffectiveFrom, effective.Contains(version.Id), now))
                    .ShouldBe([.. Enumerable.Repeat(KnowledgeVersionState.Archived, versions.Count - 1), KnowledgeVersionState.Effective], document.Name);
            }
        }
    }

    /// <summary>Everything the seeder could add to the organization, by id.</summary>
    private async Task<string> SnapshotAsync(Guid organizationId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);
        var parts = new[]
        {
            string.Join(',', (await dbContext.KnowledgeBases.Select(row => row.Id).ToListAsync(CancellationToken)).Order()),
            string.Join(',', (await dbContext.KnowledgeDocuments.Select(row => row.Id).ToListAsync(CancellationToken)).Order()),
            string.Join(',', (await dbContext.KnowledgeDocumentVersions.Select(row => new { row.Id, row.ReviewState, row.ProcessingStatus }).ToListAsync(CancellationToken))
                .Select(row => $"{row.Id}:{row.ReviewState}:{row.ProcessingStatus}").Order()),
            string.Join(',', (await dbContext.KnowledgeChunks.Select(row => row.Id).ToListAsync(CancellationToken)).Order()),
            string.Join(',', (await dbContext.KnowledgeActivities.Select(row => row.Id).ToListAsync(CancellationToken)).Order()),
            string.Join(',', (await dbContext.BackgroundJobs.Select(row => row.Id).ToListAsync(CancellationToken)).Order()),
        };
        return string.Join('\n', parts);
    }

    /// <summary>A report's section from its heading to the next one.</summary>
    private static string Section(string report, string heading)
    {
        var start = report.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, heading);
        var end = report.IndexOf("\n## ", start + heading.Length + 2, StringComparison.Ordinal);
        return end < 0 ? report[start..] : report[start..end];
    }
}
