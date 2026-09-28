using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// Reads why a test host refused to start — for failures inside <c>app.Run()</c> (options
/// validated with <c>ValidateOnStart</c>, hosted services), not ones thrown while the host is
/// built, which <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> always rethrows.
/// </summary>
/// <remarks>
/// When <c>app.Run()</c> fails, <see cref="WebApplicationFactory{TEntryPoint}"/> does not reliably
/// rethrow the host's exception (issue #96). <c>Program</c> runs on its own thread: its
/// <c>app.Run()</c> fails and disposes the host, while the factory's thread is in
/// <c>DeferredHost.StartAsync</c>, which first resolves <c>IHostApplicationLifetime</c> from that
/// host. When the dispose wins, <c>CreateClient()</c> throws <see cref="ObjectDisposedException"/>
/// ("Object name: 'IServiceProvider'") and the original exception is lost — measured at 4 in
/// 1,100 starts with 8–16 hosts starting at once, never with one at a time. The host logs the
/// exception it failed with before it rethrows it and before anything is disposed, and that log
/// entry is what an operator sees too.
/// </remarks>
internal static class StartupFailure
{
    private const string HostCategory = "Microsoft.Extensions.Hosting.Internal.Host";

    /// <summary>
    /// Starts <paramref name="factory"/>'s host, which must refuse to start, and returns the
    /// exception the host logged as the reason.
    /// </summary>
    public static Exception Of(WebApplicationFactory<Program> factory)
    {
        var log = new HostErrorLog();
        using var logged = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(log)));

        Should.Throw<Exception>(() => logged.CreateClient());

        return log.Errors.ShouldHaveSingleItem("the host should log exactly one exception it failed to start with");
    }

    private sealed class HostErrorLog : ILoggerProvider
    {
        public ConcurrentQueue<Exception> Errors { get; } = new();

        public ILogger CreateLogger(string categoryName) => categoryName == HostCategory ? new HostLogger(Errors) : NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class HostLogger : ILogger
    {
        private readonly ConcurrentQueue<Exception> _errors;

        public HostLogger(ConcurrentQueue<Exception> errors)
        {
            _errors = errors;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null)
            {
                _errors.Enqueue(exception);
            }
        }
    }
}
