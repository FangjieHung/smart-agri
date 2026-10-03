using System.Text.Json;
using SmartAgri.Application.Databases;

namespace SmartAgri.Api.Chat;

/// <summary>How a saved query answer's structured view is stored in <c>ChatMessages.DatabaseQuery</c>
/// (<c>jsonb</c>): the same camelCase shape the API sends.</summary>
internal static class ChatDatabaseQueryJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(ChatDatabaseQueryView view) => JsonSerializer.Serialize(view, Options);

    /// <summary>The stored view, or <see langword="null"/> when it cannot be read (then shown as not available).</summary>
    public static ChatDatabaseQueryView? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ChatDatabaseQueryView>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
