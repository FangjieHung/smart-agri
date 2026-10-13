using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Notifications;

/// <summary>Where the visitor of a <see cref="VisitorHandoff"/> was talking to the assistant (#355).
/// The column stores the wire name, so adding a channel needs no data migration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VisitorHandoffChannel>))]
public enum VisitorHandoffChannel
{
    /// <summary>The assistant's website channel (the embedded chat window, M5a).</summary>
    [JsonStringEnumMemberName("website")]
    Website,

    /// <summary>The assistant's LINE channel (one-to-one chat or a group, M5b).</summary>
    [JsonStringEnumMemberName("line")]
    Line,
}
