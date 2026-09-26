using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmartAgri.Api.Seeding;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Seeding;

/// <summary>
/// <see cref="DemoKnowledgeSeeder"/> without a database: registered only in Development, and off
/// — never opening a connection — unless <c>SEED_DEMO_KNOWLEDGE</c> is <c>true</c>. What it seeds:
/// <see cref="Knowledge.Evaluation.RetrievalEvaluationTests"/>.
/// </summary>
public sealed class DemoKnowledgeSeederTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    public void Only_true_turns_it_on(string? value, bool enabled)
    {
        DemoKnowledgeSeeder.IsEnabled(Configuration(value)).ShouldBe(enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task Turned_off_it_does_nothing_and_never_opens_the_database(string? value)
    {
        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresFixture.UnreachableConnectionString)
            .Options;

        // An empty container: when off, it must not need anything (the importer, the job runner).
        var seeder = new DemoKnowledgeSeeder(Configuration(value), dbContextOptions, new ServiceCollection().BuildServiceProvider(), NullLogger<DemoKnowledgeSeeder>.Instance);

        // Would throw connecting to the unreachable database if it ever tried to seed.
        await seeder.SeedAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public void It_is_registered_only_in_development(string environmentName, bool expectRegistered)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration("true"));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(PostgresFixture.UnreachableConnectionString).Options);
        services.AddDevelopmentSeeding(new HostingEnvironment { EnvironmentName = environmentName });
        using var provider = services.BuildServiceProvider();

        (provider.GetService<DemoKnowledgeSeeder>() is not null).ShouldBe(expectRegistered);
    }

    private static IConfiguration Configuration(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : [new KeyValuePair<string, string?>(DemoKnowledgeSeeder.ConfigurationKey, value)])
            .Build();
}
