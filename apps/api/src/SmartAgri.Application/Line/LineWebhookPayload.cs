using System.Text.Json;

namespace SmartAgri.Application.Line;

/// <summary>
/// A LINE webhook delivery, <c>{ "destination": "U…", "events": [ … ] }</c>, read leniently (M5b
/// plan §3 C and D): LINE adds event types and properties without notice, so anything unknown is
/// kept as it came (an event's <see cref="LineWebhookEvent.Type"/>) or ignored, never an error.
/// Console's "Verify" request is a delivery with no events.
/// </summary>
/// <param name="Destination">The bot's own user id (<see langword="null"/> when absent).</param>
/// <param name="Events">The events, in the order LINE sent them.</param>
public sealed record LineWebhookPayload(string? Destination, IReadOnlyList<LineWebhookEvent> Events)
{
    /// <summary>Parses a delivery whose signature has already been verified; <see langword="null"/>
    /// when <paramref name="body"/> is not a JSON object (an <c>events</c> that is missing or not an
    /// array counts as no events; an element that is not an object is skipped).</summary>
    public static LineWebhookPayload? TryParse(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 64 });
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var events = new List<LineWebhookEvent>();
            if (root.TryGetProperty("events", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        events.Add(LineWebhookEvent.From(element));
                    }
                }
            }

            return new LineWebhookPayload(String(root, "destination"), events);
        }
    }

    internal static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Where an event happened (<c>source</c>).</summary>
/// <param name="Type"><c>user</c> (a one-to-one chat), <c>group</c> or <c>room</c> (a multi-person
/// chat); anything else as sent.</param>
/// <param name="UserId">The user; in a group or room only on some events, and only for LINE's
/// iOS/Android apps.</param>
public sealed record LineEventSource(string? Type, string? UserId, string? GroupId, string? RoomId)
{
    /// <summary>A one-to-one chat with a user.</summary>
    public bool IsOneToOne => Type == "user";

    /// <summary>Only the type: the ids identify LINE users and chats and are never logged.</summary>
    public override string ToString() => $"{nameof(LineEventSource)} {{ Type = {Type} }}";

    /// <summary>The chat this event belongs to: the group, the room, or (one-to-one) the user;
    /// <see langword="null"/> if the source names none.</summary>
    public string? ChatId => Type switch
    {
        "group" => GroupId,
        "room" => RoomId,
        "user" => UserId,
        _ => null,
    };
}

/// <summary>A part of a message's text: <paramref name="Length"/> UTF-16 code units from
/// <paramref name="Index"/>.</summary>
public readonly record struct LineTextSpan(int Index, int Length);

/// <summary>A <c>message</c> event's <c>message</c> (the fields used here).</summary>
/// <param name="Type"><c>text</c>, <c>image</c>, <c>video</c>, <c>audio</c>, <c>file</c>,
/// <c>location</c>, <c>sticker</c>, or anything newer.</param>
/// <param name="Text">A text message's text; <see langword="null"/> for every other type.</param>
/// <param name="MentionsSelf">A text message mentions the bot itself (<c>mention.mentionees[]</c>
/// has an <c>isSelf: true</c>).</param>
public sealed record LineEventMessage(string? Id, string? Type, string? Text, bool MentionsSelf)
{
    public bool IsText => Type == "text";

    /// <summary>Where <see cref="Text"/> mentions the bot (each <c>isSelf: true</c> mentionee's
    /// <c>index</c> and <c>length</c>, in UTF-16 code units), in text order; empty when it does not or
    /// LINE gave no usable position.</summary>
    public IReadOnlyList<LineTextSpan> SelfMentions { get; init; } = [];

    /// <summary>
    /// <see cref="Text"/> without the bot's own mentions (<see cref="SelfMentions"/>, e.g. 「@安心客服」),
    /// white space around the removed parts collapsed, trimmed: the question a group member asked
    /// (M5b plan §3 D). Other members' mentions stay. <see langword="null"/> for a non-text message.
    /// </summary>
    public string? QuestionText
    {
        get
        {
            if (Text is null)
            {
                return null;
            }

            var text = Text;
            foreach (var span in SelfMentions.OrderByDescending(span => span.Index))
            {
                if (span.Index < 0 || span.Length <= 0 || span.Index + span.Length > text.Length)
                {
                    continue;
                }

                var before = text[..span.Index].TrimEnd();
                var after = text[(span.Index + span.Length)..].TrimStart();
                text = before.Length == 0 || after.Length == 0 ? before + after : before + " " + after;
            }

            return text.Trim();
        }
    }

    /// <summary>Only the type: the text is what a LINE user wrote and is never logged.</summary>
    public override string ToString() => $"{nameof(LineEventMessage)} {{ Type = {Type} }}";
}

/// <summary>
/// One webhook event (M5b plan §3 D's table). Never logged as a whole: it carries LINE user ids, a
/// reply token and what the user wrote.
/// </summary>
/// <param name="Type"><c>message</c>, <c>follow</c>, <c>unfollow</c>, <c>join</c>, <c>leave</c>,
/// <c>unsend</c>, … or anything newer (ignored).</param>
/// <param name="WebhookEventId">LINE's id of the event (a ULID), the same when it is redelivered;
/// <see langword="null"/> when absent.</param>
/// <param name="IsRedelivery"><c>deliveryContext.isRedelivery</c>.</param>
/// <param name="Mode"><c>active</c>, or <c>standby</c> (another channel answers: send nothing).</param>
/// <param name="ReplyToken">Usable once, within about a minute; <see langword="null"/> on events
/// that cannot be replied to.</param>
/// <param name="UnsentMessageId">An <c>unsend</c> event's <c>unsend.messageId</c>.</param>
public sealed record LineWebhookEvent(
    string? Type,
    string? WebhookEventId,
    bool IsRedelivery,
    long? Timestamp,
    string? Mode,
    string? ReplyToken,
    LineEventSource Source,
    LineEventMessage? Message,
    string? UnsentMessageId)
{
    /// <summary>The bot is in standby mode for this chat: it must not send anything.</summary>
    public bool IsStandby => Mode == "standby";

    /// <summary>The event's chat, or <see langword="null"/> if its source names none.</summary>
    public string? ChatId => Source.ChatId;

    /// <summary>No ids, reply token or text: those are never logged.</summary>
    public override string ToString() =>
        $"{nameof(LineWebhookEvent)} {{ Type = {Type}, Source = {Source.Type}, Message = {Message?.Type}, Mode = {Mode}, IsRedelivery = {IsRedelivery} }}";

    internal static LineWebhookEvent From(JsonElement element)
    {
        var source = element.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.Object
            ? new LineEventSource(
                LineWebhookPayload.String(sourceElement, "type"),
                LineWebhookPayload.String(sourceElement, "userId"),
                LineWebhookPayload.String(sourceElement, "groupId"),
                LineWebhookPayload.String(sourceElement, "roomId"))
            : new LineEventSource(null, null, null, null);

        var message = element.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.Object
            ? new LineEventMessage(
                LineWebhookPayload.String(messageElement, "id"),
                LineWebhookPayload.String(messageElement, "type"),
                LineWebhookPayload.String(messageElement, "type") == "text" ? LineWebhookPayload.String(messageElement, "text") : null,
                MentionsSelf(messageElement))
            {
                SelfMentions = SelfMentionSpans(messageElement),
            }
            : null;

        var isRedelivery = element.TryGetProperty("deliveryContext", out var delivery)
            && delivery.ValueKind == JsonValueKind.Object
            && delivery.TryGetProperty("isRedelivery", out var redelivery)
            && redelivery.ValueKind == JsonValueKind.True;

        var timestamp = element.TryGetProperty("timestamp", out var time) && time.ValueKind == JsonValueKind.Number
            && time.TryGetInt64(out var milliseconds)
                ? milliseconds
                : (long?)null;

        var unsent = element.TryGetProperty("unsend", out var unsend) && unsend.ValueKind == JsonValueKind.Object
            ? LineWebhookPayload.String(unsend, "messageId")
            : null;

        return new LineWebhookEvent(
            LineWebhookPayload.String(element, "type"),
            LineWebhookPayload.String(element, "webhookEventId"),
            isRedelivery,
            timestamp,
            LineWebhookPayload.String(element, "mode"),
            LineWebhookPayload.String(element, "replyToken"),
            source,
            message,
            unsent);
    }

    private static bool MentionsSelf(JsonElement message) => SelfMentionees(message).Any();

    private static IReadOnlyList<LineTextSpan> SelfMentionSpans(JsonElement message)
    {
        var spans = new List<LineTextSpan>();
        foreach (var mentionee in SelfMentionees(message))
        {
            if (mentionee.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var start)
                && mentionee.TryGetProperty("length", out var length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out var count)
                && start >= 0 && count > 0)
            {
                spans.Add(new LineTextSpan(start, count));
            }
        }

        return [.. spans.OrderBy(span => span.Index)];
    }

    private static IEnumerable<JsonElement> SelfMentionees(JsonElement message)
    {
        if (!message.TryGetProperty("mention", out var mention)
            || mention.ValueKind != JsonValueKind.Object
            || !mention.TryGetProperty("mentionees", out var mentionees)
            || mentionees.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var mentionee in mentionees.EnumerateArray())
        {
            if (mentionee.ValueKind == JsonValueKind.Object
                && mentionee.TryGetProperty("isSelf", out var isSelf)
                && isSelf.ValueKind == JsonValueKind.True)
            {
                yield return mentionee;
            }
        }
    }

    /// <summary>Event types handled by M5b (plan §3 D); every other type is ignored.</summary>
    public static class Types
    {
        public const string Message = "message";
        public const string Follow = "follow";
        public const string Unfollow = "unfollow";
        public const string Join = "join";
        public const string Leave = "leave";
        public const string Unsend = "unsend";
    }
}
