using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
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
/// and its refusal to run without <c>SEED_DEMO_PASSWORD</c>. No Docker needed — this never
/// opens a database connection (see the assertion in
/// <see cref="SeedAsync_without_the_password_configured_fails_before_touching_the_database"/>).
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
    public async Task SeedAsync_without_the_password_configured_fails_before_touching_the_database()
    {
        var configuration = new ConfigurationBuilder().Build();
        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresFixture.UnreachableConnectionString)
            .Options;
        var seeder = new DevelopmentSeeder(configuration, dbContextOptions, NullLogger<DevelopmentSeeder>.Instance);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => seeder.SeedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("SEED_DEMO_PASSWORD");
        exception.Message.ShouldContain(".env.example");
    }

    [Fact]
    public async Task SeedAsync_with_a_blank_password_is_also_refused()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(DevelopmentSeeder.PasswordConfigurationKey, "   ")])
            .Build();
        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresFixture.UnreachableConnectionString)
            .Options;
        var seeder = new DevelopmentSeeder(configuration, dbContextOptions, NullLogger<DevelopmentSeeder>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() => seeder.SeedAsync(TestContext.Current.CancellationToken));
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
