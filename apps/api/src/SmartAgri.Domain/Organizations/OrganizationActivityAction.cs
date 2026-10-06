using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Organizations;

/// <summary>
/// What an <see cref="OrganizationActivity"/> row records. Stored as the wire name (not the number),
/// so members can be reordered safely. Later slices add theirs here (M6-4: retention; M7: case
/// teams and case types).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrganizationActivityAction>))]
public enum OrganizationActivityAction
{
    /// <summary>A manager chose another chat model (M6-2). Detail: <c>from</c> and <c>to</c>, each
    /// <c>{ id, displayName }</c> (<see cref="OrganizationActivity.ChatModelChanged"/>).</summary>
    [JsonStringEnumMemberName("chat-model-changed")]
    ChatModelChanged,
}
