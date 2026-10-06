using Microsoft.Extensions.Hosting;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// Configuration section <c>Widget</c>: where the files the Api serves to visitors' browsers live
/// (M5a plan §5 Slice 6). Both defaults are inside the app's content root, which is where the
/// Dockerfile copies them; relative paths are resolved against the content root.
/// </summary>
public sealed class WidgetOptions
{
    public const string SectionName = "Widget";

    public const string DefaultRootPath = "wwwroot/widget";

    public const string DefaultEmbedScriptPath = "wwwroot/embed.js";

    /// <summary>The widget's build output (<c>dist/smart-agri-widget/browser</c>: <c>index.html</c> and the
    /// hashed <c>main-*.js</c>, <c>chunk-*.js</c>, <c>styles-*.css</c>). <c>index.html</c> is only ever
    /// served by <c>GET /use/{assistantId}</c>, with that page's headers, never as a static file.</summary>
    public string? RootPath { get; set; }

    /// <summary>The loader customers' pages include: <c>apps/embed-loader/src/embed.js</c>, served as
    /// <c>/embed.js</c>.</summary>
    public string? EmbedScriptPath { get; set; }

    /// <summary><see cref="RootPath"/> (or its default) as an absolute path.</summary>
    public string ResolveRoot(IHostEnvironment environment) =>
        Resolve(environment, string.IsNullOrWhiteSpace(RootPath) ? DefaultRootPath : RootPath);

    /// <summary><see cref="EmbedScriptPath"/> (or its default) as an absolute path.</summary>
    public string ResolveEmbedScript(IHostEnvironment environment) =>
        Resolve(environment, string.IsNullOrWhiteSpace(EmbedScriptPath) ? DefaultEmbedScriptPath : EmbedScriptPath);

    private static string Resolve(IHostEnvironment environment, string path)
    {
        var trimmed = path.Trim();
        return Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(environment.ContentRootPath, trimmed));
    }
}
