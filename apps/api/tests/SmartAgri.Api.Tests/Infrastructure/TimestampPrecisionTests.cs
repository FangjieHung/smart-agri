using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// <see cref="SmartAgri.Infrastructure.TimestampPrecisionInterceptor"/>: what a saved entity
/// holds in memory equals what PostgreSQL returns. The times below carry sub-microsecond
/// ticks on purpose, so this fails on every platform without the interceptor — not only
/// where the system clock happens to have 100 ns resolution (Linux CI).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class TimestampPrecisionTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset SubMicrosecond =
        new DateTimeOffset(2026, 9, 26, 1, 2, 3, TimeSpan.Zero).AddTicks(1_234_567);

    private readonly PostgresFixture _postgres;

    public TimestampPrecisionTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var dbContext = _postgres.CreateDbContext();
        await dbContext.Database.MigrateAsync(CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_added_entity_keeps_the_same_timestamps_the_database_returns()
    {
        var (organization, owner) = await CreateOrganizationWithOwnerAsync();

        var knowledgeBase = KnowledgeBase.Create(organization.Id, owner.Id, "退換貨政策", "客服用", SubMicrosecond);
        await using (var dbContext = _postgres.CreateDbContext(organization.Id))
        {
            dbContext.KnowledgeBases.Add(knowledgeBase);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        knowledgeBase.CreatedAt.ShouldBe(SubMicrosecond.AddTicks(-7));
        await using var readBack = _postgres.CreateDbContext(organization.Id);
        var stored = await readBack.KnowledgeBases.SingleAsync(kb => kb.Id == knowledgeBase.Id, CancellationToken);
        stored.CreatedAt.ShouldBe(knowledgeBase.CreatedAt);
        stored.UpdatedAt.ShouldBe(knowledgeBase.UpdatedAt);
    }

    [Fact]
    public async Task A_modified_entity_keeps_the_same_timestamps_the_database_returns()
    {
        var (organization, owner) = await CreateOrganizationWithOwnerAsync();
        var knowledgeBase = KnowledgeBase.Create(organization.Id, owner.Id, "商品指南", "內部", SubMicrosecond.AddTicks(-7));
        await using (var dbContext = _postgres.CreateDbContext(organization.Id))
        {
            dbContext.KnowledgeBases.Add(knowledgeBase);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var later = SubMicrosecond.AddMinutes(1);
        await using (var dbContext = _postgres.CreateDbContext(organization.Id))
        {
            var tracked = await dbContext.KnowledgeBases.SingleAsync(kb => kb.Id == knowledgeBase.Id, CancellationToken);
            tracked.ChangeDetails("商品指南（新版）", "內部", later);
            await dbContext.SaveChangesAsync(CancellationToken);
            tracked.UpdatedAt.ShouldBe(later.AddTicks(-7));
        }

        await using var readBack = _postgres.CreateDbContext(organization.Id);
        var stored = await readBack.KnowledgeBases.SingleAsync(kb => kb.Id == knowledgeBase.Id, CancellationToken);
        stored.UpdatedAt.ShouldBe(later.AddTicks(-7));
    }

    private async Task<(Organization Organization, Account Owner)> CreateOrganizationWithOwnerAsync()
    {
        var organization = new Organization(Guid.CreateVersion7(), "Timestamp Org", $"ts-{Guid.NewGuid():N}"[..15]);
        await using (var dbContext = _postgres.CreateDbContext(organizationId: null))
        {
            dbContext.Organizations.Add(organization);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        var owner = Account.Create(organization, "owner", "Owner", AccountRole.SmbAdmin);
        await using (var dbContext = _postgres.CreateDbContext(organization.Id))
        {
            dbContext.Accounts.Add(owner);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        return (organization, owner);
    }
}
