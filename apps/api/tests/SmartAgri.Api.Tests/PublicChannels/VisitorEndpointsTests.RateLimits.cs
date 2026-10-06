using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Tests.Authentication;

namespace SmartAgri.Api.Tests.PublicChannels;

/// <summary>
/// The visitor API's rate limits and reverse-proxy support (M5a plan §3 E and §7 risks 1, 3 and 4;
/// issue #197). Each test starts its own host with tiny counts (the shared host's are out of the way,
/// see <c>AuthHostFixture</c>); a host's counters are its own, so tests never see each other's.
/// Requests to assistants that do not exist are enough to count — the limiter runs before the handler,
/// so under the limit they are <c>403 public-assistant</c> and over it <c>429</c> — and the one test
/// that needs a reply in flight uses a real assistant.
/// </summary>
public sealed partial class VisitorEndpointsTests
{
    private const string RemoteIpHeader = "X-Test-Remote-Ip";
    private const int Plenty = 1_000_000;

    // --- Per-partition limits ------------------------------------------------------------------------

    /// <summary>Acceptance: session creation is limited per client IP, other IPs are not affected.</summary>
    [Fact]
    public async Task Sessions_over_the_per_ip_limit_are_429_with_retry_after_and_another_ip_is_not_affected()
    {
        await using var factory = LimitedHost([("SessionsPerIpPerMinute", 2)]);
        var client = VisitorClient(factory);

        (await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var limited = await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.1");
        await AssertRateLimitedAsync(limited, maxSeconds: 60);

        (await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.2")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "another IP");
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "still limited");
        // An IPv6 client is one partition per /64.
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "2001:db8:1:2::1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "2001:db8:1:2:ffff::9")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "2001:db8:1:2:abcd::5")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "same /64");
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "2001:db8:1:3::1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "another /64");
    }

    /// <summary>Acceptance: the per-minute layer of a visitor's limit; another visitor on the same IP passes.</summary>
    [Fact]
    public async Task A_visitor_over_the_per_minute_limit_is_429_and_another_visitor_from_the_same_ip_passes()
    {
        await using var factory = LimitedHost([("RunsPerVisitorPerMinute", 2)]);
        var client = VisitorClient(factory);
        var assistantId = Guid.NewGuid();
        var visitor = TokenFor(factory, assistantId);
        var other = TokenFor(factory, assistantId);

        (await RunFromAsync(client, assistantId, visitor, ip: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RunFromAsync(client, assistantId, visitor, ip: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertRateLimitedAsync(await RunFromAsync(client, assistantId, visitor, ip: "198.51.100.1"), maxSeconds: 60);

        (await RunFromAsync(client, assistantId, other, ip: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "another visitor, same IP and assistant");
        (await RunFromAsync(client, assistantId, visitor, ip: "198.51.100.9")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the limit follows the visitor, not the IP");
    }

    /// <summary>Acceptance: the per-hour layer (the minute layer is out of the way).</summary>
    [Fact]
    public async Task A_visitor_over_the_per_hour_limit_is_429_with_a_retry_after_of_the_hour()
    {
        await using var factory = LimitedHost([("RunsPerVisitorPerHour", 2)]);
        var client = VisitorClient(factory);
        var assistantId = Guid.NewGuid();
        var visitor = TokenFor(factory, assistantId);

        (await RunFromAsync(client, assistantId, visitor)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RunFromAsync(client, assistantId, visitor)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var limited = await RunFromAsync(client, assistantId, visitor);
        await AssertRateLimitedAsync(limited, maxSeconds: 3600);
        RetryAfterSeconds(limited).ShouldBe(3600, "a sliding window can only promise its whole length");
        (await RunFromAsync(client, assistantId, TokenFor(factory, assistantId))).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "another visitor");
    }

    /// <summary>Acceptance: chat runs are limited per client IP across visitors.</summary>
    [Fact]
    public async Task Runs_over_the_per_ip_limit_are_429_whichever_visitor_sends_them_and_another_ip_is_not_affected()
    {
        await using var factory = LimitedHost([("RunsPerIpPerMinute", 2)]);
        var client = VisitorClient(factory);
        var assistantId = Guid.NewGuid();

        (await RunFromAsync(client, assistantId, TokenFor(factory, assistantId), ip: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RunFromAsync(client, assistantId, TokenFor(factory, assistantId), ip: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertRateLimitedAsync(await RunFromAsync(client, assistantId, TokenFor(factory, assistantId), ip: "198.51.100.1"), maxSeconds: 60);

        (await RunFromAsync(client, assistantId, TokenFor(factory, assistantId), ip: "198.51.100.2")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "another IP");
        // The session limit is a different partition of the same IP.
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "sessions are counted apart");
    }

    /// <summary>Acceptance: chat runs are limited per assistant across visitors and IPs; another assistant is not affected.</summary>
    [Fact]
    public async Task Runs_over_the_per_assistant_limit_are_429_and_another_assistant_is_not_affected()
    {
        await using var factory = LimitedHost([("RunsPerAssistantPerMinute", 2)]);
        var client = VisitorClient(factory);
        var busy = Guid.NewGuid();
        var quiet = Guid.NewGuid();

        (await RunFromAsync(client, busy, TokenFor(factory, busy), ip: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RunFromAsync(client, busy, TokenFor(factory, busy), ip: "198.51.100.2")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertRateLimitedAsync(await RunFromAsync(client, busy, TokenFor(factory, busy), ip: "198.51.100.3"), maxSeconds: 60);

        (await RunFromAsync(client, quiet, TokenFor(factory, quiet), ip: "198.51.100.3")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "another assistant");
        (await RunFromAsync(client, quiet, TokenFor(factory, quiet), ip: "198.51.100.4")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "another assistant");
    }

    /// <summary>Acceptance: the assistant's concurrency limit answers 429 before the stream starts, only for the
    /// assistant that is busy, and frees up when the reply ends.</summary>
    [Fact]
    public async Task Replies_running_at_the_same_time_are_limited_per_assistant_and_the_429_comes_before_the_stream()
    {
        var model = new BlockingChatClient();
        await using var factory = LimitedHost(
            [("MaxConcurrentRunsPerAssistant", 1)],
            configure: builder => builder.ConfigureTestServices(services => services.AddScoped<IChatClient>(_ => model)));
        var org = await CreateOrganizationWithKnowledgeAsync();
        var assistantId = await PublishedAssistantAsync(org, keepConversations: false);
        var client = VisitorClient(factory);
        var first = await SessionTokenAsync(client, assistantId);
        var second = await SessionTokenAsync(client, assistantId);

        using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        using var running = RunRequest(assistantId, first, RunInput(RelatedQuestion));
        var stream = await client.SendAsync(running, HttpCompletionOption.ResponseHeadersRead, disconnect.Token);
        stream.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(disconnect.Token), Encoding.UTF8);
        while (await reader.ReadLineAsync(disconnect.Token) is { } line && !line.Contains("\"TEXT_MESSAGE_CONTENT\"", StringComparison.Ordinal))
        {
        }

        // A second visitor's question for the same assistant is refused outright: a problem, not a stream.
        var refused = await SendRunAsync(client, assistantId, second, RunInput("第二個問題"));
        await AssertRateLimitedAsync(refused, maxSeconds: 3600);
        RetryAfterSeconds(refused).ShouldBe(5, "a reply ends in seconds");
        refused.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // Another assistant has its own budget.
        var elsewhere = Guid.NewGuid();
        (await RunFromAsync(client, elsewhere, TokenFor(factory, elsewhere))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The reply ends (the visitor went away): the assistant takes questions again.
        await disconnect.CancelAsync();
        stream.Dispose();
        (await model.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken)).ShouldBeTrue();
        var started = DateTime.UtcNow;
        HttpStatusCode status;
        do
        {
            using var again = RunRequest(assistantId, second, RunInput("第三個問題"));
            using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.SendAsync(again, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            status = response.StatusCode;
            await attempt.CancelAsync(); // a 200 would stream until the model call is cancelled
            if (status == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(100, CancellationToken);
            }
        }
        while (status == HttpStatusCode.TooManyRequests && DateTime.UtcNow - started < TimeSpan.FromSeconds(10));

        status.ShouldBe(HttpStatusCode.OK, "the permit is returned when the reply ends");
    }

    // --- Who is limited -------------------------------------------------------------------------------

    /// <summary>Acceptance: a member's chat (and everything else under /api/v1/assistants) is never in a visitor partition,
    /// however empty the visitors' budgets are — and the same client IP is limited as a visitor at the same moment.</summary>
    [Fact]
    public async Task Member_chat_is_never_limited_by_the_visitor_partitions()
    {
        await using var factory = LimitedHost(
            [("SessionsPerIpPerMinute", 1), ("RunsPerVisitorPerMinute", 1), ("RunsPerVisitorPerHour", 1),
             ("RunsPerIpPerMinute", 1), ("RunsPerAssistantPerMinute", 1), ("MaxConcurrentRunsPerAssistant", 1)]);
        var org = await CreateOrganizationWithKnowledgeAsync();
        var assistantId = await PublishedAssistantAsync(org, keepConversations: false);
        var client = VisitorClient(factory);
        var token = await SessionTokenAsync(client, assistantId);

        // Use up every visitor partition of this client and assistant.
        (await RunAsync(client, assistantId, token, RunInput(RelatedQuestion))).Status.ShouldBe(HttpStatusCode.OK);
        (await SendRunAsync(client, assistantId, token, RunInput(RelatedQuestion))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CreateSessionAsync(client, assistantId)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var member = new SpaClient(factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var memberToken = (await member.SignInAsync(org.Organization.Code, "admin", Password)).AccessToken;
        for (var i = 0; i < 4; i++)
        {
            var run = await SendAsync(member.Http, $"{AssistantsPath}/{assistantId}/chat/runs", memberToken, RunInput(RelatedQuestion, runId: $"member-{i}"), "Bearer");
            run.StatusCode.ShouldBe(HttpStatusCode.OK, $"member chat run {i}: {await run.Content.ReadAsStringAsync(CancellationToken)}");
            run.Headers.Contains("Retry-After").ShouldBeFalse();
        }

        (await member.GetAsync($"{AssistantsPath}/{assistantId}/publishing/website", memberToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Acceptance (plan §7, risk 1): a script that sends no <c>Origin</c> is limited exactly like a browser; a
    /// foreign <c>Origin</c> is refused before the limiter and uses nothing.</summary>
    [Fact]
    public async Task Direct_calls_without_an_origin_header_are_limited_and_a_refused_origin_uses_no_permit()
    {
        await using var factory = LimitedHost([("SessionsPerIpPerMinute", 2)]);
        var client = VisitorClient(factory);

        (await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.1", origin: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.1", origin: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var limited = await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.1", origin: null);
        await AssertRateLimitedAsync(limited, maxSeconds: 60);

        var foreign = await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.1", origin: "https://evil.example");
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(foreign)).GetProperty("reason").GetString().ShouldBe("public-origin");

        // ...and, with a fresh IP, foreign-origin attempts do not eat the budget the page itself needs.
        for (var i = 0; i < 5; i++)
        {
            (await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.7", origin: "https://evil.example")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.7", origin: "http://localhost")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyJsonAsync(await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.7", origin: "http://localhost")))
            .GetProperty("reason").GetString().ShouldBe("public-assistant", "an own-origin request is counted and reaches the handler");
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "198.51.100.7", origin: null)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "two permits were used by own-origin requests, the foreign ones used none");
    }

    // --- Reverse proxies ------------------------------------------------------------------------------

    /// <summary>Acceptance: without <c>TrustedProxies</c> every <c>X-Forwarded-For</c> is ignored — a client cannot
    /// pick the partition it is counted in.</summary>
    [Fact]
    public async Task Without_trusted_proxies_x_forwarded_for_is_ignored()
    {
        await using var factory = LimitedHost([("SessionsPerIpPerMinute", 1)]);
        var client = VisitorClient(factory);

        (await SessionFromAsync(client, Guid.NewGuid(), ip: "10.1.2.3", forwardedFor: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var spoofed = await SessionFromAsync(client, Guid.NewGuid(), ip: "10.1.2.3", forwardedFor: "198.51.100.2");
        await AssertRateLimitedAsync(spoofed, maxSeconds: 60);
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "10.1.2.3")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the connection's own address");
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "10.9.9.9", forwardedFor: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "another connection");
    }

    /// <summary>Acceptance: with <c>TrustedProxies</c>, the forwarded client of a request from a trusted proxy is the
    /// partition; the same header from anyone else, or a hop that is not a proxy, is not believed.</summary>
    [Fact]
    public async Task With_trusted_proxies_the_forwarded_client_ip_is_used_but_only_behind_a_trusted_proxy()
    {
        await using var factory = LimitedHost([("SessionsPerIpPerMinute", 1)], trustedProxies: ["10.0.0.0/8", "192.0.2.1"]);
        var client = VisitorClient(factory);

        // Behind the proxy: one partition per forwarded client.
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "10.1.2.3", forwardedFor: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "10.1.2.3", forwardedFor: "198.51.100.2")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a different visitor behind the same proxy");
        await AssertRateLimitedAsync(await SessionFromAsync(client, Guid.NewGuid(), ip: "10.7.7.7", forwardedFor: "198.51.100.1"), maxSeconds: 60);
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "192.0.2.1", forwardedFor: "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "a single-address proxy, same client");

        // A client that puts its own address in front of the proxy's entry is not believed: the chain stops
        // at the first address that is not a trusted proxy.
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "10.1.2.3", forwardedFor: "203.0.113.50, 198.51.100.3")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "10.1.2.3", forwardedFor: "203.0.113.99, 198.51.100.3")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "counted as 198.51.100.3 both times");

        // Anyone who is not a trusted proxy: the header means nothing, the connection is the client.
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "203.0.113.5", forwardedFor: "198.51.100.200")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionFromAsync(client, Guid.NewGuid(), ip: "203.0.113.5", forwardedFor: "198.51.100.201")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>The same-origin check sees the address visitors used — behind a trusted proxy that terminates TLS.</summary>
    [Fact]
    public async Task Behind_a_trusted_proxy_the_forwarded_scheme_and_host_are_the_apis_own_origin()
    {
        const string PublicOrigin = "https://assistant.example.org";
        await using var proxied = LimitedHost([], trustedProxies: ["10.0.0.0/8"]);
        await using var direct = LimitedHost([]);

        async Task<string?> ReasonAsync(WebApplicationFactory<Program> factory)
        {
            var response = await SessionFromAsync(
                VisitorClient(factory), Guid.NewGuid(), ip: "10.1.2.3", origin: PublicOrigin,
                forwardedProto: "https", forwardedHost: "assistant.example.org");
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            return (await BodyJsonAsync(response)).GetProperty("reason").GetString();
        }

        // Trusted: https://assistant.example.org is the request's own origin, so it gets as far as the handler.
        (await ReasonAsync(proxied)).ShouldBe("public-assistant");
        // Not configured: the headers are ignored, the request is http://localhost, and that origin is foreign.
        (await ReasonAsync(direct)).ShouldBe("public-origin");
    }

    // --- Helpers --------------------------------------------------------------------------------------

    /// <summary>A host of the shared database whose limits are <paramref name="limits"/> (the others stay out of the way)
    /// and whose connections can claim any remote address with <c>X-Test-Remote-Ip</c> (a test server has none).</summary>
    private WebApplicationFactory<Program> LimitedHost(
        (string Name, int Value)[] limits,
        string[]? trustedProxies = null,
        Action<IWebHostBuilder>? configure = null) =>
        _host.Factory.WithWebHostBuilder(builder =>
        {
            foreach (var (name, value) in limits)
            {
                builder.UseSetting($"PublicChannels:RateLimits:{name}", value.ToString(CultureInfo.InvariantCulture));
            }

            for (var i = 0; i < (trustedProxies?.Length ?? 0); i++)
            {
                builder.UseSetting($"PublicChannels:TrustedProxies:{i}", trustedProxies![i]);
            }

            builder.ConfigureTestServices(services => services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteAddressFilter>());
            configure?.Invoke(builder);
        });

    /// <summary>Sets the connection's remote address from <c>X-Test-Remote-Ip</c>, before anything else runs.</summary>
    private sealed class RemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var value) && IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>A token for a fresh visitor of <paramref name="assistantId"/> (an assistant that need not exist).</summary>
    private static string TokenFor(WebApplicationFactory<Program> factory, Guid assistantId) =>
        factory.Services.GetRequiredService<VisitorTokens>().Issue(assistantId, Guid.NewGuid()).Token;

    private static async Task<HttpResponseMessage> SessionFromAsync(
        HttpClient client, Guid assistantId, string? ip = null, string? forwardedFor = null, string? origin = null,
        string? forwardedProto = null, string? forwardedHost = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SessionsPath(assistantId))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { host = (string?)null }),
        };
        AddSource(request, ip, forwardedFor, origin, forwardedProto, forwardedHost);
        var response = await client.SendAsync(request, CancellationToken);
        await response.Content.LoadIntoBufferAsync(CancellationToken);
        return response;
    }

    private static async Task<HttpResponseMessage> RunFromAsync(HttpClient client, Guid assistantId, string token, string? ip = null)
    {
        using var request = RunRequest(assistantId, token, RunInput(RelatedQuestion));
        AddSource(request, ip, forwardedFor: null, origin: null, forwardedProto: null, forwardedHost: null);
        var response = await client.SendAsync(request, CancellationToken);
        await response.Content.LoadIntoBufferAsync(CancellationToken);
        return response;
    }

    private static void AddSource(
        HttpRequestMessage request, string? ip, string? forwardedFor, string? origin, string? forwardedProto, string? forwardedHost)
    {
        void Add(string name, string? value)
        {
            if (value is not null)
            {
                request.Headers.Add(name, value);
            }
        }

        Add(RemoteIpHeader, ip);
        Add("X-Forwarded-For", forwardedFor);
        Add("X-Forwarded-Proto", forwardedProto);
        Add("X-Forwarded-Host", forwardedHost);
        Add("Origin", origin);
    }

    private static int RetryAfterSeconds(HttpResponseMessage response) =>
        int.Parse(response.Headers.GetValues("Retry-After").Single(), NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary><c>429</c>, a whole number of seconds in <c>Retry-After</c> (1 to <paramref name="maxSeconds"/>) and the
    /// same small problem body whichever partition refused.</summary>
    private static async Task AssertRateLimitedAsync(HttpResponseMessage response, int maxSeconds)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, body);
        RetryAfterSeconds(response).ShouldBeInRange(1, maxSeconds);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("status").GetInt32().ShouldBe(429);
        json.GetProperty("reason").GetString().ShouldBe("rate-limited");
        json.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}
