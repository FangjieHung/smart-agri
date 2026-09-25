using System.Text.Json;
using Shouldly;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// <c>dotnet ef database update</c> connects with <see cref="AppDbContextDesignTimeFactory"/>'s
/// connection string, so it must be the local development database the Api itself uses
/// (and <c>deploy/docker-compose.dev.yml</c> creates). No database needed.
/// </summary>
public class AppDbContextDesignTimeFactoryTests
{
    [Fact]
    public void Design_time_connection_string_equals_the_development_one()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FindRepoFile("apps", "api", "src", "SmartAgri.Api", "appsettings.Development.json")));
        var development = json.RootElement.GetProperty("ConnectionStrings").GetProperty("Default").GetString();

        AppDbContextDesignTimeFactory.DevelopmentConnectionString.ShouldBe(development);
    }

    private static string FindRepoFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{string.Join('/', relativePath)} not found above the test output directory.");
    }
}
