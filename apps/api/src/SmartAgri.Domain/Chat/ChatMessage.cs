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
        ChatThread thread, string text, ChatReplyKind replyKind, string? notice, IReadOnlyList<string> nextSteps, DateTimeOffset now)
    {
        if (replyKind is ChatReplyKind.FormRequest or ChatReplyKind.SubmissionReceipt or ChatReplyKind.DatabaseQuery
            or ChatReplyKind.CaseProposal)
        {
            throw new ArgumentException("Form, database query and case proposal replies have their own factories.", nameof(replyKind));
        }

        return new(thread, ChatMessageAuthor.Assistant, text, replyKind, notice, nextSteps, now);
    }

    /// <summary>The assistant asking the member to fill in <paramref name="databaseId"/>'s form
    /// (M4 #148). Only the database is stored: the form, purpose, recipient and readers are read
    /// again, and re-authorized, whenever the message is shown.</summary>
    public static ChatMessage FormRequest(ChatThread thread, string text, Guid databaseId, DateTimeOffset now)
    {
        RequireId(databaseId, nameof(databaseId));
        var message = new ChatMessage(thread, ChatMessageAuthor.Assistant, text, ChatReplyKind.FormRequest, null, [], now)
        {
            FormDatabaseId = databaseId,
        };
        return message;
    }

    /// <summary>The receipt of a consented submission made from this conversation (M4 #148).
    /// Only the submission id is stored — never the answers: the receipt is read from the
    /// submission (its own snapshot) for its submitter whenever the message is shown.</summary>
    public static ChatMessage SubmissionReceipt(
        ChatThread thread, string text, Guid databaseId, Guid submissionId, DateTimeOffset now)
    {
        RequireId(databaseId, nameof(databaseId));
        RequireId(submissionId, nameof(submissionId));
        return new ChatMessage(thread, ChatMessageAuthor.Assistant, text, ChatReplyKind.SubmissionReceipt, null, [], now)
        {
            FormDatabaseId = databaseId,
            SubmissionId = submissionId,
        };
    }

    /// <summary>The answer of a fixed statistics query (M4 #149): <paramref name="text"/> is composed
    /// by the server from the query's result, and <paramref name="databaseQuery"/> is that result's
    /// structured view as JSON (period, metric, source, figures) — a snapshot of the numbers as they
    /// were answered. It is shown again only while the asker may still query that database through
    /// this assistant, never handed off and never sent to a model as history.</summary>
    public static ChatMessage DatabaseQuery(ChatThread thread, string text, string databaseQuery, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseQuery);
        return new ChatMessage(thread, ChatMessageAuthor.Assistant, text, ChatReplyKind.DatabaseQuery, null, [], now)
        {
            DatabaseQueryJson = databaseQuery,
        };
    }

    /// <summary>The assistant proposing a case (M7-9, issue #254): <paramref name="proposal"/> is the
    /// snapshot (type id, draft title and description, <see cref="ChatCaseProposalStatus.Proposed"/>). The
    /// type is re-checked whenever the message is shown or confirmed.</summary>
    public static ChatMessage CaseProposal(ChatThread thread, string text, ChatCaseProposalSnapshot proposal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Status != ChatCaseProposalStatus.Proposed)
        {
            throw new ArgumentException("A new case proposal is always proposed.", nameof(proposal));
        }

        return new ChatMessage(thread, ChatMessageAuthor.Assistant, text, ChatReplyKind.CaseProposal, null, [], now)
        {
            CaseProposalJson = proposal.Serialize(),
        };
    }

    /// <summary>The case proposal's snapshot; <see langword="null"/> for any other message (or an unreadable one).</summary>
    public ChatCaseProposalSnapshot? ReadCaseProposal() =>
        ReplyKind == ChatReplyKind.CaseProposal && CaseProposalJson is { } json ? ChatCaseProposalSnapshot.Deserialize(json) : null;

    /// <summary>The asker confirmed (M7-9): the snapshot keeps exactly the title and description the asker
    /// confirmed (already validated and trimmed) and the case created from them. Only a
    /// <see cref="ChatCaseProposalStatus.Proposed"/> proposal may be confirmed; <see cref="CaseProposalJson"/>
    /// is a concurrency token, so two concurrent confirmations cannot both succeed.</summary>
    public void ConfirmCaseProposal(Guid caseId, string title, string description)
    {
        RequireId(caseId, nameof(caseId));
        var proposal = OpenCaseProposal();
        CaseProposalJson = (proposal with { Title = title, Description = description, Status = ChatCaseProposalStatus.Confirmed }).Serialize();
        ProposedCaseId = caseId;
    }

    /// <summary>「不用了」 (M7-9): only recorded; nothing is created.</summary>
    public void DismissCaseProposal()
    {
        var proposal = OpenCaseProposal();
        CaseProposalJson = (proposal with { Status = ChatCaseProposalStatus.Dismissed }).Serialize();
    }

    private ChatCaseProposalSnapshot OpenCaseProposal()
    {
        var proposal = ReadCaseProposal()
            ?? throw new InvalidOperationException("Only a case proposal message has a case proposal.");
        if (proposal.Status != ChatCaseProposalStatus.Proposed)
        {
            throw new InvalidOperationException("Only a proposed case proposal may be confirmed or dismissed.");
        }

        return proposal;
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", parameterName);
        }
    }

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

    /// <summary>The database of a <see cref="ChatReplyKind.FormRequest"/> or
    /// <see cref="ChatReplyKind.SubmissionReceipt"/>; <see langword="null"/> otherwise. No foreign
    /// key: a deleted database leaves the message, which then shows as no longer available.</summary>
    public Guid? FormDatabaseId { get; private set; }

    /// <summary>The submission of a <see cref="ChatReplyKind.SubmissionReceipt"/>;
    /// <see langword="null"/> otherwise. No foreign key, for the same reason.</summary>
    public Guid? SubmissionId { get; private set; }

    /// <summary>The structured view (JSON) of a <see cref="ChatReplyKind.DatabaseQuery"/> reply;
    /// <see langword="null"/> otherwise.</summary>
    public string? DatabaseQueryJson { get; private set; }

    /// <summary>The snapshot (JSON, <see cref="ChatCaseProposalSnapshot"/>) of a <see cref="ChatReplyKind.CaseProposal"/>
    /// reply (M7-9); <see langword="null"/> otherwise. A concurrency token: confirming and dismissing change it.</summary>
    public string? CaseProposalJson { get; private set; }

    /// <summary>The case a confirmed proposal created (M7-9); <see langword="null"/> otherwise. No foreign
    /// key: cases are kept forever, but the message goes with its thread.</summary>
    public Guid? ProposedCaseId { get; private set; }

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
