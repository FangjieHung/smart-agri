using Shouldly;

namespace SmartAgri.Api.Tests.Tenancy;

/// <summary>
/// Turning the organization filter off is only allowed when looking up an account for
/// sign-in (<c>AccountLookup</c>). Scans the production source, so a new call anywhere
/// in <c>apps/api/src</c> fails the build's test step. No database needed.
/// </summary>
public class IgnoreQueryFiltersSourceTests
{
    private const string Token = "IgnoreQueryFilters";

    [Fact]
    public void IgnoreQueryFilters_appears_only_in_AccountLookup()
    {
        var sourceRoot = Path.Combine(FindApiRoot(), "src");

        var occurrences = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: Path.GetRelativePath(sourceRoot, path), Line: index + 1, Text: line)))
            .Where(line => line.Text.Contains(Token, StringComparison.Ordinal))
            .ToList();

        occurrences
            .Select(occurrence => $"{occurrence.Path.Replace('\\', '/')}:{occurrence.Line}")
            .ShouldHaveSingleItem()
            .ShouldStartWith("SmartAgri.Infrastructure/Accounts/AccountLookup.cs:");
    }

    private static bool IsBuildOutput(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }

    private static string FindApiRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SmartAgri.slnx")))
            {
                return directory.FullName;
            }

            var nested = Path.Combine(directory.FullName, "apps", "api");
            if (File.Exists(Path.Combine(nested, "SmartAgri.slnx")))
            {
                return nested;
            }
        }

        throw new DirectoryNotFoundException("apps/api (SmartAgri.slnx) not found above the test output directory.");
    }
}
