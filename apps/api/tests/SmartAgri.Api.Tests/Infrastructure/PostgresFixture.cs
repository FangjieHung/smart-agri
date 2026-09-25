using Microsoft.EntityFrameworkCore;
using SmartAgri.Infrastructure;
using Testcontainers.PostgreSql;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// Starts one Postgres container per test class (xUnit's <c>IClassFixture{T}</c>
/// creates a single fixture instance shared by every test in that class, and disposes
/// it after the last one runs). Uses the same pgvector image as
/// <c>deploy/docker-compose*.yml</c> so migrations behave identically to production.
/// Requires a running Docker daemon; every test (or fixture) that uses this must carry
/// <see cref="TestCategories.Docker"/>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Image = "pgvector/pgvector:0.8.6-pg18";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase("smartagri_test")
        .WithUsername("smartagri_test")
        .WithPassword("smartagri_test")
        .Build();

    /// <summary>A connection string that cannot reach any database, for the tests that
    /// exercise the "database unreachable" path without needing Docker at all.</summary>
    public const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=unreachable;Username=unreachable;Password=unreachable;Timeout=1;Command Timeout=1";

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public AppDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(ConnectionString);
        return new AppDbContext(optionsBuilder.Options);
    }
}
