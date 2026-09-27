using Shouldly;

namespace SmartAgri.Api.Tests.Tenancy;

/// <summary>
/// The ways around organization isolation are each allowed in one place only. Scans the
/// production source, so a new use anywhere in <c>apps/api/src</c> fails the build's test
/// step. No database needed.
/// <list type="bullet">
/// <item>Turning the organization filter off: only when looking up an account for sign-in
/// (<c>AccountLookup</c>).</item>
/// <item>Raw SQL, which neither the filter nor the write guard sees: only claiming
/// background jobs across organizations (<c>JobClaimer</c>, M2 plan §3).</item>
/// <item>Acting for a background job's organization: only the job runner.</item>
/// </list>
/// </summary>
public class IgnoreQueryFiltersSourceTests
{
    private const string Token = "IgnoreQueryFilters";

    /// <summary>
    /// EF Core's raw SQL entry points (<c>FromSql</c>/<c>FromSqlRaw</c>/<c>FromSqlInterpolated</c>,
    /// <c>SqlQuery</c>/<c>SqlQueryRaw</c>, <c>ExecuteSql</c>/<c>ExecuteSqlRaw</c>/<c>ExecuteSqlInterpolated</c>,
    /// with or without <c>Async</c>) and ADO.NET/Npgsql commands on the context's connection.
    /// </summary>
    private static readonly string[] RawSqlTokens =
        ["FromSql", "SqlQuery", "ExecuteSql", "NpgsqlCommand", "NpgsqlBatch", "CreateCommand", "GetDbConnection"];

    [Fact]
    public void IgnoreQueryFilters_appears_only_in_AccountLookup()
    {
        Occurrences(Token)
            .ShouldHaveSingleItem()
            .ShouldStartWith("SmartAgri.Infrastructure/Accounts/AccountLookup.cs:");
    }

    [Fact]
    public void Raw_SQL_appears_only_in_JobClaimer()
    {
        var occurrences = RawSqlTokens.SelectMany(Occurrences).ToList();

        // Not vacuous: the claim itself is raw SQL, so an empty result means the scan (or
        // JobClaimer) moved, not that the rule holds.
        occurrences.ShouldNotBeEmpty();
        occurrences.ShouldAllBe(occurrence => occurrence.StartsWith("SmartAgri.Infrastructure/Jobs/JobClaimer.cs:"));
    }

    [Fact]
    public void Only_the_job_runner_enters_a_jobs_organization()
    {
        // JobOrganizationScope switches a scope's organization; only JobRunner may do that
        // (besides the type itself and its registration in AddOrganizationTenancy).
        Occurrences("JobOrganizationScope")
            .Select(occurrence => occurrence[..occurrence.LastIndexOf(':')])
            .Distinct()
            .ShouldBe(
                [
                    "SmartAgri.Api/Jobs/JobRunner.cs",
                    "SmartAgri.Api/Tenancy/JobOrganizationScope.cs",
                    "SmartAgri.Api/Tenancy/TenancyServiceCollectionExtensions.cs",
                ],
                ignoreOrder: true);
    }

    /// <summary>Every <c>path:line</c> (relative to <c>apps/api/src</c>, with <c>/</c>) whose
    /// line contains <paramref name="token"/>.</summary>
    private static List<string> Occurrences(string token)
    {
        var sourceRoot = Path.Combine(FindApiRoot(), "src");

        return
        [
            .. Directory
                .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsBuildOutput(path))
                .SelectMany(path => File.ReadLines(path)
                    .Select((line, index) => (Path: Path.GetRelativePath(sourceRoot, path), Line: index + 1, Text: line)))
                .Where(line => line.Text.Contains(token, StringComparison.Ordinal))
                .Select(occurrence => $"{occurrence.Path.Replace('\\', '/')}:{occurrence.Line}"),
        ];
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
