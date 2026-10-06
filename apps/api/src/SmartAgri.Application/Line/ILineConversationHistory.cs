using SmartAgri.Application.Answers;

namespace SmartAgri.Application.Line;

/// <summary>
/// One LINE conversation: an assistant and a chat — a user's one-to-one chat, a group or a room
/// (<see cref="LineEventSource.ChatId"/>). A group or room shares one conversation (M5b plan §3 E).
/// </summary>
public readonly record struct LineChatKey(Guid AssistantId, string ChatId)
{
    /// <summary>Only the assistant: a chat id identifies LINE users and is never logged.</summary>
    public override string ToString() => $"{nameof(LineChatKey)} {{ AssistantId = {AssistantId} }}";
}

/// <summary>One remembered message: a turn of the conversation and the LINE message id it belongs
/// to (the question's id, for the question and for the answer to it), if any.</summary>
public sealed record LineHistoryEntry(string? LineMessageId, ConversationTurn Turn);

/// <summary>
/// The earlier turns of LINE conversations (「前文」, M5b plan §3 E, decision 1): kept in this
/// process's memory only — never in the database — per <see cref="LineChatKey"/>, at most the newest
/// <see cref="LineConversationHistoryLimits.MaxMessages"/> messages, forgotten after
/// <see cref="LineConversationHistoryLimits.IdleTimeout"/> without activity, and at most
/// <see cref="LineConversationHistoryLimits.MaxConversations"/> conversations (the least recently
/// used goes first). Single process, like the rate limits: a restart forgets everything.
/// </summary>
/// <remarks>
/// The webhook processor (#231) forgets a conversation on <c>unfollow</c>/<c>leave</c> and a message
/// on <c>unsend</c>; LINE questions (#232) read it and append each question and answer. Thread-safe.
/// </remarks>
public interface ILineConversationHistory
{
    /// <summary>The conversation's remembered messages, oldest first (empty when there are none or
    /// they expired). Reading counts as activity.</summary>
    IReadOnlyList<LineHistoryEntry> Get(LineChatKey chat);

    /// <summary>Remembers <paramref name="turn"/> as the newest message (dropping the oldest beyond
    /// the limit); <paramref name="lineMessageId"/> is the LINE message it belongs to, so an
    /// <c>unsend</c> of that message can remove it.</summary>
    void Append(LineChatKey chat, string? lineMessageId, ConversationTurn turn);

    /// <summary>Forgets the whole conversation (<c>unfollow</c>, <c>leave</c>).</summary>
    void Clear(LineChatKey chat);

    /// <summary>Forgets every message belonging to LINE message <paramref name="lineMessageId"/>
    /// (<c>unsend</c>): the question and the answer to it. Returns how many were removed.</summary>
    int RemoveMessage(LineChatKey chat, string lineMessageId);
}

/// <summary>Decision E's limits of <see cref="ILineConversationHistory"/>.</summary>
/// <param name="MaxMessages">Messages kept per conversation (default 20, as for a website visitor).</param>
/// <param name="IdleTimeout">A conversation without activity this long is forgotten (default 30 minutes).</param>
/// <param name="MaxConversations">Conversations kept at once (default 10,000).</param>
public sealed record LineConversationHistoryLimits(int MaxMessages, TimeSpan IdleTimeout, int MaxConversations)
{
    public static LineConversationHistoryLimits Default { get; } = new(20, TimeSpan.FromMinutes(30), 10_000);
}
