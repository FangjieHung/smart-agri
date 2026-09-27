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
        DateTimeOffset now)
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
    }

    /// <summary>The account's own question.</summary>
    public static ChatMessage Account(ChatThread thread, string text, DateTimeOffset now) =>
        new(thread, ChatMessageAuthor.Account, text, null, null, [], now);

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

    /// <summary><see langword="null"/> for an <see cref="ChatMessageAuthor.Account"/> turn.</summary>
    public ChatReplyKind? ReplyKind { get; private set; }

    /// <summary>Set only for <see cref="ChatReplyKind.GeneralKnowledge"/>.</summary>
    public string? Notice { get; private set; }

    /// <summary>Set only for <see cref="ChatReplyKind.NoResult"/>; empty otherwise.</summary>
    public IReadOnlyList<string> NextSteps { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }
}
