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
/// span. Uses an in-memory exporter and <c>/health/live</c> (no database, no Docker, no
/// OTLP endpoint) so it runs everywhere, including CI machines without Docker and this
/// worktree's no-Docker constraint.
///
/// The in-memory exporter is added via a second <c>AddOpenTelemetry().WithTracing(...)</c>
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
        var exportedActivities = new List<Activity>();
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services
                    .AddOpenTelemetry()
                    .WithTracing(tracing => tracing.AddInMemoryExporter(exportedActivities))));

        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The in-memory exporter uses a simple (non-batching) processor, so the server-side
        // activity for the request above has already been exported by the time the client
        // finishes reading the response. ForceFlush is still called for good measure.
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

        exportedActivities.ShouldContain(activity => activity.Source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
