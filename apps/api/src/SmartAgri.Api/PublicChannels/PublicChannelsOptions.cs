using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Organizations;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// Configuration section <c>PublicChannels</c> (M5a plan §3 B, F and H; the rest of the section — rate
/// limits, trusted proxies — arrives with later slices).
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

    /// <summary>
    /// The monthly chat-model token limit (input + output) of an organization whose own
    /// <c>MonthlyTokenLimit</c> is unset. <c>0</c> suspends every such organization's website
    /// replies. Nullable so that a blank setting (an empty <c>.env</c> value passed through compose)
    /// binds as unset instead of failing; read <see cref="EffectiveDefaultMonthlyTokenLimit"/>.
    /// </summary>
    public long? DefaultMonthlyTokenLimit { get; set; }

    /// <summary>
    /// Also lets pages on <c>http://localhost:*</c> embed the chat window (the
    /// <c>frame-ancestors</c> of <c>GET /use/{id}</c>, M5a plan §3 B), so the embed code can be tried
    /// on a developer's own machine. Only allowed in Development and Testing: any other environment
    /// refuses to start with it on, because every program on a visitor's machine could then frame the
    /// page.
    /// </summary>
    public bool AllowLocalhostAncestors { get; set; }

    /// <summary><see cref="DefaultMonthlyTokenLimit"/>, or 2,000,000 when unset (decision C).</summary>
    public long EffectiveDefaultMonthlyTokenLimit =>
        DefaultMonthlyTokenLimit ?? OrganizationTokenUsageRules.DefaultMonthlyTokenLimit;

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
    /// absolute <c>http</c>/<c>https</c> URL without user info, query or fragment, or a negative
    /// <see cref="DefaultMonthlyTokenLimit"/>, or <see cref="AllowLocalhostAncestors"/> outside
    /// Development and Testing.</summary>
    internal sealed class Validator : IValidateOptions<PublicChannelsOptions>
    {
        private readonly IHostEnvironment _environment;

        public Validator(IHostEnvironment environment)
        {
            _environment = environment;
        }

        public ValidateOptionsResult Validate(string? name, PublicChannelsOptions options)
        {
            var failures = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) && options.ResolvedPublicBaseUrl is null)
            {
                failures.Add(
                    $"PublicChannels:PublicBaseUrl '{options.PublicBaseUrl}' must be an absolute http(s) URL such as https://assistant.example.org (no query or fragment).");
            }

            if (options.DefaultMonthlyTokenLimit < 0)
            {
                failures.Add(
                    $"PublicChannels:DefaultMonthlyTokenLimit {options.DefaultMonthlyTokenLimit} must be 0 or more (tokens per month; 0 suspends website replies).");
            }

            if (options.AllowLocalhostAncestors
                && !(_environment.IsDevelopment() || _environment.IsEnvironment("Testing")))
            {
                failures.Add(
                    $"PublicChannels:AllowLocalhostAncestors=true is only allowed in the Development and Testing environments, not in '{_environment.EnvironmentName}'.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
