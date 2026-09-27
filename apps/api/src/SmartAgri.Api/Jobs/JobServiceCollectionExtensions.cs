using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Jobs;
using SmartAgri.Domain.Jobs;
using SmartAgri.Infrastructure.Jobs;

namespace SmartAgri.Api.Jobs;

public static class JobServiceCollectionExtensions
{
    /// <summary>
    /// Registers the job queue: <see cref="JobClaimer"/>, <see cref="JobRunner"/>,
    /// <see cref="JobQueueMetrics"/> and the <see cref="JobWorker"/> hosted service, with
    /// <see cref="JobOptions"/> from the <c>Jobs</c> configuration section. Requires
    /// <c>AddOrganizationTenancy</c> (the runner enters each job's organization through it).
    /// </summary>
    public static IServiceCollection AddBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobOptions>()
            .Bind(configuration.GetSection(JobOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JobOptions>, JobOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<JobClaimer>();
        services.AddSingleton<JobRunner>();
        services.AddSingleton<JobQueueMetrics>();
        services.AddHostedService<JobWorker>();
        return services;
    }

    /// <summary>
    /// Makes <typeparamref name="THandler"/> process jobs of <paramref name="kind"/>. It is
    /// resolved from a new scope for every job, acting for the job's organization. One
    /// handler type may serve several kinds; each kind has exactly one handler.
    /// </summary>
    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services, string kind)
        where THandler : class, IJobHandler
    {
        BackgroundJob.RequireValidKind(kind);
        services.AddSingleton(new JobHandlerRegistration(kind, typeof(THandler)));
        services.TryAddScoped<THandler>();
        return services;
    }

    private sealed class JobOptionsValidator : IValidateOptions<JobOptions>
    {
        public ValidateOptionsResult Validate(string? name, JobOptions options) =>
            options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
