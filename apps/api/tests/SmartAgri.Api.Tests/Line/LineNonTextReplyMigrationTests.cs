using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;
using SmartAgri.Domain.Secrets;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Line;

/// <summary>
/// The #291 migration (<c>AddLineNonTextReply</c>) on a database that already holds a LINE channel:
/// the channel gets the reply every channel sent before the setting existed,
/// <see cref="AssistantLineChannel.DefaultNonTextReply"/>.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class LineNonTextReplyMigrationTests : IClassFixture<PostgresFixture>
{
    private const string MigrationBefore = "20261007054136_AddKnowledgeChunkFormat";

    private readonly PostgresFixture _postgres;

    public LineNonTextReplyMigrationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_existing_channel_gets_the_default_non_text_reply()
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
            var assistant = Assistant.Create(
                organization.Id, account.Id, "客服助理", "回答問題", null, AssistantTone.Friendly, string.Empty,
                AssistantKnowledgeScope.CompanyDataOnly, "找不到資料。", showCitations: true, keepConversations: false, now);
            dbContext.Accounts.Add(account);
            dbContext.Assistants.Add(assistant);
            dbContext.AssistantLineChannels.Add(new AssistantLineChannel(
                assistant, "@anxin-demo", "1650000000",
                new ProtectedSecret("cipher-secret", "s001", now), new ProtectedSecret("cipher-token", "t001", now),
                AssistantLineChannel.DefaultWelcomeMessage, "這段文字會隨欄位一起被移除", now));
            await dbContext.SaveChangesAsync(CancellationToken);
            assistantId = assistant.Id;
        }

        // Back to the schema before #291 (the column is dropped, the channel stays), then forward again.
        await using (var dbContext = _postgres.CreateDbContext())
        {
            await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBefore, CancellationToken);
            await dbContext.Database.MigrateAsync(CancellationToken);
        }

        await using (var dbContext = _postgres.CreateDbContext(organization.Id))
        {
            var channel = await dbContext.AssistantLineChannels.AsNoTracking()
                .SingleAsync(row => row.AssistantId == assistantId, CancellationToken);
            channel.NonTextReply.ShouldBe("目前只能回答文字問題。");
            channel.WelcomeMessage.ShouldBe(AssistantLineChannel.DefaultWelcomeMessage);
        }
    }
}
