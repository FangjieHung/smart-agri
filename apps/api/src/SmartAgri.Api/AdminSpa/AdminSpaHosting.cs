using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using SmartAgri.Api.PublicChannels;

namespace SmartAgri.Api.AdminSpa;

/// <summary>
/// The admin SPA served from the Api's own origin (pre-launch plan §3 E, #306), when
/// <c>Admin:RootPath</c> is set: one address for the admin, the API, LINE and the website embed. The
/// admin calls <c>/api/v1/*</c> with relative paths and signs in through <c>/connect/*</c> on the same
/// origin, which is what its <c>SameSite=Strict</c> sign-in cookie and <c>Authentication:LoginPath</c>
/// already assume.
/// </summary>
/// <remarks>
/// A middleware, not an endpoint: it only ever answers a <c>GET</c>/<c>HEAD</c> that <b>no endpoint
/// matched</b> (not even with another method, which routing turns into its own <c>405</c> endpoint), and
/// whose first path segment is not one of the Api's (<see cref="ApiFirstSegments"/>: a fixed list plus the
/// first literal segment of every mapped route, so a new endpoint group cannot fall through to the SPA).
/// So every API answer — including <c>401</c> for an unknown <c>/api</c> path and <c>405</c> for a known one
/// with the wrong method — is unchanged. It runs before authorization, whose fallback policy would
/// otherwise answer <c>401</c> to anything without an endpoint.
/// </remarks>
public static partial class AdminSpaHosting
{
    public const string IndexFileName = "index.html";

    /// <summary>Paths that belong to the Api even where nothing is mapped: OpenIddict answers
    /// <c>/.well-known/*</c> in its own middleware, <c>/openapi</c> is mapped in Development only, and an
    /// unknown path under the others must stay the Api's <c>404</c>/<c>401</c>, never the admin.</summary>
    public static readonly IReadOnlyList<string> FixedApiFirstSegments =
        ["api", "connect", ".well-known", "health", "use", "widget", "embed.js", "openapi"];

    private const string ImmutableCache = "public, max-age=31536000, immutable";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IServiceCollection AddAdminSpa(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdminSpaOptions>()
            .Bind(configuration.GetSection(AdminSpaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AdminSpaOptions>, AdminSpaOptions.Validator>();
        return services;
    }

    /// <summary>Serves the admin when <c>Admin:RootPath</c> is set; a no-op otherwise. Options are read per
    /// request (never while the pipeline is declared), so a bad setting fails startup through
    /// <c>ValidateOnStart</c>, not here.</summary>
    public static WebApplication UseAdminSpa(this WebApplication app)
    {
        IEndpointRouteBuilder routes = app;
        var apiFirstSegments = new Lazy<FrozenSet<string>>(
            () => ApiFirstSegments(routes.DataSources.SelectMany(source => source.Endpoints)),
            LazyThreadSafetyMode.ExecutionAndPublication);

        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<AdminSpaOptions>>().Value;
            if (!options.IsServed
                || context.GetEndpoint() is not null
                || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                || IsApiPath(context.Request.Path, apiFirstSegments.Value))
            {
                await next(context);
                return;
            }

            var root = options.ResolveRoot(context.RequestServices.GetRequiredService<IHostEnvironment>())!;
            await ServeAsync(context, root);
        });

        return app;
    }

    /// <summary>The fixed list plus the first literal segment of every route pattern (<c>api</c>,
    /// <c>connect</c>, <c>health</c>, <c>use</c>, <c>widget</c>, <c>embed.js</c>, ...), case-insensitive.</summary>
    internal static FrozenSet<string> ApiFirstSegments(IEnumerable<Endpoint> endpoints)
    {
        var segments = new HashSet<string>(FixedApiFirstSegments, StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.RoutePattern.PathSegments is [{ Parts: [RoutePatternLiteralPart literal] }, ..])
            {
                segments.Add(literal.Content);
            }
        }

        return segments.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsApiPath(PathString path, FrozenSet<string> apiFirstSegments)
    {
        var value = path.Value ?? string.Empty;
        var start = value.StartsWith('/') ? 1 : 0;
        var end = value.IndexOf('/', start);
        var first = end < 0 ? value[start..] : value[start..end];
        return first.Length > 0 && apiFirstSegments.Contains(first);
    }

    /// <summary>
    /// An existing file of the build, with long-lived caching when its name carries Angular's content hash
    /// (<c>main-V76QUCWD.js</c>) and <c>no-cache</c> otherwise; a missing path whose last segment has an
    /// extension (a stale <c>chunk-OLD12345.js</c>, a mistyped <c>/logo.png</c>) is <c>404</c>, never the
    /// page; any other path is the SPA's own route and gets <c>index.html</c>, never cached, so a new
    /// deployment is picked up at the next navigation.
    /// </summary>
    private static async Task ServeAsync(HttpContext context, string root)
    {
        var relative = (context.Request.Path.Value ?? string.Empty).TrimStart('/');
        if (relative.Length > 0 && TryResolveFile(root, relative, out var file))
        {
            if (!ContentTypes.TryGetContentType(file, out var contentType))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var isIndex = Path.GetFileName(file).Equals(IndexFileName, StringComparison.OrdinalIgnoreCase);
            var cache = !isIndex && IsHashed(Path.GetFileName(file)) ? ImmutableCache : "no-cache";
            await SendAsync(context, file, isIndex ? "text/html; charset=utf-8" : contentType, cache);
            return;
        }

        var lastSegment = relative[(relative.LastIndexOf('/') + 1)..];
        if (Path.HasExtension(lastSegment))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await SendAsync(context, Path.Combine(root, IndexFileName), "text/html; charset=utf-8", "no-cache");
    }

    /// <summary>
    /// A name with the content hash of <c>outputHashing: all</c>: what the application builder emits
    /// (<c>main-4YPR3XO6.js</c>, <c>styles-TA7CLVVE.css</c>, and chunks whose hash is URL-safe base64, sometimes
    /// with a de-duplicating digit: <c>chunk-B-N0blP72.js</c>, <c>chunk-BgUK_noc.js</c>, <c>chunk-B9545APi2.js</c>),
    /// or the widget's rule for other hashed files (<c>media/font-ABCD1234.woff2</c>). Anything else — the
    /// copied <c>favicon.ico</c>, an image from <c>libs/assets</c> — must be revalidated.
    /// </summary>
    internal static bool IsHashed(string fileName) =>
        BuildOutputName().IsMatch(fileName) || WidgetEndpoints.HashedFileName().IsMatch(fileName);

    [GeneratedRegex(@"\A(?:main|polyfills|styles|scripts|chunk|worker)-[A-Za-z0-9_-]{8,}\.(?:js|mjs|css)\z", RegexOptions.CultureInvariant)]
    private static partial Regex BuildOutputName();

    private static Task SendAsync(HttpContext context, string file, string contentType, string cacheControl)
    {
        context.Response.Headers.CacheControl = cacheControl;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(file, contentType, lastModified: File.GetLastWriteTimeUtc(file)).ExecuteAsync(context);
    }

    /// <summary>The existing file <paramref name="requestPath"/> names inside <paramref name="root"/>; false
    /// for anything that leaves the folder (<c>..</c>, a backslash, a NUL, an empty segment) or is not a file.</summary>
    private static bool TryResolveFile(string root, string requestPath, out string file)
    {
        file = string.Empty;
        if (requestPath.Contains('\\', StringComparison.Ordinal)
            || requestPath.Contains('\0', StringComparison.Ordinal)
            || requestPath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(root, requestPath));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal) || !File.Exists(candidate))
        {
            return false;
        }

        file = candidate;
        return true;
    }
}
