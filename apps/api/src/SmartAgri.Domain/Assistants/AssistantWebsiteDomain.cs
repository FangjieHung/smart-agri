using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// One host name allowed to embed an assistant's website channel (「允許網域」; M5a plan §4, table
/// <c>AssistantWebsiteDomains</c>). Unique per assistant; at most
/// <see cref="AssistantWebsiteChannel.MaxAllowedDomains"/> per assistant, checked by the
/// Application rule (<c>WebsiteChannelRules</c>) before anything is written. The database removes
/// the row with its <see cref="AssistantWebsiteChannel"/> (composite foreign key, cascade).
/// </summary>
/// <remarks>
/// <see cref="Domain"/> is already validated and normalized (lower-case host name, no scheme,
/// path, port or wildcard) by the same rule as the frontend's <c>validateAllowedDomain()</c>; this
/// type only guards the basics.
/// </remarks>
public sealed class AssistantWebsiteDomain : IOrganizationScoped
{
    /// <summary>The longest host name DNS allows.</summary>
    public const int DomainMaxLength = 253;

    /// <summary>For EF Core materialization.</summary>
    private AssistantWebsiteDomain()
    {
    }

    public AssistantWebsiteDomain(AssistantWebsiteChannel channel, string domain, DateTimeOffset addedAt)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        if (domain.Length > DomainMaxLength || !string.Equals(domain, domain.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"A domain must be lower case and at most {DomainMaxLength} characters.", nameof(domain));
        }

        AssistantId = channel.AssistantId;
        OrganizationId = channel.OrganizationId;
        Domain = domain;
        AddedAt = addedAt;
    }

    public Guid AssistantId { get; private set; }

    /// <summary>Always the channel's (and the assistant's) organization.</summary>
    public Guid OrganizationId { get; private set; }

    public string Domain { get; private set; } = string.Empty;

    public DateTimeOffset AddedAt { get; private set; }

    /// <summary>
    /// When a visitor's chat window last reported being embedded on this domain (passive
    /// installation detection, M5a plan §3 H; written from Slice 4 on). For information only,
    /// never a security decision.
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; private set; }
}
