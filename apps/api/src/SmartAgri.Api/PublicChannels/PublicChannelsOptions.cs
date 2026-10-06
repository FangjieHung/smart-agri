using Microsoft.Extensions.Options;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// Configuration section <c>PublicChannels</c> (M5a plan §3 H; the rest of the section — rate
/// limits, trusted proxies, localhost ancestors, the default token limit — arrives with later
/// slices).
/// </summary>
public sealed class PublicChannelsOptions
{
    public const string SectionName = "PublicChannels";

    /// <summary>
    /// The address visitors' browsers reach this API at, e.g. <c>https://assistant.example.org</c>
    /// (no path is needed; a trailing <c>/</c> is ignored). The website channel's embed code points
    /// at <c>{PublicBaseUrl}/embed.js</c>. Optional at startup: without it the embed code is
    /// <see langword="null"/> and publishing a website channel is refused with
    /// <c>422 public-base-url</c>.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary><see cref="PublicBaseUrl"/> normalized (absolute, escaped, no trailing <c>/</c>),
    /// or <see langword="null"/> when unset or blank.</summary>
    public string? ResolvedPublicBaseUrl =>
        TryResolve(PublicBaseUrl, out var resolved) ? resolved : null;

    /// <summary>The <c>&lt;script&gt;</c> line a customer pastes into their site, or
    /// <see langword="null"/> without <see cref="PublicBaseUrl"/>.</summary>
    public string? EmbedCode(Guid assistantId) =>
        ResolvedPublicBaseUrl is { } baseUrl
            ? $"<script src=\"{baseUrl}/embed.js\" data-assistant=\"{assistantId}\" async></script>"
            : null;

    private static bool TryResolve(string? raw, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return false;
        }

        resolved = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>Refuses to start with a <see cref="PublicBaseUrl"/> that is set but is not an
    /// absolute <c>http</c>/<c>https</c> URL without user info, query or fragment.</summary>
    internal sealed class Validator : IValidateOptions<PublicChannelsOptions>
    {
        public ValidateOptionsResult Validate(string? name, PublicChannelsOptions options) =>
            string.IsNullOrWhiteSpace(options.PublicBaseUrl) || options.ResolvedPublicBaseUrl is not null
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    $"PublicChannels:PublicBaseUrl '{options.PublicBaseUrl}' must be an absolute http(s) URL such as https://assistant.example.org (no query or fragment).");
    }
}
