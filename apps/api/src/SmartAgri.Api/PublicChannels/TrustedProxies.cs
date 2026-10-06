using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// Reverse-proxy support (M5a plan §3 E, risk 4; issue #197): <c>PublicChannels:TrustedProxies</c>
/// names the proxies in front of the API, and only requests that arrive from one of them have their
/// <c>X-Forwarded-For</c> (the client address the rate limits partition by), <c>X-Forwarded-Proto</c>
/// and <c>X-Forwarded-Host</c> applied — so <see cref="PublicOriginGuard"/> and the embed code see the
/// address visitors really used (<c>https</c> although the proxy talks plain <c>http</c> to the API).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not configured</b> (the default): nothing is applied; every <c>X-Forwarded-*</c> header is
/// ignored and the client is the connection's source address. Behind a reverse proxy that makes every
/// visitor one client, so a Production host logs a warning, once, at the first request that carries
/// <c>X-Forwarded-For</c> (a client cannot make the header appear on its own: it is the proxy that
/// adds it — and that is exactly the case this warns about).
/// </para>
/// <para>
/// <b>Configured</b>: only the listed addresses and networks are trusted (the framework's default of
/// trusting loopback does not apply — list <c>127.0.0.1</c> and <c>::1</c> for a proxy on the same
/// machine). The chain is walked from the right and stops at the first address that is not a trusted
/// proxy, so a client that sends its own <c>X-Forwarded-For</c> cannot choose the address it is counted as.
/// Applies to every request, not only the visitor API (the token endpoints see the same scheme and host).
/// </para>
/// <para>
/// Nothing reads <c>PublicChannels</c> while the pipeline is declared: the middleware reads its options
/// when the host starts, after <c>ValidateOnStart</c> has had its say, so a bad setting refuses to start
/// the same way every other bad setting does.
/// </para>
/// </remarks>
public static class TrustedProxies
{
    private const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>Configures <see cref="ForwardedHeadersOptions"/> from <c>PublicChannels:TrustedProxies</c>:
    /// no forwarded header is applied at all unless at least one proxy is listed.</summary>
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<PublicChannelsOptions>>((forwarded, channels) =>
        {
            var (addresses, networks, _) = channels.Value.ParseTrustedProxies();
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            if (addresses.Count + networks.Count == 0)
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.None;
                return;
            }

            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            // Walk the whole chain while every hop is a trusted proxy (the default of 1 would take the
            // last entry only, wrong behind a CDN in front of a proxy).
            forwarded.ForwardLimit = null;
            foreach (var address in addresses)
            {
                forwarded.KnownProxies.Add(address);
            }

            foreach (var network in networks)
            {
                forwarded.KnownIPNetworks.Add(network);
            }
        });
        return services;
    }

    /// <summary>Applies the forwarded headers of the configured proxies (nothing without any), and in
    /// Production warns once about <c>X-Forwarded-For</c> arriving while none is configured.</summary>
    public static WebApplication UseTrustedProxies(this WebApplication app)
    {
        app.UseForwardedHeaders();
        if (app.Environment.IsProduction())
        {
            app.UseMiddleware<UnconfiguredProxyWarning>();
        }

        return app;
    }

    private sealed class UnconfiguredProxyWarning
    {
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;
        private readonly bool _configured;
        private int _warned;

        public UnconfiguredProxyWarning(RequestDelegate next, IOptions<PublicChannelsOptions> options, ILoggerFactory loggerFactory)
        {
            _next = next;
            _logger = loggerFactory.CreateLogger(typeof(TrustedProxies).FullName!);
            var (addresses, networks, _) = options.Value.ParseTrustedProxies();
            _configured = addresses.Count + networks.Count > 0;
        }

        public Task InvokeAsync(HttpContext context)
        {
            if (!_configured
                && Volatile.Read(ref _warned) == 0
                && context.Request.Headers.ContainsKey(ForwardedForHeader)
                && Interlocked.Exchange(ref _warned, 1) == 0)
            {
                _logger.LogWarning(
                    "A request arrived with X-Forwarded-For but PublicChannels:TrustedProxies is not configured, so the header is ignored " +
                    "and every visitor behind the reverse proxy is counted as one client IP by the visitor API's rate limits. " +
                    "Set PublicChannels:TrustedProxies (TRUSTED_PROXIES in deploy/.env) to the proxy's address or network.");
            }

            return _next(context);
        }
    }
}
