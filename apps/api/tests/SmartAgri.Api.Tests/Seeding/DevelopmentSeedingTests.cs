using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmartAgri.Api.Seeding;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Seeding;

namespace SmartAgri.Api.Tests.Seeding;

/// <summary>
/// <see cref="DevelopmentSeedingServiceCollectionExtensions"/> and the parts of
/// <see cref="DevelopmentSeeder"/> that don't need a database: whether it is registered,
/// and that it skips seeding (with a warning, and without ever touching the database)
/// when <c>SEED_DEMO_PASSWORD</c> is not set. No Docker needed — this never opens a
/// database connection (see the assertion in
/// <see cref="SeedAsync_without_the_password_configured_skips_seeding_and_logs_a_warning"/>).
/// </summary>
public class DevelopmentSeedingTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public void DevelopmentSeeder_is_registered_only_in_development(string environmentName, bool expectRegistered)
    {
        using var provider = BuildProvider(environmentName);

        (provider.GetService<DevelopmentSeeder>() is not null).ShouldBe(expectRegistered);
    }

    [Fact]
    public async Task SeedDevelopmentDataAsync_is_a_no_op_outside_development()
    {
        using var provider = BuildProvider("Production");

        // Would throw connecting to the unreachable database if it ever tried to seed.
        await provider.SeedDevelopmentDataAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SeedAsync_without_the_password_configured_skips_seeding_and_logs_a_warning()
    {
        var configuration = new ConfigurationBuilder().Build();
        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresFixture.UnreachableConnectionString)
            .Options;
        var logger = new ListLogger<DevelopmentSeeder>();
        var seeder = new DevelopmentSeeder(configuration, dbContextOptions, logger);

        // Would throw connecting to the unreachable database if this ever tried to seed.
        await seeder.SeedAsync(TestContext.Current.CancellationToken);

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("SEED_DEMO_PASSWORD");
        warning.Message.ShouldContain(".env.example");
    }

    [Fact]
    public async Task SeedAsync_with_a_blank_password_also_skips_seeding_and_logs_a_warning()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(DevelopmentSeeder.PasswordConfigurationKey, "   ")])
            .Build();
        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresFixture.UnreachableConnectionString)
            .Options;
        var logger = new ListLogger<DevelopmentSeeder>();
        var seeder = new DevelopmentSeeder(configuration, dbContextOptions, logger);

        await seeder.SeedAsync(TestContext.Current.CancellationToken);

        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    /// <summary>Every entry <see cref="DevelopmentSeeder"/> logs, in order — used to prove
    /// it warns instead of throwing when <c>SEED_DEMO_PASSWORD</c> is unset.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static ServiceProvider BuildProvider(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresFixture.UnreachableConnectionString)
            .Options);
        services.AddDevelopmentSeeding(new HostingEnvironment { EnvironmentName = environmentName });
        return services.BuildServiceProvider();
    }
}
