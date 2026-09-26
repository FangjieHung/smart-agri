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
        options.WorkerEnabled.ShouldBeTrue();
        options.Concurrency.ShouldBe(3);
        options.PollInterval.ShouldBe(TimeSpan.FromSeconds(5));
        factory.Services.GetServices<IHostedService>().OfType<JobWorker>().ShouldHaveSingleItem();

        // No handler is registered yet (M2 plan, Slice 6 adds document processing), so the
        // worker never polls the database.
        factory.Services.GetRequiredService<JobRunner>().Kinds.ShouldBeEmpty();
    }
}
