namespace SmartAgri.Domain.Observability;

/// <summary>
/// Name of the application's own <see cref="System.Diagnostics.Metrics.Meter"/> (e.g. the
/// job queue depth), registered with the OpenTelemetry SDK's meter provider via
/// <c>AddMeter(SmartAgriMeter.Name)</c> in
/// <c>SmartAgri.Api/Observability/OpenTelemetryExtensions.cs</c>. Meters are created through
/// the host's <c>IMeterFactory</c> rather than a static instance, so every host (and test
/// host) has its own instruments.
/// </summary>
public static class SmartAgriMeter
{
    public const string Name = "SmartAgri";
}
