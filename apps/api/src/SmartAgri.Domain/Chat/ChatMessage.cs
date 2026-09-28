using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Chat;

/// <summary>
/// One turn of a <see cref="ChatThread"/> (table <c>ChatMessages</c>, M3 plan §4). An account
/// turn (<see cref="ChatMessageAuthor.Account"/>) is just <see cref="Text"/>; an assistant turn
/// is the <b>validated, final</b> reply — a rejected model output is never saved
/// (<c>GroundedAnswerService</c> never runs it past validation before #77 calls
/// <see cref="Assistant"/>).
/// </summary>
public sealed class ChatMessage : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private ChatMessage()
    {
    }

    private ChatMessage(
        ChatThread thread,
        ChatMessageAuthor author,
        string text,
        ChatReplyKind? replyKind,
        string? notice,
        IReadOnlyList<string> nextSteps,
        DateTimeOffset now,
        string? clientMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Id = Guid.CreateVersion7();
        OrganizationId = thread.OrganizationId;
        ThreadId = thread.Id;
        Author = author;
        Text = text;
        ReplyKind = replyKind;
        Notice = notice;
        NextSteps = [.. nextSteps];
        CreatedAt = now;
        ClientMessageId = clientMessageId;
        thread.RegisterMessage(now);
        Sequence = thread.MessageCount;
    }

    /// <summary>The account's own question. <paramref name="clientMessageId"/> is the id the
    /// client gave the <c>RunAgentInput</c> user message that produced it (ticket #105), already
    /// validated (<c>ChatRunRules.ValidateClientMessageId</c>) — <see langword="null"/> when the
    /// client sent none or it was not a valid id. It lets a retried run after a mid-stream
    /// failure (same id, same thread, sent again by the client) recognize the question it
    /// already saved instead of saving it again.</summary>
    public static ChatMessage Account(ChatThread thread, string text, DateTimeOffset now, string? clientMessageId = null) =>
        new(thread, ChatMessageAuthor.Account, text, null, null, [], now, clientMessageId);

    /// <summary>The assistant's validated reply. <paramref name="notice"/> only makes sense for
    /// <see cref="ChatReplyKind.GeneralKnowledge"/>; <paramref name="nextSteps"/> only for
    /// <see cref="ChatReplyKind.NoResult"/> — the caller (#77) is trusted to pass the right
    /// combination, mirroring <c>GroundedReply</c>'s factories.</summary>
    public static ChatMessage Assistant(
        ChatThread thread, string text, ChatReplyKind replyKind, string? notice, IReadOnlyList<string> nextSteps, DateTimeOffset now) =>
        new(thread, ChatMessageAuthor.Assistant, text, replyKind, notice, nextSteps, now);

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ThreadId { get; private set; }

    public ChatMessageAuthor Author { get; private set; }

    public string Text { get; private set; } = string.Empty;

    /// <summary>Only ever set for an <see cref="ChatMessageAuthor.Account"/> turn (ticket #105):
    /// the id the client gave this question's user message, when it sent a valid one. Internal
    /// bookkeeping only — never returned by any API view — used to recognize a retried run's
    /// question as the one already saved instead of saving it a second time.</summary>
    public string? ClientMessageId { get; private set; }

    /// <summary><see langword="null"/> for an <see cref="ChatMessageAuthor.Account"/> turn.</summary>
    public ChatReplyKind? ReplyKind { get; private set; }

    /// <summary>Set only for <see cref="ChatReplyKind.GeneralKnowledge"/>.</summary>
    public string? Notice { get; private set; }

    /// <summary>Set only for <see cref="ChatReplyKind.NoResult"/>; empty otherwise.</summary>
    public IReadOnlyList<string> NextSteps { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>1-based position within the thread, assigned when the message is created
    /// (<see cref="ChatThread.RegisterMessage"/>). Messages are ordered by this, not by
    /// <see cref="CreatedAt"/>: two turns saved in the same instant, or ids generated in the
    /// same millisecond, would otherwise come back in an arbitrary order.</summary>
    public int Sequence { get; private set; }
}
