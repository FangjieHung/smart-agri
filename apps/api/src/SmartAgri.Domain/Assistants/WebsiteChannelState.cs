using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// The website channel's state as its owner set it (M5a plan §3 C): saved settings that are not
/// published yet, published, or paused by the owner. Whether it actually answers visitors right
/// now is derived on every read (<see cref="WebsiteServingState"/>), never stored.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebsiteChannelState>))]
public enum WebsiteChannelState
{
    /// <summary>Settings saved, not published (or unpublished again).</summary>
    [JsonStringEnumMemberName("draft")]
    Draft,

    /// <summary>Published by the owner; answers visitors while <see cref="WebsiteServingState.Serving"/>.</summary>
    [JsonStringEnumMemberName("published")]
    Published,

    /// <summary>Published, then paused by the owner; settings and publication are kept.</summary>
    [JsonStringEnumMemberName("paused")]
    Paused,
}
