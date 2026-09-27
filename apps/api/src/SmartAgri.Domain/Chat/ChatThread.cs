using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Chat;

/// <summary>
/// One private conversation between one account and one assistant (table <c>ChatThreads</c>,
/// M3 plan §4). Only ever created for an assistant with <see cref="Assistant.KeepConversations"/>
/// true (#76 acceptance: <c>KeepConversations = false</c> refuses <c>POST conversations</c> with
/// <c>422</c> before this constructor is ever called). Isolated by
/// <c>(AccountId, AssistantId)</c>: the owner of the assistant cannot see another account's
/// threads, not even that they exist (mapping §5.4) — <c>SmartAgri.Application.Chat.ChatThreadAccess</c>
/// is the only place that is enforced.
/// </summary>
public sealed class ChatThread : IOrganizationScoped
{
    public const int TitleMaxLength = 60;

    /// <summary>For EF Core materialization.</summary>
    private ChatThread()
    {
    }

    public ChatThread(Assistant assistant, Guid accountId, string title, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        RequireId(accountId, nameof(accountId));
        Id = Guid.CreateVersion7();
        OrganizationId = assistant.OrganizationId;
        AssistantId = assistant.Id;
        AccountId = accountId;
        CreatedAt = now;
        LastActivityAt = now;
        MessageCount = 0;
        Rename(title, now);
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid AssistantId { get; private set; }

    /// <summary>Whose thread this is; never the assistant's owner unless they are also the
    /// one chatting.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Defaults to the first question, truncated; the owner may rename it
    /// (<c>PATCH .../conversations/{threadId}</c>). Validated by the caller
    /// (<c>SmartAgri.Application.Chat.ChatThreadTitleRules</c>): 1-<see cref="TitleMaxLength"/>
    /// trimmed characters.</summary>
    public string Title { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The newest message's <c>CreatedAt</c> (or <see cref="CreatedAt"/> if none
    /// yet); what the thread list sorts by (newest first).</summary>
    public DateTimeOffset LastActivityAt { get; private set; }

    /// <summary>Denormalized count of this thread's messages, for
    /// <c>ChatThreadSummaryView.messageCount</c> without a join.</summary>
    public int MessageCount { get; private set; }

    /// <summary>Sets <see cref="Title"/>; the caller (not this method) validates blankness and
    /// length so it can return the exact <c>422</c> message the frontend expects.</summary>
    public void Rename(string title, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(title);
        var trimmed = title.Trim();
        if (trimmed.Length is 0 or > TitleMaxLength)
        {
            throw new ArgumentException($"A thread title must be 1-{TitleMaxLength} characters.", nameof(title));
        }

        Title = trimmed;
    }

    /// <summary>Bumps <see cref="LastActivityAt"/> and <see cref="MessageCount"/> when a
    /// message is added (#77 calls this once per saved turn; #76's tests call it directly to
    /// seed a thread).</summary>
    public void RegisterMessage(DateTimeOffset now)
    {
        MessageCount += 1;
        LastActivityAt = now;
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", parameterName);
        }
    }
}
