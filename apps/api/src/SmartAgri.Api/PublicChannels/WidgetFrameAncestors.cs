using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure.Assistants;

namespace SmartAgri.Api.PublicChannels;

/// <summary>
/// Which pages may embed an assistant's chat window: the allowed domains of a published (or paused)
/// website channel as <c>frame-ancestors</c> sources, derived from the database on every request
/// (M5a plan §3 B).
/// </summary>
internal static class WidgetFrameAncestors
{
    /// <summary>The CSP source for pages on any local port, added by
    /// <c>PublicChannels:AllowLocalhostAncestors</c>.</summary>
    public const string Localhost = "http://localhost:*";

    /// <summary>
    /// The <c>frame-ancestors</c> sources for <paramref name="channel"/>, or <see langword="null"/> when
    /// nobody may embed it: no channel, a draft, or no usable domain. A paused channel still lists its
    /// domains (the widget then shows 「目前暫停服務」); so do the suspended states, which the visitor API
    /// answers. Each domain becomes <c>https://&lt;domain&gt;</c>; a stored value that is not a valid
    /// domain (the settings endpoint never writes one) is skipped, so nothing can reach the header
    /// unchecked.
    /// </summary>
    public static string? For(PublicWebsiteChannel? channel, bool allowLocalhost)
    {
        if (channel is null || channel.State is not (WebsiteChannelState.Published or WebsiteChannelState.Paused))
        {
            return null;
        }

        var sources = channel.Domains
            .Where(domain => domain == WebsiteChannelRules.NormalizeDomain(domain)
                && WebsiteChannelRules.ValidateDomain(domain, []) is null)
            .Select(domain => $"https://{domain}")
            .ToList();
        if (sources.Count == 0)
        {
            return null;
        }

        if (allowLocalhost)
        {
            sources.Add(Localhost);
        }

        return string.Join(' ', sources);
    }
}
