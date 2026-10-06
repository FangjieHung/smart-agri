using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shouldly;
using SmartAgri.Api.Errors;
using SmartAgri.Api.PublicChannels;

namespace SmartAgri.Api.Tests.PublicChannels;

/// <summary>The parts of the visitor API's rate limits that need no host (M5a plan §3 E; #197).</summary>
public class PublicRateLimitingTests
{
    [Theory]
    [InlineData("198.51.100.7", "198.51.100.7")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.7")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    public void A_client_is_its_ipv4_address_or_its_ipv6_64(string address, string expected)
    {
        PublicRateLimiting.ClientPartition(IPAddress.Parse(address)).ShouldBe(expected);
    }

    [Fact]
    public void A_connection_without_an_address_shares_one_partition()
    {
        PublicRateLimiting.ClientPartition(null).ShouldBe("unknown");
    }

    [Fact]
    public void The_line_webhook_is_limited_per_assistant_in_the_url_before_anything_else_runs()
    {
        using var limiter = PublicRateLimiting.CreateLimiter(new PublicRateLimitOptions { LineWebhooksPerAssistantPerMinute = 2 });
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();

        Acquire(limiter, first).ShouldBeTrue();
        Acquire(limiter, first.ToUpperInvariant()).ShouldBeTrue("the same GUID however it is written");
        Acquire(limiter, first).ShouldBeFalse();
        Acquire(limiter, second).ShouldBeTrue("each assistant has its own budget");

        // Every value that is not a GUID shares one partition.
        Acquire(limiter, "not-an-id").ShouldBeTrue();
        Acquire(limiter, "also-not").ShouldBeTrue();
        Acquire(limiter, "still-not").ShouldBeFalse();

        // Other endpoints are not counted against it, nor is a request without the marker.
        var unmarked = new DefaultHttpContext();
        unmarked.Request.RouteValues["assistantId"] = first;
        limiter.AttemptAcquire(unmarked).IsAcquired.ShouldBeTrue();

        static bool Acquire(System.Threading.RateLimiting.PartitionedRateLimiter<HttpContext> limiter, string assistantId)
        {
            var context = new DefaultHttpContext();
            context.SetEndpoint(new Endpoint(
                null, new EndpointMetadataCollection(new PublicRateLimitMarker(PublicRateLimitedEndpoint.LineWebhook)), "line-webhook"));
            context.Request.RouteValues["assistantId"] = assistantId;
            return limiter.AttemptAcquire(context).IsAcquired;
        }
    }

    [Fact]
    public async Task The_429_body_is_one_fixed_problem_whichever_partition_refused()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await ApiErrors.RateLimited().ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(429);
        context.Response.ContentType.ShouldBe("application/problem+json");
        context.Response.Body.Position = 0;
        var json = JsonDocument.Parse(context.Response.Body).RootElement;
        json.GetProperty("status").GetInt32().ShouldBe(429);
        json.GetProperty("title").GetString().ShouldBe("Too Many Requests");
        json.GetProperty("reason").GetString().ShouldBe("rate-limited");
    }

    /// <summary>Acceptance: the 429 contract is in <c>openapi/v1.json</c> for both rate-limited endpoints, and only for them.</summary>
    [Fact]
    public void The_429_is_in_the_openapi_document_for_the_session_and_chat_run_endpoints_only()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "apps", "api", "openapi", "v1.json")));
        var paths = document.RootElement.GetProperty("paths");

        var documented = new List<string>();
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Value.TryGetProperty("responses", out var responses) && responses.TryGetProperty("429", out _))
                {
                    documented.Add($"{operation.Name.ToUpperInvariant()} {path.Name}");
                }
            }
        }

        documented.Order().ShouldBe(
        [
            "POST /api/v1/public/assistants/{id}/chat/runs",
            "POST /api/v1/public/assistants/{id}/visitor-sessions",
        ]);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "nx.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (nx.json) above " + AppContext.BaseDirectory);
    }
}
