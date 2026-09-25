using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using SmartAgri.Api.Setup;

namespace SmartAgri.Api.Tests.Setup;

/// <summary>A scripted terminal: canned input lines, captured output.</summary>
public sealed class FakeSetupConsole : ISetupConsole
{
    private readonly Queue<string> _input;

    public FakeSetupConsole(bool interactive, params string[] input)
    {
        IsInteractive = interactive;
        _input = new Queue<string>(input);
    }

    public StringWriter OutWriter { get; } = new();

    public StringWriter ErrorWriter { get; } = new();

    public TextWriter Out => OutWriter;

    public TextWriter Error => ErrorWriter;

    public bool IsInteractive { get; }

    public int LinesRead { get; private set; }

    public string? ReadLine()
    {
        if (_input.Count == 0)
        {
            return null;
        }

        LinesRead++;
        return _input.Dequeue();
    }

    /// <summary>The password from the "一次性密碼：" line, or <see langword="null"/>.</summary>
    public string? PrintedPassword =>
        OutWriter.ToString().Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("一次性密碼：", StringComparison.Ordinal))
            .Select(line => line["一次性密碼：".Length..])
            .SingleOrDefault();
}

/// <summary>An <see cref="IInitialSetupStore"/> with a fixed state that records what it is asked to create.</summary>
public sealed class FakeInitialSetupStore : IInitialSetupStore
{
    public InitialSetupState State { get; set; } = InitialSetupState.Ready;

    public InitialSetupResult? CreateResult { get; set; }

    public List<(InitialSetupRequest Request, string Password)> Created { get; } = [];

    public Task<InitialSetupState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(State);

    public Task<InitialSetupResult> CreateAsync(InitialSetupRequest request, string password, CancellationToken cancellationToken)
    {
        Created.Add((request, password));
        return Task.FromResult(CreateResult ?? InitialSetupResult.Created(Guid.NewGuid()));
    }
}

/// <summary>
/// Everything the host's telemetry pipeline sees, as text: every <c>ILogger</c> entry
/// (message, structured values, exception) at every level through a plain logger provider
/// and through OpenTelemetry's logger provider, and every exported span (names, tags,
/// events, status). Used to prove the one-time password never reaches any of them.
/// </summary>
public sealed class TelemetryCapture
{
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly ConcurrentQueue<string> _otelLogs = new();
    private readonly List<Activity> _activities = [];

    public IReadOnlyCollection<string> Logs => _logs;

    public IReadOnlyCollection<string> OpenTelemetryLogs => _otelLogs;

    public IReadOnlyList<string> Spans
    {
        get
        {
            lock (_activities)
            {
                return [.. _activities.Select(Describe)];
            }
        }
    }

    /// <summary>All captured text (logs, OpenTelemetry logs, spans).</summary>
    public IEnumerable<string> All => Logs.Concat(OpenTelemetryLogs).Concat(Spans);

    /// <summary>Wires the capture into a test host.</summary>
    public void Attach(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(new CapturingLoggerProvider(_logs));
            logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
            logging.AddFilter<OpenTelemetryLoggerProvider>(null, LogLevel.Trace);
        });

        builder.ConfigureServices(services =>
            services
                .AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddInMemoryExporter(_activities))
                .WithLogging(
                    logging => logging.AddProcessor(new CapturingLogProcessor(_otelLogs)),
                    options =>
                    {
                        options.IncludeFormattedMessage = true;
                        options.IncludeScopes = true;
                    }));
    }

    private static string Describe(Activity activity) =>
        string.Join(
            " | ",
            new[] { activity.Source.Name, activity.DisplayName, activity.OperationName, activity.StatusDescription ?? string.Empty }
                .Concat(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))
                .Concat(activity.Events.SelectMany(activityEvent =>
                    new[] { activityEvent.Name }.Concat(activityEvent.Tags.Select(tag => $"{tag.Key}={tag.Value}"))))
                .Concat(activity.Baggage.Select(item => $"{item.Key}={item.Value}")));

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _sink;

        public CapturingLoggerProvider(ConcurrentQueue<string> sink)
        {
            _sink = sink;
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _sink);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _sink;

        public CapturingLogger(string category, ConcurrentQueue<string> sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            _sink.Enqueue($"{_category} scope: {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(", ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;
            _sink.Enqueue($"{logLevel} {_category}: {formatter(state, exception)} [{values}] {exception}");
        }
    }

    private sealed class CapturingLogProcessor : BaseProcessor<LogRecord>
    {
        private readonly ConcurrentQueue<string> _sink;

        public CapturingLogProcessor(ConcurrentQueue<string> sink)
        {
            _sink = sink;
        }

        // LogRecord instances are pooled and reused, so copy everything to text right away.
        public override void OnEnd(LogRecord data)
        {
            var attributes = data.Attributes is null
                ? string.Empty
                : string.Join(", ", data.Attributes.Select(pair => $"{pair.Key}={pair.Value}"));
            _sink.Enqueue($"{data.LogLevel} {data.CategoryName}: {data.FormattedMessage} {data.Body} [{attributes}] {data.Exception}");
        }
    }
}
