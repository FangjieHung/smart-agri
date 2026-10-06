using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// Whether the website channel answers visitors right now (M5a plan §3 C's table, checked in
/// this order): derived on every read by <c>WebsiteChannelServing.Evaluate</c>, never stored.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebsiteServingState>))]
public enum WebsiteServingState
{
    /// <summary>The channel is not <see cref="WebsiteChannelState.Published"/> (nor paused), or it
    /// has no allowed domain.</summary>
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
