using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>The launcher's colour, one of the frontend's fixed choices
/// (<c>publishing.model.ts</c>'s <c>WebsiteBrandColor</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebsiteBrandColor>))]
public enum WebsiteBrandColor
{
    [JsonStringEnumMemberName("forest")]
    Forest,

    [JsonStringEnumMemberName("ocean")]
    Ocean,

    [JsonStringEnumMemberName("amber")]
    Amber,

    [JsonStringEnumMemberName("plum")]
    Plum,
}
