using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Jobs;

namespace SmartAgri.Api.Tests.Jobs;

/// <summary>Job queue configuration; no database needed.</summary>
public class JobOptionsTests
{
    [Fact]
    public void Defaults_run_the_worker_one_job_at_a_time()
    {
        var options = new JobOptions();

        options.WorkerEnabled.ShouldBeTrue();
        options.Concurrency.ShouldBe(1);
        options.Validate().ShouldBeNull();
    }

    [Fact]
    public void Unusable_settings_are_reported()
    {
        new JobOptions { Concurrency = 0 }.Validate().ShouldNotBeNull();
        new JobOptions { PollInterval = TimeSpan.Zero }.Validate().ShouldNotBeNull();
        new JobOptions { LeaseDuration = TimeSpan.Zero }.Validate().ShouldNotBeNull();
        new JobOptions { RetryBaseDelay = TimeSpan.Zero }.Validate().ShouldNotBeNull();
        new JobOptions { RetryBaseDelay = TimeSpan.FromMinutes(5), RetryMaxDelay = TimeSpan.FromMinutes(1) }.Validate().ShouldNotBeNull();
    }

    [Fact]
    public async Task The_Api_registers_the_worker_and_binds_the_Jobs_section()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Jobs:Concurrency", "3");
            builder.UseSetting("Jobs:PollInterval", "00:00:05");
        });

        var options = factory.Services.GetRequiredService<IOptions<JobOptions>>().Value;
        options.Concurrency.ShouldBe(3);
        options.PollInterval.ShouldBe(TimeSpan.FromSeconds(5));
        factory.Services.GetServices<IHostedService>().OfType<JobWorker>().ShouldHaveSingleItem();

        // Registered by the Api itself (document processing, M2 plan Slice 5). Because
        // there is a handler, the worker would poll the configured database: every test
        // host has it turned off (TestHostDefaults), which is also what this host sees.
        factory.Services.GetRequiredService<JobRunner>().Kinds.ShouldBe(["knowledge.process-version"]);
        options.WorkerEnabled.ShouldBeFalse();
    }

    [Fact]
    public void A_test_host_can_still_turn_the_worker_on()
    {
        // UseSetting wins over TestHostDefaults' environment variable. The database is
        // unreachable, so the worker that starts here finds nothing to do.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", Infrastructure.PostgresFixture.UnreachableConnectionString);
            builder.UseSetting("Jobs:WorkerEnabled", "true");
        });

        factory.Services.GetRequiredService<IOptions<JobOptions>>().Value.WorkerEnabled.ShouldBeTrue();
    }
}
