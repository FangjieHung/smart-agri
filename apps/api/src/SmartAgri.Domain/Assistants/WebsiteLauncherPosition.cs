using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>Where the launcher sits on the customer's page (<c>publishing.model.ts</c>'s
/// <c>WebsiteLauncherPosition</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebsiteLauncherPosition>))]
public enum WebsiteLauncherPosition
{
    [JsonStringEnumMemberName("bottom-right")]
    BottomRight,

    [JsonStringEnumMemberName("bottom-left")]
    BottomLeft,
}
