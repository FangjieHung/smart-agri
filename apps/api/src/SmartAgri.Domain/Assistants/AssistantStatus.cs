using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// Whether an assistant answers when used. A subset of the frontend's wider
/// <c>AssistantStatus</c> union (<c>apps/admin/src/app/core/domain/assistant.model.ts</c>),
/// which also has <c>draft</c> (an <see cref="AssistantDraft"/> row, never an
/// <see cref="Assistant"/>) and <c>published</c> (M5, external publishing). An
/// <see cref="Assistant"/> row is always <see cref="Ready"/> or <see cref="Paused"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantStatus>))]
public enum AssistantStatus
{
    /// <summary>The owner, and anyone it is shared with, may use it (準備好).</summary>
    [JsonStringEnumMemberName("ready")]
    Ready,

    /// <summary>Only the owner may use it while paused (暫停使用); plan §5 Slice 3.</summary>
    [JsonStringEnumMemberName("paused")]
    Paused,
}
