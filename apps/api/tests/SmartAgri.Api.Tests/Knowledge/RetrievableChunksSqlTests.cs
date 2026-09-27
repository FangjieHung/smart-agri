using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Tenancy;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// The SQL EF Core generates for the retrieval eligibility rule (M2 plan §3; ticket #42), no
/// database needed: every condition is in the query itself — <c>EffectiveFrom ≤ @now</c> as a
/// parameter, so no scheduled job is involved — and the joined version and document rows are
/// filtered by organization like the chunks. The results are asserted against PostgreSQL through
/// the real vector search in <see cref="KnowledgeRetrievalEligibilityTests"/>.
/// </summary>
public class RetrievableChunksSqlTests
{
    [Fact]
    public void The_rule_is_one_query_with_joins_to_the_version_and_document_and_a_not_exists_over_later_versions()
    {
        var organizationId = Guid.NewGuid();
        using var dbContext = TenancyTestContexts.Create(organizationId);
        var now = new DateTimeOffset(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);
        var query = new Vector(new float[] { 1, 0 });

        // What KnowledgeChunkVectorCollection.SearchAsync runs, with the filter #43 passes.
        var sql = dbContext.KnowledgeChunks
            .AsNoTracking()
            .Where(chunk => chunk.EmbeddingModel == "fake-test" && chunk.Embedding != null)
            .Where(RetrievableChunks.InKnowledgeBase(Guid.NewGuid(), now, "fake-test"))
            .Select(chunk => new { chunk, Distance = EF.Property<Vector>(chunk, nameof(KnowledgeChunk.Embedding)).CosineDistance(query) })
            .OrderBy(result => result.Distance)
            .Take(5)
            .ToQueryString();
        TestContext.Current.SendDiagnosticMessage(sql);

        sql.ShouldContain("INNER JOIN");
        sql.ShouldContain("NOT (k.\"Excluded\")");
        sql.ShouldContain("\"DisabledAt\" IS NULL");
        sql.ShouldContain("\"ReviewState\" = 'approved'");
        sql.ShouldContain("\"EffectiveFrom\" <= @now");
        sql.ShouldContain("\"ProcessingStatus\" IN ('ready', 'partially-readable')");
        sql.ShouldContain("NOT EXISTS");
        sql.ShouldContain("\"VersionNumber\" >");
        sql.ShouldContain("<=>", Case.Sensitive, "cosine distance");

        // The chunk, the version, the document and the later versions: each read for this
        // organization only.
        CountOf(sql, "\"OrganizationId\" = @").ShouldBeGreaterThanOrEqualTo(4);
        sql.ShouldContain(organizationId.ToString());
        sql.ShouldNotContain("KnowledgeFileContents");
    }

    [Fact]
    public void List_counts_use_the_same_version_rule()
    {
        using var dbContext = TenancyTestContexts.Create(Guid.NewGuid());

        var sql = KnowledgeItemStates
            .Of(dbContext.KnowledgeDocuments, dbContext.KnowledgeDocumentVersions, DateTimeOffset.UtcNow)
            .ToQueryString();
        TestContext.Current.SendDiagnosticMessage(sql);

        sql.ShouldContain("LEFT JOIN");
        sql.ShouldContain("\"EffectiveFrom\" <= @now");
        sql.ShouldContain("NOT EXISTS");
        sql.ShouldContain("\"DisabledAt\" IS NOT NULL");
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
