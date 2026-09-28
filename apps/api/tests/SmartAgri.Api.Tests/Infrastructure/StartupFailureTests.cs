using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// <see cref="StartupFailure"/> still finds why the host refused to start when the factory loses
/// the race described there (issue #96). No database needed.
/// </summary>
public sealed class StartupFailureTests
{
    [Fact]
    public void When_the_host_is_disposed_before_the_factory_starts_it_the_factory_loses_the_reason()
    {
        // Documents the race StartupFailure exists for; if a future WebApplicationFactory rethrows
        // the host's own exception here, StartupFailure can go.
        using var factory = InvalidJobOptions(new LateStartingFactory());

        Should.Throw<ObjectDisposedException>(() => factory.CreateClient()).ObjectName.ShouldBe(nameof(IServiceProvider));
    }

    [Fact]
    public void When_the_host_is_disposed_before_the_factory_starts_it_the_reason_is_still_found()
    {
        using var factory = InvalidJobOptions(new LateStartingFactory());

        StartupFailure.Of(factory).ShouldBeOfType<OptionsValidationException>().Message.ShouldContain("Jobs:Concurrency must be 1-64.");
    }

    [Fact]
    public void Without_the_race_the_reason_is_found_too()
    {
        using var factory = InvalidJobOptions(new WebApplicationFactory<Program>());

        StartupFailure.Of(factory).ShouldBeOfType<OptionsValidationException>().Message.ShouldContain("Jobs:Concurrency must be 1-64.");
    }

    // JobOptions are validated on start, so this host fails inside app.Run(), after it is built.
    private static WebApplicationFactory<Program> InvalidJobOptions(WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString)
            .UseSetting("Jobs:Concurrency", "0"));

    /// <summary>
    /// Starts the host it has built only once <c>Program</c>'s own thread has failed in
    /// <c>app.Run()</c> and disposed it: the losing side of the race, every time.
    /// </summary>
    private sealed class LateStartingFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = builder.Build();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!IsDisposed(host.Services))
            {
                DateTime.UtcNow.ShouldBeLessThan(deadline, "the host should fail to start and be disposed");
                Thread.Sleep(10);
            }

            host.Start();
            return host;
        }

        private static bool IsDisposed(IServiceProvider services)
        {
            try
            {
                services.GetService(typeof(IHostEnvironment));
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
}
