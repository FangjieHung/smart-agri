using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Databases;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests.Databases;

/// <summary>The statistics time zone (<c>Statistics:TimeZone</c>, M4 #147) is validated on start:
/// a typo fails the host with a message naming the setting, instead of surfacing at the first
/// statistics request. No database needed.</summary>
public sealed class StatisticsOptionsStartupTests
{
    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("")]
    public void An_unknown_time_zone_id_refuses_to_start(string id)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString)
            .UseSetting("Statistics:TimeZone", id));

        StartupFailure.Of(factory).ShouldBeOfType<OptionsValidationException>().Message.ShouldContain("Statistics:TimeZone");
    }

    [Fact]
    public void The_default_is_Taipei_and_a_known_id_resolves()
    {
        new StatisticsOptions().TryResolve()!.BaseUtcOffset.ShouldBe(TimeSpan.FromHours(8));
        new StatisticsOptions { TimeZone = "Europe/Berlin" }.TryResolve().ShouldNotBeNull();
        new StatisticsOptions { TimeZone = "Nope/Nowhere" }.TryResolve().ShouldBeNull();
    }
}
