using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using SmartAgri.Infrastructure.Assistants;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// What the Api serves to visitors' browsers (M5a plan §3 A and B, §5 Slice 6, issue #201): the chat
/// window <c>GET /use/{assistantId}</c> that customers' pages embed in an iframe, its static files
/// <c>/widget/*</c>, and the loader <c>/embed.js</c>. All anonymous (they opt out of the fallback
/// authorization policy) and none of them under <c>/api</c>: they are not part of the OpenAPI document.
/// </summary>
public static partial class WidgetEndpoints
{
    /// <summary>The page's <c>Referrer-Policy</c>, the same as the widget's own <c>&lt;meta name="referrer"&gt;</c>.</summary>
    private const string ReferrerPolicy = "strict-origin";

    private const string HtmlContentType = "text/html; charset=utf-8";

    private const string ImmutableCache = "public, max-age=31536000, immutable";

    private const string EmbedScriptCache = "public, max-age=300";

    public const string UnavailableMessage = "這個對話視窗目前無法使用";

    public const string OpenFromSiteMessage = "請從官網開啟這個對話視窗";

    private const string NeutralStyle =
        "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;"
        + "font:16px/1.5 system-ui,sans-serif;color:#44403c;background:#fafaf9}p{margin:0;padding:24px;text-align:center}";

    /// <summary>The CSP of the neutral pages: nothing may load, the one inline style is allowed by its hash,
    /// and nothing may frame them.</summary>
    private static readonly string NeutralContentSecurityPolicy =
        $"default-src 'none'; style-src 'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(NeutralStyle)))}'; "
        + "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly string UnavailablePage = NeutralPage(UnavailableMessage);

    private static readonly string OpenFromSitePage = NeutralPage(OpenFromSiteMessage);

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IEndpointRouteBuilder MapWidgetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/use/{assistantId}", ServeChatWindowAsync).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet("/widget/{**path}", ServeWidgetFile).AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet("/embed.js", ServeEmbedScript).AllowAnonymous().ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>
    /// <c>GET /use/{assistantId}</c>. Decides in this order, and every answer that is not the chat window
    /// itself is one fixed page:
    /// <list type="number">
    /// <item>No widget build on this server: <c>503</c> with a plain explanation (a deployment problem,
    /// the same for every id);</item>
    /// <item><c>Sec-Fetch-Dest: document</c> (opened in a tab, not in an iframe; decision D): 「請從官網開啟這個對話視窗」,
    /// without looking anything up. Not a security boundary (a header the visitor controls); the iframe
    /// restriction is <c>frame-ancestors</c>;</item>
    /// <item>The assistant has no published (or paused) website channel with an allowed domain, or does not
    /// exist, or the id is not an id: 「這個對話視窗目前無法使用」 with <c>frame-ancestors 'none'</c> — the same
    /// bytes and headers for all of them, so the response does not say which;</item>
    /// <item>Otherwise <c>index.html</c> with a fresh nonce on <c>&lt;app-root ngCspNonce&gt;</c>, and
    /// <c>frame-ancestors</c> listing the allowed domains.</item>
    /// </list>
    /// The channel is read from the database on every request, so a removed domain stops being allowed with
    /// the next one. Suspended states (acceptance, knowledge, quota) still serve the chat window: it asks the
    /// visitor API and shows 「目前暫停服務」.
    /// </summary>
    private static async Task<IResult> ServeChatWindowAsync(
        HttpContext context,
        string assistantId,
        PublicWebsiteChannelLookup lookup,
        WidgetIndexTemplate template,
        IOptions<PublicChannelsOptions> options)
    {
        SetPageHeaders(context.Response);

        if (!template.TryLoad(out var html, out var problem))
        {
            return Results.Text(problem, "text/plain; charset=utf-8", Encoding.UTF8, StatusCodes.Status503ServiceUnavailable);
        }

        if (context.Request.Headers["Sec-Fetch-Dest"].ToString().Equals("document", StringComparison.OrdinalIgnoreCase))
        {
            SetNeutralHeaders(context.Response);
            return Results.Text(OpenFromSitePage, HtmlContentType, Encoding.UTF8);
        }

        var channel = Guid.TryParse(assistantId, out var id)
            ? await lookup.FindAsync(id, context.RequestAborted)
            : null;
        var frameAncestors = WidgetFrameAncestors.For(channel, options.Value.AllowLocalhostAncestors);
        if (frameAncestors is null)
        {
            SetNeutralHeaders(context.Response);
            return Results.Text(UnavailablePage, HtmlContentType, Encoding.UTF8, StatusCodes.Status404NotFound);
        }

        var nonce = NewNonce();
        context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy(nonce, frameAncestors);
        return Results.Text(WidgetIndexTemplate.WithNonce(html, nonce), HtmlContentType, Encoding.UTF8);
    }

    /// <summary>The widget's CSP (M5a plan §3 A): script from this origin only, styles from this origin plus
    /// this response's nonce (Angular inserts component styles at run time), requests to this origin only.</summary>
    internal static string ContentSecurityPolicy(string nonce, string frameAncestors) =>
        $"default-src 'self'; script-src 'self'; style-src 'self' 'nonce-{nonce}'; connect-src 'self'; "
        + $"img-src 'self' data:; base-uri 'self'; form-action 'none'; frame-ancestors {frameAncestors}";

    /// <summary>The headers every answer of <c>/use/{id}</c> has, whatever it says.</summary>
    private static void SetPageHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = ReferrerPolicy;
        response.Headers["X-Robots-Tag"] = "noindex";
    }

    private static void SetNeutralHeaders(HttpResponse response) =>
        response.Headers.ContentSecurityPolicy = NeutralContentSecurityPolicy;

    private static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    private static string NeutralPage(string message) =>
        "<!doctype html>\n<html lang=\"zh-Hant\">\n<head>\n<meta charset=\"utf-8\">\n"
        + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
        + "<meta name=\"robots\" content=\"noindex\">\n<title>客服對話</title>\n"
        + $"<style>{NeutralStyle}</style>\n</head>\n<body>\n<p>{message}</p>\n</body>\n</html>\n";

    /// <summary>
    /// <c>GET /widget/{path}</c>: a file of the widget's build, except <c>index.html</c> (served by
    /// <c>/use/{id}</c> only: as a plain file it would be a copy of the page without
    /// <c>frame-ancestors</c>). Hashed files (<c>main-V76QUCWD.js</c>) are immutable and cached for a year;
    /// anything else must be revalidated.
    /// </summary>
    private static IResult ServeWidgetFile(
        HttpContext context,
        string? path,
        IOptions<WidgetOptions> options,
        IHostEnvironment environment)
    {
        if (!TryResolveFile(options.Value.ResolveRoot(environment), path, out var file)
            || !ContentTypes.TryGetContentType(file, out var contentType))
        {
            return Results.NotFound();
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = HashedFileName().IsMatch(Path.GetFileName(file)) ? ImmutableCache : "no-cache";
        return Results.File(file, contentType, lastModified: File.GetLastWriteTimeUtc(file));
    }

    /// <summary><c>GET /embed.js</c>: the loader as it is in <c>apps/embed-loader/src/embed.js</c>, cached for
    /// five minutes (it has no hash in its name, and the embed code customers paste cannot change).</summary>
    private static IResult ServeEmbedScript(
        HttpContext context,
        IOptions<WidgetOptions> options,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        var file = options.Value.ResolveEmbedScript(environment);
        if (!File.Exists(file))
        {
            loggerFactory.CreateLogger(typeof(WidgetEndpoints))
                .LogWarning("embed.js was not found at {Path} (Widget:EmbedScriptPath).", file);
            return Results.Text(
                "The embed script is not part of this server's build (Widget:EmbedScriptPath).",
                "text/plain; charset=utf-8",
                Encoding.UTF8,
                StatusCodes.Status503ServiceUnavailable);
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = EmbedScriptCache;
        // Customers' pages load it with a plain <script>; this says that cross-origin use is intended.
        context.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        return Results.File(file, "text/javascript; charset=utf-8", lastModified: File.GetLastWriteTimeUtc(file));
    }

    /// <summary>
    /// The existing file <paramref name="requestPath"/> names inside <paramref name="root"/>, never
    /// <c>index.html</c>; false for anything that leaves the folder (<c>..</c>, a rooted or backslashed
    /// path, a NUL) or is not a file.
    /// </summary>
    private static bool TryResolveFile(string root, string? requestPath, out string file)
    {
        file = string.Empty;
        if (string.IsNullOrEmpty(requestPath)
            || requestPath.Contains('\\', StringComparison.Ordinal)
            || requestPath.Contains('\0', StringComparison.Ordinal)
            || requestPath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return false;
        }

        if (requestPath.Equals("index.html", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>A build output name with the content hash Angular's <c>outputHashing: all</c> adds before the
    /// extension (<c>chunk-DGISV55Z.js</c>, <c>styles-JLI52OSW.css</c>).</summary>
    [GeneratedRegex(@"-[A-Za-z0-9]{8}\.[A-Za-z0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashedFileName();
}
