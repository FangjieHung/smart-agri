using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Cases;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Cases;
using SmartAgri.Application.Observability;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Observability;
using SmartAgri.Infrastructure;
using ChatMessage = SmartAgri.Domain.Chat.ChatMessage;

namespace SmartAgri.Api.Chat;

/// <summary>
/// A <c>case-proposal</c> reply's proposal (M7-9, issue #254), as <c>GET chat</c> and the stream show
/// it: the snapshot (type id, title, description, status, the case once confirmed) plus the type as it
/// is <b>now</b> — its name, default case group and handling time — and whether it can still be
/// confirmed. Never any other conversation text.
/// </summary>
/// <param name="TypeName">The type's current name.</param>
/// <param name="Available">Whether confirming may succeed now: still proposed, the type still active and
/// still one the assistant may propose. A deactivated or removed type shows 「無法建立」 and confirming is <c>422</c>.</param>
/// <param name="Group">The type's default case group (where a confirmed case goes).</param>
/// <param name="DueHours">The type's handling time in hours (the case's due time is counted from confirmation).</param>
/// <param name="CaseId">The case a confirmed proposal created; <see langword="null"/> otherwise.</param>
public sealed record ChatCaseProposalView(
    Guid TypeId,
    string TypeName,
    string Title,
    string Description,
    ChatCaseProposalStatus Status,
    bool Available,
    CaseGroupRefView Group,
    int DueHours,
    Guid? CaseId);

/// <summary>One case type an assistant may propose right now: the offer the rules and the model see, and
/// what the card shows next to it.</summary>
internal sealed record ProposableCaseType(CaseProposalOffer Offer, CaseGroupRefView Group, int DueHours);

/// <summary>The reads the case proposal shares between the run, <c>GET chat</c> and the confirm endpoint (M7-9).</summary>
internal static class ChatCaseProposals
{
    /// <summary>The types <paramref name="assistantId"/> may propose now: on its list and active, oldest
    /// choice first (re-read on every request, nothing cached).</summary>
    public static async Task<IReadOnlyList<ProposableCaseType>> ProposableAsync(
        AppDbContext dbContext, Guid assistantId, CancellationToken cancellationToken)
    {
        var rows = await (
                from link in dbContext.AssistantCaseTypes.AsNoTracking()
                join type in dbContext.CaseTypes.AsNoTracking() on link.CaseTypeId equals type.Id
                join caseGroup in dbContext.CaseGroups.AsNoTracking() on type.DefaultGroupId equals caseGroup.Id
                where link.AssistantId == assistantId && type.IsActive
                orderby link.CreatedAt, type.Id
                select new { type.Id, type.Name, type.Description, type.DefaultDueHours, GroupId = caseGroup.Id, GroupName = caseGroup.Name, caseGroup.ArchivedAt })
            .ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(row => new ProposableCaseType(
                new CaseProposalOffer(row.Id, row.Name, row.Description),
                new CaseGroupRefView(row.GroupId, row.GroupName, row.ArchivedAt is not null),
                row.DefaultDueHours)),
        ];
    }

    /// <summary>The proposal views of <paramref name="messages"/>' case proposals, by message id, with the
    /// types re-checked now (<paramref name="assistantId"/>'s list, active). An unreadable snapshot has none.</summary>
    public static async Task<IReadOnlyDictionary<Guid, ChatCaseProposalView>> ViewsAsync(
        AppDbContext dbContext, Guid assistantId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var snapshots = messages
            .Select(message => (message, snapshot: message.ReadCaseProposal()))
            .Where(pair => pair.snapshot is not null)
            .ToList();
        if (snapshots.Count == 0)
        {
            return new Dictionary<Guid, ChatCaseProposalView>();
        }

        var typeIds = snapshots.Select(pair => pair.snapshot!.TypeId).Distinct().ToList();
        var types = await (
                from type in dbContext.CaseTypes.AsNoTracking()
                join caseGroup in dbContext.CaseGroups.AsNoTracking() on type.DefaultGroupId equals caseGroup.Id
                where typeIds.Contains(type.Id)
                select new { type.Id, type.Name, type.IsActive, type.DefaultDueHours, GroupId = caseGroup.Id, GroupName = caseGroup.Name, caseGroup.ArchivedAt })
            .ToDictionaryAsync(row => row.Id, cancellationToken);
        var listed = (await dbContext.AssistantCaseTypes.AsNoTracking()
                .Where(link => link.AssistantId == assistantId && typeIds.Contains(link.CaseTypeId))
                .Select(link => link.CaseTypeId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var views = new Dictionary<Guid, ChatCaseProposalView>();
        foreach (var (message, snapshot) in snapshots)
        {
            if (!types.TryGetValue(snapshot!.TypeId, out var type))
            {
                continue;
            }

            views[message.Id] = new ChatCaseProposalView(
                type.Id,
                type.Name,
                snapshot.Title,
                snapshot.Description,
                snapshot.Status,
                snapshot.Status == ChatCaseProposalStatus.Proposed && type.IsActive && listed.Contains(type.Id),
                new CaseGroupRefView(type.GroupId, type.GroupName, type.ArchivedAt is not null),
                type.DefaultDueHours,
                message.ProposedCaseId);
        }

        return views;
    }
}

/// <summary>
/// The model-chosen case proposal (M7-9, <c>Chat:FormRequests:Trigger = Model</c>): after the form
/// decision said no, one more selection call (<see cref="ModelInvocationPurpose.CaseProposal"/>, counted)
/// offers the assistant's proposable types by name and description; the model picks one and drafts the
/// title and description; the server accepts only an offered type and truncates the draft
/// (<see cref="CaseProposalRules.ParseCall"/>). Follows <see cref="ChatFormRequestTool"/>. The call also
/// offers <see cref="CaseProposalRules.NoMatchToolName"/> (#297): the model choosing it is no proposal (the
/// answer pipeline answers), not a failure — the same call, purpose and usage.
/// </summary>
/// <remarks>
/// <b>Failure.</b> When the model call fails, the keyword rule decides instead
/// (<see cref="CaseProposalRules.KeywordProposal"/>, decision T), so a model outage never breaks the
/// reply. M7-12's evaluation can call <see cref="SelectAsync"/> (or the rules directly) per question.
/// </remarks>
public sealed class ChatCaseProposalTool
{
    public const string ActivityName = "smartagri.chat.case_proposal";

    private readonly IChatClient _chat;
    private readonly ILogger<ChatCaseProposalTool> _logger;

    public ChatCaseProposalTool(IChatClient chat, ILogger<ChatCaseProposalTool> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    /// <summary>The proposal for <paramref name="question"/> among <paramref name="offers"/> (at least one),
    /// or <see langword="null"/> for none. Never throws for a model failure (keyword fallback).</summary>
    public async Task<CaseProposalDraft?> SelectAsync(
        Guid assistantId, IReadOnlyList<CaseProposalOffer> offers, string question, Guid askerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offers);
        using var activity = SmartAgriActivitySource.Instance.StartActivity(ActivityName);

        var options = new ModelInvocationAttribution(ModelInvocationPurpose.CaseProposal, askerId, assistantId).ToChatOptions();
        options.Tools = CaseProposalRules.Declarations(offers);
        options.ToolMode = ChatToolMode.Auto;
        options.AllowMultipleToolCalls = false;

        ChatResponse response;
        try
        {
            response = await _chat.GetResponseAsync(CaseProposalRules.SelectionPrompt(question), options, cancellationToken);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Only types: the model provider's error can quote the question it rejected.
            _logger.LogWarning("A conversation's case-proposal selection failed; the keyword rule decides instead: {Failure}", ExceptionSummary.Of(exception));
            activity?.SetStatus(ActivityStatusCode.Error);
            var fallback = CaseProposalRules.KeywordProposal(question, offers);
            activity?.SetTag("smartagri.case_proposal.status", fallback is null ? "fallback-none" : "fallback-keyword");
            return fallback;
        }

        var (match, draft) = CaseProposalRules.ParseCall(response, offers, question);
        activity?.SetTag("smartagri.case_proposal.status", match switch
        {
            CaseProposalCallMatch.Matched => "proposed",
            CaseProposalCallMatch.NoCall => "no-tool",
            CaseProposalCallMatch.NoMatch => "no-match",
            _ => "rejected",
        });
        return draft;
    }
}

/// <summary>
/// The case proposal (M7-9, plan §3 H, decisions L, T, U): the proposal stage's second proposal, after
/// the form request. Step 1 checks, on every request, that the conversation is kept (confirming needs the
/// saved message, and the case links the thread), that the asker is an internal account (never an
/// external customer) and that the assistant has a type it may propose now; keyword mode decides there
/// too, model mode in the stream with <see cref="ChatCaseProposalTool"/>.
/// </summary>
internal sealed class ChatCaseProposal(AppDbContext dbContext, RequestAccountRole roles, ChatCaseProposalTool tool) : IChatProposal
{
    public async Task<ChatProposalCandidate?> FindAsync(ChatProposalContext context, CancellationToken cancellationToken)
    {
        if (!context.Assistant.KeepConversations
            || await roles.GetAsync(context.AskerId, cancellationToken) is not { } role
            || !CaseGroupRules.IsEligibleMember(role))
        {
            return null;
        }

        var proposable = await ChatCaseProposals.ProposableAsync(dbContext, context.Assistant.Id, cancellationToken);
        if (proposable.Count == 0)
        {
            return null;
        }

        IReadOnlyList<CaseProposalOffer> offers = [.. proposable.Select(type => type.Offer)];
        if (context.Trigger == ChatFormRequestTrigger.Model)
        {
            return new ModelSelection(tool, context, proposable, offers);
        }

        return CaseProposalRules.KeywordProposal(context.Question, offers) is { } draft
            ? new Decided(Reply(draft, proposable))
            : null;
    }

    /// <summary>The reply of <paramref name="draft"/>, one of <paramref name="proposable"/>'s types (also the
    /// combined selection's case reply, #286).</summary>
    internal static ChatProposalReply Reply(CaseProposalDraft draft, IReadOnlyList<ProposableCaseType> proposable) =>
        new CaseProposalReply(draft, proposable.Single(type => type.Offer.TypeId == draft.Offer.TypeId));

    /// <summary>Keyword mode: already decided in step 1, no model call.</summary>
    private sealed class Decided(ChatProposalReply reply) : ChatProposalCandidate
    {
        public override Task<ChatProposalReply?> DecideAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ChatProposalReply?>(reply);
    }

    /// <summary>Model mode: one selection call, only when the proposals before it said no — or, when the form
    /// is offered too, folded into the combined selection by <see cref="ChatProposalStage"/> (#286).</summary>
    internal sealed class ModelSelection(
        ChatCaseProposalTool tool,
        ChatProposalContext context,
        IReadOnlyList<ProposableCaseType> proposable,
        IReadOnlyList<CaseProposalOffer> offers) : ChatProposalCandidate
    {
        /// <summary>The types the assistant may propose now (re-read for this request).</summary>
        public IReadOnlyList<ProposableCaseType> Proposable => proposable;

        /// <summary><see cref="Proposable"/> as offered to the rules and the model.</summary>
        public IReadOnlyList<CaseProposalOffer> Offers => offers;

        public override async Task<ChatProposalReply?> DecideAsync(CancellationToken cancellationToken) =>
            await tool.SelectAsync(context.Assistant.Id, offers, context.Question, context.AskerId, cancellationToken) is { } draft
                ? Reply(draft, proposable)
                : null;
    }

    /// <summary>The server's text and the snapshot; the type is re-read whenever the message is shown.</summary>
    private sealed class CaseProposalReply(CaseProposalDraft draft, ProposableCaseType type) : ChatProposalReply
    {
        public override string Text => CaseProposalRules.ProposalText(draft.Offer.Name);

        private ChatCaseProposalSnapshot Snapshot => ChatCaseProposalSnapshot.Propose(draft.Offer.TypeId, draft.Title, draft.Description);

        public override ChatMessage CreateMessage(ChatThread thread, DateTimeOffset now) =>
            ChatMessage.CaseProposal(thread, Text, Snapshot, now);

        public override ChatMessageView ToView(ChatMessage saved) =>
            ChatEndpoints.ToMessageView(saved, [], caseProposal: View(saved.ProposedCaseId));

        /// <summary>Never used: a case is only proposed in a kept conversation (see <see cref="FindAsync"/>).</summary>
        public override ChatMessageView Transient(DateTimeOffset now) =>
            new(
                Guid.CreateVersion7(),
                "assistant",
                null,
                new ChatReplyView("case-proposal", Text, [], null, [], null, null, null, View(null) with { Available = false }),
                now);

        private ChatCaseProposalView View(Guid? caseId) =>
            new(draft.Offer.TypeId, draft.Offer.Name, draft.Title, draft.Description, ChatCaseProposalStatus.Proposed, true, type.Group, type.DueHours, caseId);
    }
}
