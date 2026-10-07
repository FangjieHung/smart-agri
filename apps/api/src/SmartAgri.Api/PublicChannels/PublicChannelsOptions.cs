using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// Configuration section <c>PublicChannels</c> (M5a plan §3 B, E, F and H).
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

    /// <summary>
    /// Rate limits of the visitor API (<c>/api/v1/public/*</c>, plan §3 E). Every value has a default
    /// (decision C), so the section is optional; see <see cref="PublicRateLimitOptions"/>.
    /// </summary>
    public PublicRateLimitOptions RateLimits { get; set; } = new();

    /// <summary>
    /// LINE answers (M5b #232, plan §3 D): see <see cref="PublicLineOptions"/>. Optional; every value
    /// has a default.
    /// </summary>
    public PublicLineOptions Line { get; set; } = new();

    /// <summary>
    /// Reverse proxies (IP addresses or CIDR networks, e.g. <c>10.0.0.0/8</c>) whose
    /// <c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c> and <c>X-Forwarded-Host</c> are believed
    /// (plan §3 E). Only when this is non-empty are those headers applied, and only when the request
    /// comes from one of these addresses; otherwise <c>X-Forwarded-For</c> is ignored and the client
    /// is the connection's source address. An entry may also hold several, separated by commas, so one
    /// <c>.env</c> value can carry a list. Without it, behind a reverse proxy, every visitor looks
    /// like one IP and shares one rate-limit partition.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>The addresses and networks of <see cref="TrustedProxies"/> (entries split on commas,
    /// semicolons and white space, blanks skipped), and the entries that are neither.</summary>
    public (IReadOnlyList<System.Net.IPAddress> Addresses, IReadOnlyList<System.Net.IPNetwork> Networks, IReadOnlyList<string> Invalid) ParseTrustedProxies()
    {
        var addresses = new List<System.Net.IPAddress>();
        var networks = new List<System.Net.IPNetwork>();
        var invalid = new List<string>();
        foreach (var entry in (TrustedProxies ?? []).SelectMany(
                     value => (value ?? string.Empty).Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)))
        {
            if (entry.Contains('/'))
            {
                if (System.Net.IPNetwork.TryParse(entry, out var network))
                {
                    networks.Add(network);
                    continue;
                }
            }
            else if (System.Net.IPAddress.TryParse(entry, out var address))
            {
                addresses.Add(address);
                continue;
            }

            invalid.Add(entry);
        }

        return (addresses, networks, invalid);
    }

    /// <summary><see cref="DefaultMonthlyTokenLimit"/>, or 2,000,000 when unset (decision C).</summary>
    public long EffectiveDefaultMonthlyTokenLimit =>
        DefaultMonthlyTokenLimit ?? OrganizationTokenUsageRules.DefaultMonthlyTokenLimit;

    /// <summary><see cref="PublicBaseUrl"/> normalized (absolute, escaped, no trailing <c>/</c>),
    /// or <see langword="null"/> when unset or blank.</summary>
    public string? ResolvedPublicBaseUrl =>
        TryResolve(PublicBaseUrl, out var resolved) ? resolved : null;

    /// <summary>The <c>&lt;script&gt;</c> line a customer pastes into their site, or
    /// <see langword="null"/> without <see cref="PublicBaseUrl"/>. The loader reads the launcher's
    /// position and colour only from the page (<c>apps/embed-loader/README.md</c>), so a bottom-left
    /// launcher is written into the line as <c>data-position="left"</c> (#205: it was dropped before)
    /// and a brand colour other than the default <see cref="WebsiteBrandColor.Forest"/> as
    /// <c>data-brand="{wire name}"</c> (#225). Leaving out the defaults keeps the line unchanged for
    /// the common case, and a line pasted before either attribute existed still means the defaults.</summary>
    public string? EmbedCode(
        Guid assistantId,
        WebsiteLauncherPosition position = WebsiteLauncherPosition.BottomRight,
        WebsiteBrandColor brandColor = WebsiteBrandColor.Forest)
    {
        if (ResolvedPublicBaseUrl is not { } baseUrl)
        {
            return null;
        }

        var positionAttribute = position == WebsiteLauncherPosition.BottomLeft ? " data-position=\"left\"" : "";
        var brandAttribute = brandColor == WebsiteBrandColor.Forest
            ? ""
            : $" data-brand=\"{WireNames<WebsiteBrandColor>.ToWire(brandColor)}\"";
        return $"<script src=\"{baseUrl}/embed.js\" data-assistant=\"{assistantId}\"{positionAttribute}{brandAttribute} async></script>";
    }

    /// <summary>The address LINE delivers an assistant's webhook events to (M5b plan §3 B, decision C:
    /// the connection test sets it on the LINE channel), or <see langword="null"/> without
    /// <see cref="PublicBaseUrl"/>.</summary>
    public string? LineWebhookUrl(Guid assistantId) =>
        ResolvedPublicBaseUrl is { } baseUrl ? $"{baseUrl}/api/v1/line/webhook/{assistantId}" : null;

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
    /// Development and Testing, or a rate limit below 1, or a <see cref="TrustedProxies"/> entry that is
    /// neither an IP address nor a CIDR network.</summary>
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

            AddRateLimitFailures(options.RateLimits, failures);

            if (options.Line is null)
            {
                failures.Add("PublicChannels:Line must not be null.");
            }
            else if (options.Line.ReplyDeadlineSeconds is < PublicLineOptions.MinReplyDeadlineSeconds or > PublicLineOptions.MaxReplyDeadlineSeconds)
            {
                failures.Add(
                    $"PublicChannels:Line:ReplyDeadlineSeconds {options.Line.ReplyDeadlineSeconds} must be {PublicLineOptions.MinReplyDeadlineSeconds} to {PublicLineOptions.MaxReplyDeadlineSeconds} (a LINE reply token lasts about a minute).");
            }

            var (_, _, invalidProxies) = options.ParseTrustedProxies();
            foreach (var entry in invalidProxies)
            {
                failures.Add(
                    $"PublicChannels:TrustedProxies entry '{entry}' must be an IP address (10.0.0.5) or a CIDR network (10.0.0.0/8).");
            }

            if (options.AllowLocalhostAncestors
                && !(_environment.IsDevelopment() || _environment.IsEnvironment("Testing")))
            {
                failures.Add(
                    $"PublicChannels:AllowLocalhostAncestors=true is only allowed in the Development and Testing environments, not in '{_environment.EnvironmentName}'.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }

        private static void AddRateLimitFailures(PublicRateLimitOptions? limits, List<string> failures)
        {
            if (limits is null)
            {
                failures.Add("PublicChannels:RateLimits must not be null.");
                return;
            }

            foreach (var (name, value) in limits.Values())
            {
                if (value < 1)
                {
                    failures.Add($"PublicChannels:RateLimits:{name} {value} must be 1 or more.");
                }
            }
        }
    }
}

/// <summary>
/// <c>PublicChannels:RateLimits</c> (M5a plan §3 E and decision C): how often the visitor API
/// (<c>/api/v1/public/*</c>) answers before it refuses with <c>429</c> and <c>Retry-After</c>; from
/// M5b (plan §3 F) also the LINE webhook's limits, named <c>Line*</c>. The
/// counters live in this process's memory (one API container per deployment; several instances would
/// each count on their own). Every value is a count of at least 1; the window is part of the name.
/// </summary>
public sealed class PublicRateLimitOptions
{
    /// <summary>Visitor sessions one client IP may start per minute (fixed window). Default 10.</summary>
    public int SessionsPerIpPerMinute { get; set; } = 10;

    /// <summary>Questions one visitor (a session's random <c>visitor_id</c>) may send per minute
    /// (sliding window). Default 6.</summary>
    public int RunsPerVisitorPerMinute { get; set; } = 6;

    /// <summary>Questions one visitor may send per hour (sliding window, the second layer). Default 60.</summary>
    public int RunsPerVisitorPerHour { get; set; } = 60;

    /// <summary>Questions one client IP may send per minute, whatever the visitor (sliding window).
    /// Default 20.</summary>
    public int RunsPerIpPerMinute { get; set; } = 20;

    /// <summary>Questions one assistant may be asked per minute, by all visitors (sliding window).
    /// Default 120.</summary>
    public int RunsPerAssistantPerMinute { get; set; } = 120;

    /// <summary>Replies one assistant may be generating at the same time, for all visitors. Default 10.</summary>
    public int MaxConcurrentRunsPerAssistant { get; set; } = 10;

    /// <summary>LINE webhook requests one assistant's URL accepts per minute (M5b #231; sliding window,
    /// checked before the body is read or the signature verified, so forged requests cost little).
    /// Beyond it: <c>429</c>. Default 1,000.</summary>
    public int LineWebhooksPerAssistantPerMinute { get; set; } = 1000;

    /// <summary>LINE webhook deliveries of one assistant the background processor handles at the
    /// same time (M5b #231, decision E); the others wait in the queue. Default 10.</summary>
    public int LineMaxConcurrentWebhooksPerAssistant { get; set; } = 10;

    /// <summary>Questions one LINE user may ask an assistant per minute in a one-to-one chat (M5b #232,
    /// decision E; sliding window). Beyond it the user gets 「問題太頻繁了，請稍後再試」 once per window
    /// and no answer. Default 6.</summary>
    public int LineQuestionsPerUserPerMinute { get; set; } = 6;

    /// <summary>The same per hour (sliding window, the second layer). Default 60.</summary>
    public int LineQuestionsPerUserPerHour { get; set; } = 60;

    /// <summary>Questions one LINE group or room may ask an assistant per minute, all members together
    /// (sliding window). Default 10.</summary>
    public int LineQuestionsPerGroupPerMinute { get; set; } = 10;

    /// <summary>LINE questions one assistant may be asked per minute, all chats together (sliding
    /// window). Default 120.</summary>
    public int LineQuestionsPerAssistantPerMinute { get; set; } = 120;

    /// <summary>LINE answers one assistant may be generating at the same time. Default 10.</summary>
    public int LineMaxConcurrentQuestionsPerAssistant { get; set; } = 10;

    internal IEnumerable<(string Name, int Value)> Values()
    {
        yield return (nameof(SessionsPerIpPerMinute), SessionsPerIpPerMinute);
        yield return (nameof(RunsPerVisitorPerMinute), RunsPerVisitorPerMinute);
        yield return (nameof(RunsPerVisitorPerHour), RunsPerVisitorPerHour);
        yield return (nameof(RunsPerIpPerMinute), RunsPerIpPerMinute);
        yield return (nameof(RunsPerAssistantPerMinute), RunsPerAssistantPerMinute);
        yield return (nameof(MaxConcurrentRunsPerAssistant), MaxConcurrentRunsPerAssistant);
        yield return (nameof(LineWebhooksPerAssistantPerMinute), LineWebhooksPerAssistantPerMinute);
        yield return (nameof(LineMaxConcurrentWebhooksPerAssistant), LineMaxConcurrentWebhooksPerAssistant);
        yield return (nameof(LineQuestionsPerUserPerMinute), LineQuestionsPerUserPerMinute);
        yield return (nameof(LineQuestionsPerUserPerHour), LineQuestionsPerUserPerHour);
        yield return (nameof(LineQuestionsPerGroupPerMinute), LineQuestionsPerGroupPerMinute);
        yield return (nameof(LineQuestionsPerAssistantPerMinute), LineQuestionsPerAssistantPerMinute);
        yield return (nameof(LineMaxConcurrentQuestionsPerAssistant), LineMaxConcurrentQuestionsPerAssistant);
    }
}

/// <summary><c>PublicChannels:Line</c> (M5b #232): how LINE answers are delivered.</summary>
public sealed class PublicLineOptions
{
    public const int MinReplyDeadlineSeconds = 1;
    public const int MaxReplyDeadlineSeconds = 60;

    /// <summary>
    /// Seconds from receiving a LINE event within which an answer is sent with the event's reply token
    /// (free; LINE keeps it valid for about a minute). An answer ready later — or whose reply LINE
    /// refuses as an expired or invalid token — is pushed in a one-to-one chat (counted against the LINE
    /// account's monthly quota, and on the channel's 「本月補送次數」) and dropped in a group or room
    /// (decision A). 1 to 60; default 50 (decision E).
    /// </summary>
    public int ReplyDeadlineSeconds { get; set; } = 50;

    public TimeSpan ReplyDeadline => TimeSpan.FromSeconds(ReplyDeadlineSeconds);
}
