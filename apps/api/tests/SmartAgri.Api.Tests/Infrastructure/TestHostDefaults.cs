using System.Runtime.CompilerServices;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// Settings every Api host started by this test assembly gets, before any test runs.
/// </summary>
internal static class TestHostDefaults
{
    /// <summary>
    /// Turns the background job worker off in every test host (<c>Jobs__WorkerEnabled</c>,
    /// read by <c>WebApplication.CreateBuilder</c>). The Api registers a real job handler
    /// (<c>knowledge.process-version</c>), so a host started with its Development settings
    /// — <c>HealthEndpointTests</c>, <c>OpenTelemetryTracingTests</c> and others that use a
    /// plain <c>WebApplicationFactory&lt;Program&gt;</c> — would otherwise poll whatever
    /// database <c>appsettings.Development.json</c> names (a developer's local one) and
    /// process its jobs. Tests that need jobs run <c>JobRunner</c> themselves; a host that
    /// really needs the worker can still turn it on with <c>UseSetting</c>, which wins over
    /// environment variables.
    /// </summary>
#pragma warning disable CA2255 // A test assembly is the one place a module initializer is meant for.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void TurnOffTheJobWorker() => Environment.SetEnvironmentVariable("Jobs__WorkerEnabled", "false");
}
