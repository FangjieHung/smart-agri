using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using SmartAgri.Api.Errors;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// The visitor API accepts only same-origin browser requests (M5a plan §3 B): a request to
/// <c>/api/v1/public/*</c> that carries an <c>Origin</c> header other than the API's own gets
/// <c>403 public-origin</c> before authentication or the endpoint runs. A request without
/// <c>Origin</c> (a script, <c>curl</c>, a same-origin GET) is let through — this is a browser
/// boundary, not abuse protection; rate limits and the monthly token limit are (plan §3 E, F).
/// </summary>
/// <remarks>
/// "The API's own origin" is the origin of the request as received (scheme, host and port), and
/// also that of <c>PublicChannels:PublicBaseUrl</c> when it is set, because behind a TLS-terminating
/// reverse proxy the API may see <c>http://</c> while visitors' browsers send <c>https://</c>. No CORS
/// policy is registered, so a cross-origin browser call fails its preflight before it is sent; this
/// guard also covers the requests a browser sends without a preflight.
/// </remarks>
public static class PublicOriginGuard
{
    /// <summary>Every visitor endpoint lives under this path.</summary>
    public const string PathPrefix = "/api/v1/public";

    public static IApplicationBuilder UsePublicOriginGuard(this IApplicationBuilder app) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase),
            branch => branch.Use(async (context, next) =>
            {
                if (!IsAllowed(context))
                {
                    await ApiErrors.Forbidden(ForbiddenReason.PublicOrigin).ExecuteAsync(context);
                    return;
                }

                await next(context);
            }));

    /// <summary>No <c>Origin</c>, or exactly one that equals the API's own origin.</summary>
    internal static bool IsAllowed(HttpContext context)
    {
        var origins = context.Request.Headers[HeaderNames.Origin];
        if (origins.Count == 0)
        {
            return true;
        }

        if (origins.Count != 1 || !TryOrigin(origins[0], out var origin))
        {
            return false;
        }

        var request = context.Request;
        if (TryOrigin($"{request.Scheme}://{request.Host.Value}", out var own) && SameOrigin(origin, own))
        {
            return true;
        }

        var publicBaseUrl = context.RequestServices.GetService<IOptions<PublicChannelsOptions>>()?.Value.ResolvedPublicBaseUrl;
        return publicBaseUrl is not null && TryOrigin(publicBaseUrl, out var configured) && SameOrigin(origin, configured);
    }

    private static bool TryOrigin(string? value, out Uri origin)
    {
        origin = null!;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || parsed.Host.Length == 0)
        {
            return false;
        }

        origin = parsed;
        return true;
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
