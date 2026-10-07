using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Assistants;

/// <summary>
/// The #224 migration (<c>AddAssistantAudience</c>) on a database that already holds assistants:
/// each gets <see cref="AssistantAudience.AccountMembers"/>, the only audience M3 accepted.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class AssistantAudienceMigrationTests : IClassFixture<PostgresFixture>
{
    private const string MigrationBefore = "20261007082324_AddKnowledgeChunkTableIndex";

    private readonly PostgresFixture _postgres;

    public AssistantAudienceMigrationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_existing_assistant_gets_account_members()
    {
        await using (var dbContext = _postgres.CreateDbContext())
        {
            await dbContext.Database.MigrateAsync(CancellationToken);
        }

        var now = new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);
        var organization = new Organization(Guid.CreateVersion7(), "安心商行", "t" + Guid.NewGuid().ToString("N")[..12]);
        await using (var dbContext = _postgres.CreateDbContext())
        {
            dbContext.Organizations.Add(organization);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        Guid assistantId;
        await using (var dbContext = _postgres.CreateDbContext(organization.Id))
        {
            var account = Account.Create(organization, "admin", "安心商行管理者", AccountRole.SmbAdmin);
            // Saved as external first, so the value after the round trip can only come from the migration.
            var assistant = Assistant.Create(
                organization.Id, account.Id, "客服助理", "回答問題", null, AssistantTone.Friendly, string.Empty,
                AssistantKnowledgeScope.CompanyDataOnly, "找不到資料。", showCitations: true, keepConversations: false, now,
                AssistantAudience.MembersAndExternalCustomers);
            dbContext.Accounts.Add(account);
            dbContext.Assistants.Add(assistant);
            await dbContext.SaveChangesAsync(CancellationToken);
            assistantId = assistant.Id;
        }

        // Back to the schema before #224 (the column is dropped, the assistant stays), then forward again.
        await using (var dbContext = _postgres.CreateDbContext())
        {
            await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBefore, CancellationToken);
            await dbContext.Database.MigrateAsync(CancellationToken);
        }

        await using (var dbContext = _postgres.CreateDbContext(organization.Id))
        {
            var assistant = await dbContext.Assistants.AsNoTracking()
                .SingleAsync(row => row.Id == assistantId, CancellationToken);
            assistant.Audience.ShouldBe(AssistantAudience.AccountMembers);
            assistant.Name.ShouldBe("客服助理");
        }
    }
}
