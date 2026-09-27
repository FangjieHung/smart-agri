using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// The voice an assistant answers in. The serialized names must equal the frontend's
/// <c>AssistantTone</c> union in
/// <c>apps/admin/src/app/core/domain/assistant-draft.model.ts</c> exactly;
/// <c>SmartAgri.Domain.Tests</c> compares them against that file.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantTone>))]
public enum AssistantTone
{
    [JsonStringEnumMemberName("friendly")]
    Friendly,

    [JsonStringEnumMemberName("professional")]
    Professional,

    [JsonStringEnumMemberName("concise")]
    Concise,
}
