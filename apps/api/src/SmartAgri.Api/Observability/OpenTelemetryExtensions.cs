using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SmartAgri.Domain.Observability;

namespace SmartAgri.Api.Observability;

/// <summary>
/// Registers OpenTelemetry traces, metrics and logs (ASP.NET Core, HttpClient and Npgsql
/// instrumentation, plus the custom <see cref="SmartAgriActivitySource"/>) — see the
/// observability ADR (<c>docs/adr/2026-09-25-observability.md</c>) and the M1 skeleton
/// plan, Slice 3.
///
/// Kept as a single extension method/file, called once from <c>Program.cs</c>, so it
/// doesn't spread OTel wiring across a file other slices are also editing.
///
/// The OTLP exporter is only registered when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
/// When it isn't, no exporter is added at all (not even a default one pointed at
/// localhost) — instrumentation still runs (so in-process consumers such as the
/// integration test's in-memory exporter, or `dotnet-trace`, still see spans), but nothing
/// is sent anywhere and the app never blocks on an absent collector.
/// </summary>
public static class OpenTelemetryExtensions
{
    private const string ServiceName = "smart-agri-api";

    public static IHostApplicationBuilder AddSmartAgriObservability(this IHostApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(SmartAgriActivitySource.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();

                if (exportOtlp)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsqlInstrumentation();

                if (exportOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            });

        if (exportOtlp)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName));
                logging.AddOtlpExporter();
            });
        }

        return builder;
    }
}
