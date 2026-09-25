namespace SmartAgri.Api.Setup;

/// <summary>
/// The terminal <c>setup</c> talks to. The one-time password is written to
/// <see cref="Out"/> and nowhere else: deliberately not through <c>ILogger</c>, whose
/// providers (console formatter, OpenTelemetry log exporter, anything added later) could
/// forward it to files or a collector.
/// </summary>
public interface ISetupConsole
{
    TextWriter Out { get; }

    TextWriter Error { get; }

    /// <summary>Whether prompts can be answered (stdin is a terminal, not redirected).</summary>
    bool IsInteractive { get; }

    /// <summary>The next input line, or <see langword="null"/> at end of input.</summary>
    string? ReadLine();
}

/// <summary>The process's own stdin/stdout/stderr.</summary>
public sealed class SystemSetupConsole : ISetupConsole
{
    public static readonly SystemSetupConsole Instance = new();

    private SystemSetupConsole()
    {
    }

    public TextWriter Out => Console.Out;

    public TextWriter Error => Console.Error;

    public bool IsInteractive => !Console.IsInputRedirected;

    public string? ReadLine() => Console.In.ReadLine();
}
