using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;

namespace SmartAgri.Api.Tests;

/// <summary>
/// Slice 3 acceptance: a request must produce at least one <c>Microsoft.AspNetCore</c>
/// span. Uses <c>/health/live</c> (no database, no Docker, no OTLP endpoint) so it runs
/// everywhere, including CI machines without Docker.
///
/// The capturing processor is added via a second <c>AddOpenTelemetry().WithTracing(...)</c>
/// call in the test host: OpenTelemetry's DI integration keeps a single
/// <see cref="OpenTelemetry.Trace.TracerProviderBuilder"/> per app, so this appends an
/// extra processor to the same provider that
/// <c>SmartAgri.Api.Observability.OpenTelemetryExtensions.AddSmartAgriObservability</c>
/// already configured in <c>Program.cs</c>, rather than building a second, separate one.
/// </summary>
public class OpenTelemetryTracingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OpenTelemetryTracingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_live_request_produces_an_AspNetCore_span()
    {
        var endedSources = new ConcurrentQueue<string>();
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services
                    .AddOpenTelemetry()
                    .WithTracing(tracing => tracing.AddProcessor(new SourceNameProcessor(endedSources)))));

        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The server-side activity ends after the response has been sent, so the client can
        // finish reading it before the span is recorded (ForceFlush cannot wait for a span
        // that has not ended yet). Wait for it instead of asserting straight away.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!HasAspNetCoreSpan(endedSources) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        endedSources.ShouldContain(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    private static bool HasAspNetCoreSpan(ConcurrentQueue<string> endedSources) =>
        endedSources.Any(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));

    /// <summary>
    /// Records the source name of every ended span. Spans end on whichever thread finishes
    /// them, so the sink must be thread-safe (the in-memory exporter's plain list is not).
    /// </summary>
    private sealed class SourceNameProcessor : BaseProcessor<Activity>
    {
        private readonly ConcurrentQueue<string> _sink;

        public SourceNameProcessor(ConcurrentQueue<string> sink)
        {
            _sink = sink;
        }

        public override void OnEnd(Activity data) => _sink.Enqueue(data.Source.Name);
    }
}
