using SmartAgri.Application.Answers;

namespace SmartAgri.Application.Line;

/// <summary>
/// <see cref="ILineConversationHistory"/> in this process's memory: a dictionary of conversations
/// plus a least-recently-used list, under one lock. An idle conversation expires lazily (when it is
/// next touched) and is pruned whenever a new conversation would exceed the capacity, so memory
/// stays bounded by <see cref="LineConversationHistoryLimits.MaxConversations"/> ×
/// <see cref="LineConversationHistoryLimits.MaxMessages"/> messages. Registered as a singleton.
/// </summary>
public sealed class InMemoryLineConversationHistory : ILineConversationHistory
{
    private readonly TimeProvider _clock;
    private readonly LineConversationHistoryLimits _limits;
    private readonly Lock _lock = new();
    private readonly Dictionary<LineChatKey, LinkedListNode<Conversation>> _conversations = [];

    /// <summary>Most recently used first.</summary>
    private readonly LinkedList<Conversation> _recency = new();

    public InMemoryLineConversationHistory(TimeProvider clock, LineConversationHistoryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxMessages, 1, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxConversations, 1, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limits.IdleTimeout, TimeSpan.Zero, nameof(limits));
        _clock = clock;
        _limits = limits;
    }

    /// <summary>How many conversations are held right now (expired ones not yet pruned included).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _conversations.Count;
            }
        }
    }

    public IReadOnlyList<LineHistoryEntry> Get(LineChatKey chat)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            return Live(chat, now) is { } node ? Touch(node, now).Messages.ToArray() : [];
        }
    }

    public void Append(LineChatKey chat, string? lineMessageId, ConversationTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            var node = Live(chat, now);
            if (node is null)
            {
                MakeRoom(now);
                node = _recency.AddFirst(new Conversation(chat, now));
                _conversations[chat] = node;
            }

            var conversation = Touch(node, now);
            conversation.Messages.Enqueue(new LineHistoryEntry(lineMessageId, turn));
            while (conversation.Messages.Count > _limits.MaxMessages)
            {
                conversation.Messages.Dequeue();
            }
        }
    }

    public void Clear(LineChatKey chat)
    {
        lock (_lock)
        {
            if (_conversations.Remove(chat, out var node))
            {
                _recency.Remove(node);
            }
        }
    }

    public int RemoveMessage(LineChatKey chat, string lineMessageId)
    {
        ArgumentNullException.ThrowIfNull(lineMessageId);
        lock (_lock)
        {
            if (Live(chat, _clock.GetUtcNow()) is not { } node)
            {
                return 0;
            }

            var messages = node.Value.Messages;
            var kept = messages.Where(entry => !string.Equals(entry.LineMessageId, lineMessageId, StringComparison.Ordinal)).ToList();
            var removed = messages.Count - kept.Count;
            if (removed > 0)
            {
                messages.Clear();
                foreach (var entry in kept)
                {
                    messages.Enqueue(entry);
                }
            }

            return removed;
        }
    }

    /// <summary>The conversation, or <see langword="null"/> (removing it) when it has expired.</summary>
    private LinkedListNode<Conversation>? Live(LineChatKey chat, DateTimeOffset now)
    {
        if (!_conversations.TryGetValue(chat, out var node))
        {
            return null;
        }

        if (now - node.Value.LastActivity < _limits.IdleTimeout)
        {
            return node;
        }

        _conversations.Remove(chat);
        _recency.Remove(node);
        return null;
    }

    private Conversation Touch(LinkedListNode<Conversation> node, DateTimeOffset now)
    {
        node.Value.LastActivity = now;
        if (node != _recency.First)
        {
            _recency.Remove(node);
            _recency.AddFirst(node);
        }

        return node.Value;
    }

    /// <summary>Before a new conversation: drops the expired ones (always the least recently used
    /// end of the list), then, at capacity, the least recently used.</summary>
    private void MakeRoom(DateTimeOffset now)
    {
        while (_recency.Last is { } oldest
               && (now - oldest.Value.LastActivity >= _limits.IdleTimeout || _conversations.Count >= _limits.MaxConversations))
        {
            _conversations.Remove(oldest.Value.Key);
            _recency.RemoveLast();
        }
    }

    private sealed class Conversation(LineChatKey key, DateTimeOffset lastActivity)
    {
        public LineChatKey Key { get; } = key;

        public DateTimeOffset LastActivity { get; set; } = lastActivity;

        public Queue<LineHistoryEntry> Messages { get; } = new();
    }
}
