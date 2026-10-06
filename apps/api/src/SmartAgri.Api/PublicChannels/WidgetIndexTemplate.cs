using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// The widget's <c>index.html</c> as built, read from <c>Widget:RootPath</c> and kept while the file does
/// not change (a rebuilt widget is picked up without restarting). The per-response part, the nonce, is
/// added by <see cref="WithNonce"/>.
/// </summary>
public sealed partial class WidgetIndexTemplate
{
    private readonly IOptions<WidgetOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<WidgetIndexTemplate> _logger;
    private readonly object _gate = new();
    private (string Path, DateTime LastWriteUtc, long Length, string Html)? _cached;

    public WidgetIndexTemplate(IOptions<WidgetOptions> options, IHostEnvironment environment, ILogger<WidgetIndexTemplate> logger)
    {
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// The page's HTML, or <see langword="false"/> with an explanation for an operator: there is no build at
    /// <c>Widget:RootPath</c>, or its <c>index.html</c> has no <c>&lt;app-root&gt;</c> to hand the nonce to
    /// (without it Angular's styles would be blocked and the window would show no styling).
    /// </summary>
    public bool TryLoad(out string html, out string problem)
    {
        var path = System.IO.Path.Combine(_options.Value.ResolveRoot(_environment), "index.html");
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            html = string.Empty;
            problem = $"The chat window is not built into this server: {path} does not exist. "
                + "Build it with `npx nx build widget --configuration=production` and point Widget:RootPath at "
                + "dist/smart-agri-widget/browser (the Docker image does this).";
            _logger.LogWarning("Widget build not found at {Path} (Widget:RootPath).", path);
            return false;
        }

        lock (_gate)
        {
            if (_cached is not { } cached
                || cached.Path != path
                || cached.LastWriteUtc != info.LastWriteTimeUtc
                || cached.Length != info.Length)
            {
                var text = File.ReadAllText(path);
                _cached = (path, info.LastWriteTimeUtc, info.Length, text);
                cached = _cached.Value;
            }

            if (!AppRoot().IsMatch(cached.Html))
            {
                html = string.Empty;
                problem = $"{path} has no <app-root> element, so the nonce cannot be passed to Angular (ngCspNonce).";
                _logger.LogError("Widget index.html at {Path} has no <app-root> element.", path);
                return false;
            }

            html = cached.Html;
            problem = string.Empty;
            return true;
        }
    }

    /// <summary><paramref name="html"/> with <c>ngCspNonce="nonce"</c> on its <c>&lt;app-root&gt;</c>, which
    /// Angular reads to put the nonce on the <c>&lt;style&gt;</c> elements it inserts. The nonce is
    /// base64, so it needs no escaping in an attribute value.</summary>
    internal static string WithNonce(string html, string nonce) =>
        AppRoot().Replace(html, $"<app-root ngCspNonce=\"{nonce}\"", 1);

    [GeneratedRegex(@"<app-root(?=[\s>/])", RegexOptions.CultureInvariant)]
    private static partial Regex AppRoot();
}
