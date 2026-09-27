using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using SmartAgri.Api.Ai;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Tenancy;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>
/// What <c>reindex</c> refuses before it touches the database: the <c>Fake</c> provider outside
/// Development/Testing (like startup), no provider at all, and bad arguments. Built from the
/// Api's registrations without starting a host, as the subcommand runs. What it does against a
/// database: <see cref="KnowledgeChunkVectorCollectionTests"/>.
/// </summary>
public sealed class ReindexCommandTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reindex_refuses_the_fake_provider_in_production_too()
    {
        using var services = Services("Production", ("Ai:Embedding:Provider", "Fake"), ("Ai:Embedding:Model", "fake-a"));
        var error = new StringWriter();

        var exit = await ReindexCommand.RunAsync(services, [], TextWriter.Null, error, CancellationToken);

        exit.ShouldBe(ReindexCommand.ExitUsage);
        error.ToString().ShouldContain("Fake is only allowed in the Development and Testing environments");
    }

    [Fact]
    public async Task Reindex_without_a_provider_or_with_bad_arguments_stops_before_touching_the_database()
    {
        using (var unconfigured = Services("Production"))
        {
            var error = new StringWriter();
            (await ReindexCommand.RunAsync(unconfigured, [], TextWriter.Null, error, CancellationToken)).ShouldBe(ReindexCommand.ExitFailed);
            error.ToString().ShouldContain("Ai:Embedding:Provider");
        }

        using var fake = Services("Development", ("Ai:Embedding:Provider", "Fake"), ("Ai:Embedding:Model", "fake-a"));
        foreach (var args in new[] { ["--batch-size", "0"], ["--batch-size=abc"], ["--organization"], new[] { "--everything" } })
        {
            var error = new StringWriter();
            (await ReindexCommand.RunAsync(fake, args, TextWriter.Null, error, CancellationToken)).ShouldBe(ReindexCommand.ExitUsage, string.Join(' ', args));
            error.ToString().ShouldContain("用法：reindex");
        }

        var output = new StringWriter();
        (await ReindexCommand.RunAsync(fake, ["--help"], output, TextWriter.Null, CancellationToken)).ShouldBe(ReindexCommand.ExitSuccess);
        output.ToString().ShouldStartWith("用法：reindex");
    }

    /// <summary>The Api's registrations as the one-shot subcommands see them: built, never started.</summary>
    private static ServiceProvider Services(string environment, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment })
            .AddDbContext<AppDbContext>(options => options.UseNpgsql(PostgresFixture.UnreachableConnectionString))
            .AddOrganizationTenancy()
            .AddEmbeddings(configuration)
            .BuildServiceProvider();
    }
}
