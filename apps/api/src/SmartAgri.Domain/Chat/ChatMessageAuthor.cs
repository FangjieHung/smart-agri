using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Chat;

/// <summary>Who wrote a <see cref="ChatMessage"/> — the frontend's <c>MessageAuthor</c>
/// (<c>conversation.model.ts</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatMessageAuthor>))]
public enum ChatMessageAuthor
{
    [JsonStringEnumMemberName("account")]
    Account,

    [JsonStringEnumMemberName("assistant")]
    Assistant,
}
