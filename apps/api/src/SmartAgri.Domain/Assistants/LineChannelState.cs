using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// The LINE channel's state as its owner set it (M5b plan §3 A; the same three states as
/// <see cref="WebsiteChannelState"/>): saved connection settings that are not enabled yet, enabled
/// (「已啟用」), or paused by the owner. Whether it actually answers LINE users right now is derived
/// on every read (<see cref="ChannelServingState"/>), never stored.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LineChannelState>))]
public enum LineChannelState
{
    /// <summary>Settings saved, not enabled (or unpublished again, or its connection settings were
    /// changed since it was enabled: see <see cref="AssistantLineChannel.TryApplySettings"/>).</summary>
    [JsonStringEnumMemberName("draft")]
    Draft,

    /// <summary>Enabled by the owner; answers LINE users while <see cref="ChannelServingState.Serving"/>.</summary>
    [JsonStringEnumMemberName("published")]
    Published,

    /// <summary>Enabled, then paused by the owner; settings and publication are kept.</summary>
    [JsonStringEnumMemberName("paused")]
    Paused,
}
