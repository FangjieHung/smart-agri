using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Errors;

namespace SmartAgri.Api.PublicChannels;

/// <summary>Which visitor endpoint a request is for; set as endpoint metadata
/// (<see cref="PublicRateLimitMarker"/>) so the limiter never has to parse paths.</summary>
public enum PublicRateLimitedEndpoint
{
    /// <summary><c>POST …/visitor-sessions</c>.</summary>
    SessionCreate,

    /// <summary><c>POST …/chat/runs</c>.</summary>
    ChatRun,
}

/// <summary>Endpoint metadata that puts an endpoint under the visitor API's rate limits.</summary>
public sealed record PublicRateLimitMarker(PublicRateLimitedEndpoint Kind);

/// <summary>
/// The visitor API's rate limits (M5a plan §3 E, issue #197): ASP.NET Core's built-in rate limiter
/// as <see cref="RateLimiterOptions.GlobalLimiter"/>, a chain of partitioned limiters that each
/// apply only to endpoints carrying <see cref="PublicRateLimitMarker"/> — so <c>/api/v1/public/*</c>
/// is limited and nothing else (member endpoints, the widget, health checks) ever is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Partitions</b> (values in <see cref="PublicRateLimitOptions"/>): session creation — per client
/// IP, fixed window of one minute; chat runs — per visitor (<c>visitor_id</c> claim) in a one-minute
/// and a one-hour sliding window, per client IP in a one-minute sliding window, per assistant
/// (<c>assistant_id</c> claim) in a one-minute sliding window plus a limit on replies being generated
/// at once. A request must pass every limiter that applies; the first to refuse answers
/// <c>429 { reason: "rate-limited" }</c> with <c>Retry-After</c> (seconds), before the answer
/// stream starts. <c>Retry-After</c> is exact for the session limit (fixed window), the whole window for a
/// sliding-window refusal (the built-in limiter cannot say when the next permit frees up; the oldest request is at
/// most one window old, so it is never too early) and 5 seconds for the concurrency limit. Limiters are ordered narrowest first (visitor, IP, assistant), so a refusal by a
/// wider partition never uses up another visitor's budget and a flooding visitor mostly burns their
/// own; permits taken by an earlier limiter are not given back when a later one refuses.
/// </para>
/// <para>
/// <b>Where it runs</b>: after authorization, so the visitor claims are known (a request with no or
/// an invalid visitor token is already <c>401</c> and costs nothing), and after
/// <see cref="PublicOriginGuard"/> (a foreign <c>Origin</c> is <c>403</c> and uses no permit). A
/// request without <c>Origin</c> — a script, <c>curl</c> — is limited exactly like a browser's (plan
/// §7, risk 1: the browser-side checks do not stop scripts, these limits do).
/// </para>
/// <para>
/// <b>Client IP</b> is the connection's remote address, which <see cref="TrustedProxies"/> replaces
/// with the forwarded client only for requests from a configured proxy. An IPv6 client is one
/// partition per /64 (it can use any address in its own /64), an IPv4-mapped one counts as IPv4.
/// </para>
/// <para>
/// In memory, per process: one API container per deployment (several instances would each count on
/// their own). The clock is the system's — the windows are minutes and an hour, so tests use tiny
/// counts rather than a fake clock.
/// </para>
/// </remarks>
public static class PublicRateLimiting
{
    /// <summary>Seconds to wait when the limiter that refused cannot say (the concurrency limit:
    /// it frees up when a reply ends, which takes a few seconds).</summary>
    internal const int FallbackRetryAfterSeconds = 5;

    private static readonly TimeSpan ConcurrencyRetryAfter = TimeSpan.FromSeconds(FallbackRetryAfterSeconds);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>Adds the rate limiter service and its options (values from <c>PublicChannels:RateLimits</c>).</summary>
    public static IServiceCollection AddPublicRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<PublicChannelsOptions>>((options, channels) =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = CreateLimiter(channels.Value.RateLimits);
            options.OnRejected = RejectAsync;
        });
        return services;
    }

    /// <summary>Puts a visitor endpoint under the rate limits.</summary>
    public static TBuilder WithPublicRateLimit<TBuilder>(this TBuilder builder, PublicRateLimitedEndpoint kind)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new PublicRateLimitMarker(kind));

    internal static PartitionedRateLimiter<HttpContext> CreateLimiter(PublicRateLimitOptions limits) =>
        PartitionedRateLimiter.CreateChained(
            // Session creation: per client IP.
            Layer(PublicRateLimitedEndpoint.SessionCreate, ClientKey, retryAfter: null, key =>
                RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.SessionsPerIpPerMinute,
                    Window = Minute,
                    QueueLimit = 0,
                })),
            // Chat runs, narrowest first: per visitor (minute, hour), per client IP, per assistant.
            Layer(PublicRateLimitedEndpoint.ChatRun, VisitorKey, Minute, key => Sliding(key, limits.RunsPerVisitorPerMinute, Minute, segments: 6)),
            Layer(PublicRateLimitedEndpoint.ChatRun, VisitorKey, Hour, key => Sliding(key, limits.RunsPerVisitorPerHour, Hour, segments: 12)),
            Layer(PublicRateLimitedEndpoint.ChatRun, ClientKey, Minute, key => Sliding(key, limits.RunsPerIpPerMinute, Minute, segments: 6)),
            Layer(PublicRateLimitedEndpoint.ChatRun, AssistantKey, Minute, key => Sliding(key, limits.RunsPerAssistantPerMinute, Minute, segments: 6)),
            Layer(PublicRateLimitedEndpoint.ChatRun, AssistantKey, ConcurrencyRetryAfter, key =>
                RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = limits.MaxConcurrentRunsPerAssistant,
                    QueueLimit = 0,
                })));

    private static RateLimitPartition<string> Sliding(string key, int permits, TimeSpan window, int segments) =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            SegmentsPerWindow = segments,
            QueueLimit = 0,
        });

    /// <summary>
    /// One limiter of the chain: applies only to <paramref name="kind"/>, and only when the request has
    /// a partition key (<paramref name="key"/> returns <see langword="null"/> otherwise). A sliding
    /// window or concurrency limiter cannot say when it will take the next request, so a refusal by
    /// one carries <paramref name="retryAfter"/> instead: for a sliding window its whole window, which is
    /// never too early (the oldest request is at most one window old); the fixed window's own exact
    /// value is kept (<paramref name="retryAfter"/> <see langword="null"/>).
    /// </summary>
    private static PartitionedRateLimiter<HttpContext> Layer(
        PublicRateLimitedEndpoint kind,
        Func<HttpContext, string?> key,
        TimeSpan? retryAfter,
        Func<string, RateLimitPartition<string>> partition)
    {
        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.GetEndpoint()?.Metadata.GetMetadata<PublicRateLimitMarker>() is { } marker
            && marker.Kind == kind
            && key(context) is { } partitionKey
                ? partition(partitionKey)
                : RateLimitPartition.GetNoLimiter("none"));
        return retryAfter is { } hint ? new RetryAfterHint(limiter, hint) : limiter;
    }

    private static string? VisitorKey(HttpContext context) =>
        VisitorAuthentication.Read(context.User) is { } visitor ? visitor.VisitorId.ToString("N") : null;

    private static string? AssistantKey(HttpContext context) =>
        VisitorAuthentication.Read(context.User) is { } visitor ? visitor.AssistantId.ToString("N") : null;

    private static string? ClientKey(HttpContext context) => ClientPartition(context.Connection.RemoteIpAddress);

    /// <summary>The rate-limit partition of a client address: itself, or for IPv6 its /64.</summary>
    internal static string ClientPartition(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private static async ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : FallbackRetryAfterSeconds;
        var response = context.HttpContext.Response;
        response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        response.Headers.CacheControl = "no-store";
        await ApiErrors.RateLimited().ExecuteAsync(context.HttpContext);
    }

    /// <summary>Adds a <see cref="MetadataName.RetryAfter"/> to the refusals of <paramref name="inner"/> that have none.</summary>
    private sealed class RetryAfterHint(PartitionedRateLimiter<HttpContext> inner, TimeSpan hint) : PartitionedRateLimiter<HttpContext>
    {
        public override RateLimiterStatistics? GetStatistics(HttpContext resource) => inner.GetStatistics(resource);

        protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount) =>
            Hinted(inner.AttemptAcquire(resource, permitCount));

        protected override async ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken) =>
            Hinted(await inner.AcquireAsync(resource, permitCount, cancellationToken));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
        }

        private RateLimitLease Hinted(RateLimitLease lease) =>
            lease.IsAcquired || lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan _) ? lease : new HintedLease(lease, hint);
    }

    private sealed class HintedLease(RateLimitLease inner, TimeSpan hint) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = hint;
                return true;
            }

            return inner.TryGetMetadata(metadataName, out metadata);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
        }
    }
}
