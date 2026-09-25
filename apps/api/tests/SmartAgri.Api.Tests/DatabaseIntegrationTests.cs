using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests;

/// <summary>
/// Needs a real Postgres, so a Docker daemon. Run these with
/// <c>dotnet test --filter-not-trait "Category=Docker"</c> to exclude them, or
/// <c>dotnet test --filter-trait "Category=Docker"</c> to run only these (see
/// apps/api/README.md). One <see cref="PostgresFixture"/> container is shared by every
/// test in this class (per M1 skeleton plan, Slice 2: "each test class gets its own
/// database").
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class DatabaseIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public DatabaseIntegrationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Migrations_apply_idempotently()
    {
        await using var dbContext = _postgres.CreateDbContext();

        // First apply: creates the vector extension (and nothing else, yet).
        await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var pendingAfterFirstApply = await dbContext.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);
        pendingAfterFirstApply.ShouldBeEmpty();

        // Second apply against the same database must be a no-op, not an error.
        await Should.NotThrowAsync(() => dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken));
        var pendingAfterSecondApply = await dbContext.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);
        pendingAfterSecondApply.ShouldBeEmpty();

        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        appliedMigrations.ShouldContain(migration => migration.Contains("InitialCreate"));
    }

    [Fact]
    public async Task Health_ready_returns_ok_when_database_reachable()
    {
        await using var dbContext = _postgres.CreateDbContext();
        await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", _postgres.ConnectionString));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
