using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// Whether a public channel — the website channel or the LINE channel — answers right now (M5a plan
/// §3 C's table, checked in this order; generalized for both channels by M5b plan §4): derived on
/// every read by <c>ChannelServing.Evaluate</c>, never stored. Named <c>WebsiteServingState</c>
/// until M5b #229; the wire names did not change.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChannelServingState>))]
public enum ChannelServingState
{
    /// <summary>The channel is not published (nor paused), or not usable: a website channel without
    /// an allowed domain, a LINE channel whose connection test has not passed all its checks.</summary>
    [JsonStringEnumMemberName("not-published")]
    NotPublished,

    /// <summary>The channel or the assistant itself is paused.</summary>
    [JsonStringEnumMemberName("paused")]
    Paused,

    /// <summary>The acceptance status is failed or not-accepted, or outdated while the latest
    /// completed run had a failed case.</summary>
    [JsonStringEnumMemberName("suspended-acceptance")]
    SuspendedAcceptance,

    /// <summary>A connected knowledge base is not owned by the assistant's owner (decision B).</summary>
    [JsonStringEnumMemberName("suspended-knowledge")]
    SuspendedKnowledge,

    /// <summary>The organization's monthly token limit is reached (M5a plan §3 F).</summary>
    [JsonStringEnumMemberName("suspended-quota")]
    SuspendedQuota,

    /// <summary>Answers visitors.</summary>
    [JsonStringEnumMemberName("serving")]
    Serving,
}
