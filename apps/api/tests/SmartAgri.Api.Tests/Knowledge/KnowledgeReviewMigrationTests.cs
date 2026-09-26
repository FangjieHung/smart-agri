using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// The Slice 8 migration (<c>VersionReviewAndDisable</c>) on a database that already holds
/// documents: every existing version becomes <c>pending-review</c> — nothing uploaded before
/// approval existed is retrievable until a person approves it — and every document enabled.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class KnowledgeReviewMigrationTests : IClassFixture<PostgresFixture>
{
    private const string MigrationBefore = "20260926155234_Embeddings";

    private readonly PostgresFixture _postgres;

    public KnowledgeReviewMigrationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Existing_versions_become_pending_review_and_existing_documents_stay_enabled()
    {
        await using (var dbContext = _postgres.CreateDbContext())
        {
            await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBefore, CancellationToken);
        }

        var organization = new Organization(Guid.CreateVersion7(), "安心商行", "t" + Guid.NewGuid().ToString("N")[..12]);
        var now = new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
        var documentId = Guid.CreateVersion7();
        var versionId = Guid.CreateVersion7();
        await using (var dbContext = _postgres.CreateDbContext())
        {
            dbContext.Organizations.Add(organization);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        await using (var dbContext = _postgres.CreateDbContext(organization.Id))
        {
            var account = Account.Create(organization, "admin", "安心商行管理者", AccountRole.SmbAdmin);
            var knowledgeBase = KnowledgeBase.Create(organization.Id, account.Id, "退換貨政策", string.Empty, now);
            dbContext.Accounts.Add(account);
            dbContext.KnowledgeBases.Add(knowledgeBase);
            await dbContext.SaveChangesAsync(CancellationToken);

            // The tables as the earlier migrations left them: no review or disable columns yet.
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "KnowledgeDocuments" ("Id", "OrganizationId", "KnowledgeBaseId", "Kind", "Name", "CreatedAt")
                VALUES ({documentId}, {organization.Id}, {knowledgeBase.Id}, 'document', '退貨政策.pdf', {now})
                """,
                CancellationToken);
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "KnowledgeDocumentVersions" ("Id", "OrganizationId", "KnowledgeBaseId", "DocumentId", "VersionNumber",
                    "FileName", "ContentType", "SizeBytes", "Sha256", "ProcessingStatus", "Issue", "UploadBatchId",
                    "UploadedByAccountId", "UploadedAt", "UpdatedAt")
                VALUES ({versionId}, {organization.Id}, {knowledgeBase.Id}, {documentId}, 1,
                    '退貨政策.pdf', 'application/pdf', 3, {new string('a', 64)}, 'ready', NULL, NULL,
                    {account.Id}, {now}, {now})
                """,
                CancellationToken);
        }

        await using (var dbContext = _postgres.CreateDbContext())
        {
            await dbContext.Database.MigrateAsync(CancellationToken);
        }

        await using (var dbContext = _postgres.CreateDbContext(organization.Id))
        {
            var version = await dbContext.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(row => row.Id == versionId, CancellationToken);
            (version.ReviewState, version.EffectiveFrom, version.ApprovedByAccountId, version.ApprovedAt)
                .ShouldBe((KnowledgeReviewState.PendingReview, (DateTimeOffset?)null, (Guid?)null, (DateTimeOffset?)null));
            version.ProcessingStatus.ShouldBe(KnowledgeDocumentStatus.Ready, "ready, yet not in effect");
            var document = await dbContext.KnowledgeDocuments.AsNoTracking().SingleAsync(row => row.Id == documentId, CancellationToken);
            (document.DisabledAt, document.DisabledByAccountId, document.DisabledReason).ShouldBe(((DateTimeOffset?)null, (Guid?)null, (string?)null));
            (await dbContext.KnowledgeDocumentVersions.CountAsync(RetrievableChunks.CurrentEffectiveVersion(now.AddYears(1)), CancellationToken))
                .ShouldBe(0);

            // The constraints accept an approval made the normal way.
            var tracked = await dbContext.KnowledgeDocumentVersions.SingleAsync(row => row.Id == versionId, CancellationToken);
            tracked.Approve(tracked.UploadedByAccountId, now.AddHours(1), now.AddHours(1));
            await dbContext.SaveChangesAsync(CancellationToken);
            (await dbContext.KnowledgeDocumentVersions.CountAsync(RetrievableChunks.CurrentEffectiveVersion(now.AddHours(1)), CancellationToken))
                .ShouldBe(1);
        }
    }

    [Fact]
    public async Task The_database_refuses_approval_columns_that_disagree_with_the_review_state()
    {
        await using var dbContext = _postgres.CreateDbContext();
        await dbContext.Database.MigrateAsync(CancellationToken);
        var organization = new Organization(Guid.CreateVersion7(), "安心商行", "t" + Guid.NewGuid().ToString("N")[..12]);
        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync(CancellationToken);

        await using var scoped = _postgres.CreateDbContext(organization.Id);
        var account = Account.Create(organization, "admin", "安心商行管理者", AccountRole.SmbAdmin);
        var knowledgeBase = KnowledgeBase.Create(organization.Id, account.Id, "退換貨政策", string.Empty, DateTimeOffset.UtcNow);
        var document = KnowledgeDocument.CreateUploaded(knowledgeBase, "退貨政策.pdf", DateTimeOffset.UtcNow);
        var version = KnowledgeDocumentVersion.Create(document, 1, "退貨政策.pdf", "application/pdf", 3, new string('b', 64), account.Id, null, DateTimeOffset.UtcNow);
        scoped.Accounts.Add(account);
        scoped.KnowledgeBases.Add(knowledgeBase);
        scoped.KnowledgeDocuments.Add(document);
        await scoped.SaveChangesAsync(CancellationToken);

        // Approved while queued, pending with an approver, disabled without who and why: none
        // can be stored, whoever writes it.
        (await ViolatedConstraintAsync(() => scoped.KnowledgeDocumentVersions
                .Where(row => row.Id == version.Id)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(row => row.ReviewState, KnowledgeReviewState.Approved)
                    .SetProperty(row => row.EffectiveFrom, DateTimeOffset.UtcNow)
                    .SetProperty(row => row.ApprovedAt, DateTimeOffset.UtcNow)
                    .SetProperty(row => row.ApprovedByAccountId, account.Id), CancellationToken)))
            .ShouldBe("CK_KnowledgeDocumentVersions_Review");
        (await ViolatedConstraintAsync(() => scoped.KnowledgeDocumentVersions
                .Where(row => row.Id == version.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ApprovedByAccountId, account.Id), CancellationToken)))
            .ShouldBe("CK_KnowledgeDocumentVersions_Review");
        (await ViolatedConstraintAsync(() => scoped.KnowledgeDocuments
                .Where(row => row.Id == document.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.DisabledAt, DateTimeOffset.UtcNow), CancellationToken)))
            .ShouldBe("CK_KnowledgeDocuments_Disabled");
    }

    /// <summary>The name of the check constraint <paramref name="write"/> violated.</summary>
    private static async Task<string?> ViolatedConstraintAsync(Func<Task> write)
    {
        var exception = await Should.ThrowAsync<Exception>(write);
        for (var current = (Exception?)exception; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.CheckViolation } violation)
            {
                return violation.ConstraintName;
            }
        }

        throw new ShouldAssertException($"Expected a check violation, got {exception}");
    }
}
