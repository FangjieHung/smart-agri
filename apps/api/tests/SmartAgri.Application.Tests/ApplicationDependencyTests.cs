using System.Reflection;
using System.Xml.Linq;
using Shouldly;
using SmartAgri.Application.Knowledge;

namespace SmartAgri.Application.Tests;

/// <summary>
/// The Application layer may depend only on <c>SmartAgri.Domain</c> and on abstraction
/// packages (M2 plan §3): Npgsql, EF Core, ASP.NET Core, PdfPig, OpenXml and vendor SDKs
/// belong in Infrastructure or Api, so business code never depends on a provider directly
/// (backend-stack ADR). Reads the project file itself, so adding a reference fails here
/// before any code uses it.
/// </summary>
public class ApplicationDependencyTests
{
    private static readonly string[] AllowedProjectReferences = ["SmartAgri.Domain"];

    /// <summary>Stable abstraction boundaries named by the backend-stack ADR; the M2 slices
    /// that need them (embeddings, vector search) add the actual references.</summary>
    private static readonly string[] AllowedPackageReferences =
    [
        "Microsoft.Extensions.AI.Abstractions",
        "Microsoft.Extensions.VectorData.Abstractions",
    ];

    [Fact]
    public void The_project_file_references_only_Domain_and_allowed_abstraction_packages()
    {
        var project = XDocument.Load(ApplicationProjectPath());

        (project.Root!.Attribute("Sdk")?.Value).ShouldBe(
            "Microsoft.NET.Sdk",
            "the Web SDK would bring in all of ASP.NET Core");

        ProjectReferenceNames(project).ShouldAllBe(name => AllowedProjectReferences.Contains(name));
        Includes(project, "PackageReference").ShouldAllBe(name => AllowedPackageReferences.Contains(name));
        Includes(project, "FrameworkReference").ShouldBeEmpty("e.g. Microsoft.AspNetCore.App");
        Includes(project, "Reference").ShouldBeEmpty("no direct assembly references");
    }

    [Fact]
    public void The_project_file_check_is_not_vacuous()
    {
        // If the file moved or its references were restructured (e.g. into an imported
        // file), the check above would pass on nothing.
        ProjectReferenceNames(XDocument.Load(ApplicationProjectPath())).ShouldBe(["SmartAgri.Domain"]);
    }

    [Fact]
    public void No_shared_build_file_adds_references_to_every_project()
    {
        // Directory.Build.props/.targets apply to Application too, so a reference there
        // would bypass the project-file check.
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            var path = Path.Combine(ApiRoot(), name);
            if (!File.Exists(path))
            {
                continue;
            }

            var shared = XDocument.Load(path);
            foreach (var element in new[] { "PackageReference", "ProjectReference", "FrameworkReference", "Reference" })
            {
                Includes(shared, element).ShouldBeEmpty($"{name} must not add a {element}");
            }
        }
    }

    [Fact]
    public void The_compiled_assembly_references_only_the_runtime_Domain_and_allowed_abstractions()
    {
        var referenced = typeof(KnowledgeSharingPolicy).Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name!)
            .ToList();

        referenced.ShouldContain("SmartAgri.Domain");
        referenced
            .Where(name => !IsRuntimeAssembly(name))
            .ShouldAllBe(name => AllowedProjectReferences.Contains(name) || AllowedPackageReferences.Contains(name));
    }

    private static bool IsRuntimeAssembly(string name) =>
        name is "System" or "mscorlib" or "netstandard" || name.StartsWith("System.", StringComparison.Ordinal);

    private static IEnumerable<string> ProjectReferenceNames(XDocument project) =>
        Includes(project, "ProjectReference").Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));

    /// <summary>The <c>Include</c> (or <c>Update</c>) of every <paramref name="element"/>,
    /// with or without an MSBuild XML namespace.</summary>
    private static List<string> Includes(XDocument document, string element) =>
    [
        .. document.Descendants()
            .Where(candidate => candidate.Name.LocalName == element)
            .Select(candidate => candidate.Attribute("Include")?.Value ?? candidate.Attribute("Update")?.Value ?? string.Empty),
    ];

    private static string ApplicationProjectPath() =>
        Path.Combine(ApiRoot(), "src", "SmartAgri.Application", "SmartAgri.Application.csproj");

    private static string ApiRoot()
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
