using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests;

/// <summary>
/// Covers the "database unreachable" side of <c>/health/ready</c> without needing
/// Docker: pointing the app at a connection string nothing is listening on is enough
/// to exercise the same failure path a stopped Postgres container would. The "database
/// reachable" side needs a real Postgres and lives in
/// <see cref="DatabaseIntegrationTests"/> (tagged <see cref="TestCategories.Docker"/>).
/// </summary>
public class HealthReadyEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthReadyEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString));
    }

    [Fact]
    public async Task Health_ready_returns_service_unavailable_when_database_unreachable()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Health_live_is_unaffected_by_an_unreachable_database()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
