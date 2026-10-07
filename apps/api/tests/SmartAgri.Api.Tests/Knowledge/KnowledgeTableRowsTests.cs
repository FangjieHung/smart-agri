using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Assistants;
using SmartAgri.Infrastructure.Knowledge;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// #324 against real PostgreSQL: processing records each table row's table, and
/// <see cref="EfKnowledgeTableRows"/> reads one table's not-excluded rows, in the organization in
/// scope only, so <see cref="GroundedAnswerService"/> sends a retrieved row's whole table — and
/// nothing excluded, of another table, or of another organization.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class KnowledgeTableRowsTests : IClassFixture<AuthHostFixture>
{
    private const string StoreInfo = """
        # 門市

        門市資料如下。

        | 項目 | 內容 |
        | --- | --- |
        | 店名 | 安心商行青禾門市 |
        | 營業時間 | 09:00–18:00 |
        | 公休日 | 每週三 |

        | 品項 | 價格 |
        | --- | --- |
        | 糙米飯糰 | 45 元 |
        """;

    private const string Name = "項目：店名\n內容：安心商行青禾門市";
    private const string Hours = "項目：營業時間\n內容：09:00–18:00";
    private const string Closed = "項目：公休日\n內容：每週三";
    private const string RiceBall = "品項：糙米飯糰\n價格：45 元";

    private readonly AuthHostFixture _host;

    public KnowledgeTableRowsTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_retrieved_row_is_answered_with_its_tables_other_not_excluded_rows_in_order_and_nothing_else()
    {
        var owner = await KnowledgeTestOwner.CreateAsync(_host);
        var versionId = await UploadAsync(owner);
        var other = await KnowledgeTestOwner.CreateAsync(_host, "別的商行");
        var otherVersionId = await UploadAsync(other);

        // Processing numbered the section's two tables; the text has none.
        var chunks = await ChunksAsync(owner, versionId);
        chunks.Select(chunk => (chunk.Text, chunk.TableIndex)).ShouldBe(
        [
            ("門市資料如下。", null),
            (Name, 0),
            (Hours, 0),
            (Closed, 0),
            (RiceBall, 1),
        ]);
        var closed = chunks.Single(chunk => chunk.Text == Closed);
        var table = new KnowledgeTableKey(versionId, closed.UnitOrdinal, 0);

        // The owner excludes the shop name.
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            await dbContext.KnowledgeChunks.Where(chunk => chunk.VersionId == versionId && chunk.Text == Name)
                .ExecuteUpdateAsync(set => set.SetProperty(chunk => chunk.Excluded, true), CancellationToken);
        }

        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var rows = await new EfKnowledgeTableRows(dbContext).FindAsync([table], CancellationToken);
            rows.OrderBy(row => row.Ordinal).Select(row => (row.Text, row.Table)).ShouldBe([(Hours, table), (Closed, table)]);
        }

        // Another organization asking for this table (or its own one's key) sees nothing of it.
        await using (var dbContext = _host.Postgres.CreateDbContext(other.Organization.Id))
        {
            (await new EfKnowledgeTableRows(dbContext).FindAsync([table], CancellationToken)).ShouldBeEmpty();
            (await new EfKnowledgeTableRows(dbContext).FindAsync([table with { VersionId = otherVersionId }], CancellationToken))
                .Select(row => row.Text).ShouldBe([Name, Hours, Closed], ignoreOrder: true, "its own rows, none excluded");
        }

        // The answer pipeline on the real database: the closed day was retrieved alone.
        var chat = new CapturingChatClient("週二有營業，每週三公休。[1]");
        GroundedAnswerResult result;
        await using (var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id))
        {
            var passage = new RetrievedKnowledgePassage(
                closed.Id, owner.KnowledgeBaseId, closed.DocumentId, "門市資訊.md", versionId, 1, KnowledgeVersionState.Effective,
                closed.LocationLabel, closed.Text, 0.457, null, new KnowledgeTablePosition(closed.UnitOrdinal, 0, closed.Ordinal));
            var service = new GroundedAnswerService(
                new EfAnswerKnowledgeBases(dbContext),
                new OnePassageRetriever(passage),
                new EfKnowledgeTableRows(dbContext),
                chat,
                _host.Factory.Services.GetRequiredService<GroundedAnswerMetrics>(),
                new NoOutcomes(),
                new FixedOrganizationContext(owner.Organization.Id),
                TimeProvider.System);
            var profile = new GroundedAnswerProfile(
                "門市小幫手", "回答門市問題", AssistantTone.Concise, string.Empty, AssistantKnowledgeScope.CompanyDataOnly,
                "查無結果", null, owner.AccountId, [owner.KnowledgeBaseId]);
            result = await service.AnswerAsync(new GroundedAnswerRequest(profile, "週二有營業嗎？", [], owner.AccountId, null), CancellationToken);
        }

        var system = chat.System.ShouldNotBeNull();
        system.ShouldContain(Hours + KnowledgeTableExpansion.RowSeparator + Closed, Case.Sensitive);
        system.ShouldNotContain("安心商行青禾門市", Case.Sensitive, "an excluded row is never sent");
        system.ShouldNotContain("糙米飯糰", Case.Sensitive, "another table of the same section");
        system.ShouldNotContain("[2] 文件：", Case.Sensitive);
        var citation = result.Reply.Citations.ShouldHaveSingleItem();
        (citation.ChunkId, citation.DocumentId).ShouldBe((closed.Id, closed.DocumentId));
        result.TableExpansion.ShouldBe(new GroundedTableExpansion(1, 1, Hours.EnumerateRunes().Count(), false));
    }

    private async Task<Guid> UploadAsync(KnowledgeTestOwner owner)
    {
        var response = await owner.PostFileAsync($"/api/v1/knowledge-bases/{owner.KnowledgeBaseId}/documents", "門市資訊.md", Encoding.UTF8.GetBytes(StoreInfo));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        await _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeDocumentVersions
            .Where(version => version.KnowledgeBaseId == owner.KnowledgeBaseId)
            .Select(version => version.Id)
            .SingleAsync(CancellationToken);
    }

    private async Task<List<KnowledgeChunk>> ChunksAsync(KnowledgeTestOwner owner, Guid versionId)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(owner.Organization.Id);
        return await dbContext.KnowledgeChunks.AsNoTracking()
            .Where(chunk => chunk.VersionId == versionId)
            .OrderBy(chunk => chunk.UnitOrdinal).ThenBy(chunk => chunk.Ordinal)
            .ToListAsync(CancellationToken);
    }

    /// <summary>Returns one passage whatever the question (retrieval itself is not under test).</summary>
    private sealed class OnePassageRetriever(RetrievedKnowledgePassage passage) : IKnowledgeRetriever
    {
        public KnowledgeRetrievalSettings Settings => KnowledgeRetrievalSettings.Default;

        public Task<KnowledgeRetrievalResult> RetrieveAsync(KnowledgeRetrievalQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new KnowledgeRetrievalResult([passage], query.MinScore ?? Settings.MinScore));
    }

    /// <summary>Answers with a fixed text and keeps the system prompt it was given.</summary>
    private sealed class CapturingChatClient(string answer) : IChatClient
    {
        public string? System { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            System = messages.First().Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            System = messages.First().Text;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, answer);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class NoOutcomes : IAnswerOutcomeRecorder
    {
        public Task RecordAsync(
            Guid organizationId, Guid? assistantId, AnswerOutcomeChannel channel, AnswerReplyKind replyKind, AnswerRejectionReason? rejectionReason,
            IReadOnlyCollection<Guid> citedDocumentIds, bool usedCandidates, DateTimeOffset at, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordDatabaseQueryAsync(
            Guid organizationId, Guid assistantId, AnswerDatabaseQueryResult result, DateTimeOffset at, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
