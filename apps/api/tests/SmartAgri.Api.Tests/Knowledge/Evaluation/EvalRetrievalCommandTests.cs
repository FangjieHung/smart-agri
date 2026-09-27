using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using SmartAgri.Api.Ai;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Knowledge.Evaluation;
using SmartAgri.Api.Tenancy;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Knowledge.Evaluation;

/// <summary>
/// What <c>eval-retrieval</c> refuses before it touches the database: another environment than
/// Development/Testing, no embedding provider, a broken set, bad arguments. Built from the Api's
/// registrations without starting a host, as the subcommand runs, against a database that cannot
/// be reached. What it does against a database: <see cref="RetrievalEvaluationTests"/>.
/// </summary>
public sealed class EvalRetrievalCommandTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task It_refuses_to_run_outside_development_and_testing(string environment)
    {
        using var services = Services(environment, ("Ai:Embedding:Provider", "OpenAICompatible"), ("Ai:Embedding:Model", "e5"), ("Ai:Embedding:Endpoint", "http://localhost:1/v1"));
        var error = new StringWriter();

        var exit = await EvalRetrievalCommand.RunAsync(services, [], TextWriter.Null, error, CancellationToken);

        exit.ShouldBe(EvalRetrievalCommand.ExitUsage);
        error.ToString().ShouldContain($"只能在 Development 或 Testing 環境執行（目前是 {environment}）");
    }

    [Fact]
    public async Task Without_a_provider_or_with_a_broken_set_it_stops_before_touching_the_database()
    {
        using (var unconfigured = Services("Development"))
        {
            var error = new StringWriter();
            (await EvalRetrievalCommand.RunAsync(unconfigured, [], TextWriter.Null, error, CancellationToken)).ShouldBe(EvalRetrievalCommand.ExitFailed);
            error.ToString().ShouldContain("Ai:Embedding:Provider");
        }

        using var fake = Services("Development", ("Ai:Embedding:Provider", "Fake"), ("Ai:Embedding:Model", "fake-a"));
        var empty = Directory.CreateTempSubdirectory("smartagri-eval-empty-");
        try
        {
            var error = new StringWriter();
            (await EvalRetrievalCommand.RunAsync(fake, ["--set", empty.FullName], TextWriter.Null, error, CancellationToken)).ShouldBe(EvalRetrievalCommand.ExitUsage);
            error.ToString().ShouldContain("找不到 documents.json");
            error.ToString().ShouldContain("找不到 questions.json");
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Bad_arguments_print_the_usage_and_help_prints_it_too()
    {
        using var fake = Services("Development", ("Ai:Embedding:Provider", "Fake"), ("Ai:Embedding:Model", "fake-a"));
        foreach (var args in new[] { ["--timeout", "-1"], ["--timeout=abc"], ["--set"], ["--report="], new[] { "--everything" } })
        {
            var error = new StringWriter();
            (await EvalRetrievalCommand.RunAsync(fake, args, TextWriter.Null, error, CancellationToken)).ShouldBe(EvalRetrievalCommand.ExitUsage, string.Join(' ', args));
            error.ToString().ShouldContain("用法：eval-retrieval");
        }

        var output = new StringWriter();
        (await EvalRetrievalCommand.RunAsync(fake, ["--help"], output, TextWriter.Null, CancellationToken)).ShouldBe(EvalRetrievalCommand.ExitSuccess);
        output.ToString().ShouldStartWith("用法：eval-retrieval");

        EvalRetrievalCommand.TryParse(["--set=a", "--report", "b.md", "--timeout", "30"], out var parsed, out _).ShouldBeTrue();
        parsed.ShouldBe(new EvalRetrievalCommand.Arguments("a", "b.md", TimeSpan.FromSeconds(30), Help: false));
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
            .AddBackgroundJobs(configuration)
            .AddKnowledge(configuration)
            .AddEmbeddings(configuration)
            .BuildServiceProvider();
    }
}
