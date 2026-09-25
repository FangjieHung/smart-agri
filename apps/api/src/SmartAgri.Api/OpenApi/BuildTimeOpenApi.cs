using System.Reflection;

namespace SmartAgri.Api.OpenApi;

/// <summary>
/// Detects whether the current process is Microsoft.Extensions.ApiDescription.Server's
/// build-time OpenAPI document generator, not the real Api. That tool (invoked from
/// <c>SmartAgri.Api.csproj</c>'s <c>GenerateOpenApiDocuments</c> target on every build; see
/// <c>apps/api/README.md</c>, "OpenAPI document and frontend types") runs this project's own
/// composition root — everything in <c>Program.cs</c> up to
/// <c>WebApplicationBuilder.Build()</c> — through <c>HostFactoryResolver</c> purely to read
/// endpoint metadata: it never calls <c>.Run()</c>, opens an HTTP listener, or queries the
/// database.
/// </summary>
/// <remarks>
/// Detected via the well-known entry assembly name that tool's host runs under
/// (<c>GetDocument.Insider</c>) — the pattern Microsoft's own docs recommend for telling
/// build-time OpenAPI/EF-style design-time tooling apart from a normal run. Used to skip
/// the parts of startup that would otherwise need real certificates or a reachable database
/// during that one build-time invocation (currently: <see cref="Authentication.TokenCredentials"/>'s
/// eager production certificate check in <c>AddSmartAgriAuthentication</c>). It must never
/// change what a real (non-generator) host does in any environment, including
/// <c>Development</c> — <c>TokenKeyStartupTests</c> covers that a non-Development host
/// without certificates still refuses to start.
/// </remarks>
public static class BuildTimeOpenApi
{
    private const string GeneratorEntryAssemblyName = "GetDocument.Insider";

    public static bool IsGeneratingDocument { get; } = DetectGenerator();

    private static bool DetectGenerator() =>
        Assembly.GetEntryAssembly()?.GetName().Name == GeneratorEntryAssemblyName;
}
