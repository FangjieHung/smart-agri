using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// Whether an assistant answers when used. A subset of the frontend's wider
/// <c>AssistantStatus</c> union (<c>apps/admin/src/app/core/domain/assistant.model.ts</c>),
/// which also has <c>draft</c> (an <see cref="AssistantDraft"/> row, never an
/// <see cref="Assistant"/>) and <c>published</c> (a mock-only value). An
/// <see cref="Assistant"/> row is always <see cref="Ready"/> or <see cref="Paused"/>.
/// </summary>
/// <remarks>
/// "Published" is deliberately not an assistant status (M5a plan §4): publishing is a
/// <em>channel</em>'s state (<see cref="AssistantWebsiteChannel.State"/>, and later LINE's), since
/// one assistant can be shared within the platform and embedded on a website at the same time.
/// <see cref="Paused"/> here pauses every channel at once: the website channel then serves
/// nobody (<see cref="ChannelServingState.Paused"/>).
/// </remarks>
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
