using SmartAgri.Api.Jobs;
using SmartAgri.Application.Reports;

namespace SmartAgri.Api.Reports;

public static class ReportServiceCollectionExtensions
{
    /// <summary>
    /// Registers the periodic reports (M4 #150): the schedule service and the handlers of
    /// <see cref="GenerateDatabaseReportJob.Kind"/> and <see cref="SummarizeDatabaseReportJob.Kind"/> jobs.
    /// Requires <c>AddBackgroundJobs</c>, <c>AddChat</c> and the fixed query service
    /// (<c>DatabaseFixedQueryService</c>, <c>Statistics</c> options).
    /// </summary>
    public static IServiceCollection AddPeriodicReports(this IServiceCollection services)
    {
        services.AddScoped<ReportScheduleService>();
        services.AddJobHandler<GenerateDatabaseReportHandler>(GenerateDatabaseReportJob.Kind);
        services.AddJobHandler<SummarizeDatabaseReportHandler>(SummarizeDatabaseReportJob.Kind);
        return services;
    }
}
