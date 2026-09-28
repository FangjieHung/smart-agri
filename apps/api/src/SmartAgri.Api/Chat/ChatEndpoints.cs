using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Chat;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Chat;

// View records mirror apps/admin/src/app/core/domain/conversation.model.ts, so the generated
// types line up as closely as this slice's scope allows. Deliberate differences (write these
// down as they come up, per KnowledgeBase/AssistantEndpoints precedent):
// - ids are GUIDs, and ChatCitationView.id is a plain string ("citation-{messageId}-{ordinal}")
//   rather than the frontend's branded `citation-${string}`;
// - ChatMessageView / ChatReplyView are not true discriminated unions on the wire: both are one
//   C# record with the inapplicable fields always sent as `null` (not omitted — issue #106: an
//   earlier version used JsonIgnore(WhenWritingNull) to omit them, but that made the actual JSON
//   disagree with the generated OpenAPI document, which describes every property as required.
//   PR #103 worked around the resulting frontend bug; this is the real fix), since Minimal
//   API/System.Text.Json has no built-in support for an OpenAPI oneOf keyed by a sibling
//   "kind"/"author" string the way the frontend's TS union is. A future ticket can special-case
//   the OpenAPI document if the generated TS type needs to be a true union instead of an
//   all-optional shape;
// - AssistantChatView.welcome / .privacyNotice / .suggestedPrompts have no backing data model yet
//   (M3 plan §4 does not add an Assistant field for them, and the wizard's suggested-prompt system,
//   mapping §5.6 point 3, is explicitly fixture-only and out of scope): welcome and privacyNotice are
//   derived from fields that do exist (name/purpose/historyMode), and suggestedPrompts is always [].
//   A later ticket owns making these configurable;
// - form-request / submission-receipt reply kinds do not exist (M3 plan §8: forms are M4).

/// <summary>One citation a <c>company-data</c> reply shows inline.</summary>
public sealed record ChatCitationView(string Id, string KnowledgeBaseName, string DocumentName, string Excerpt, string UpdatedLabel);

/// <summary>
/// One reply's content, shaped like the frontend's <c>ChatReplyView</c> union — see the
/// class-level note on why this is one record rather than a true union. <see cref="Citations"/>
/// is only ever non-empty for <c>company-data</c>; <see cref="Notice"/> only for
/// <c>general-knowledge</c>; <see cref="NextSteps"/> only for <c>no-result</c>.
/// </summary>
public sealed record ChatReplyView(
    string Kind,
    string Text,
    IReadOnlyList<ChatCitationView> Citations,
    string? Notice,
    IReadOnlyList<string> NextSteps);

/// <summary>One turn. <see cref="Text"/> is set for <c>author: "account"</c>,
/// <see cref="Reply"/> for <c>author: "assistant"</c> — never both. Both are always present in
/// the JSON as either a value or explicit <c>null</c> (issue #106), matching what the OpenAPI
/// document already describes.</summary>
public sealed record ChatMessageView(
    Guid Id,
    string Author,
    string? Text,
    ChatReplyView? Reply,
    DateTimeOffset CreatedAt);

/// <summary>A suggested opening prompt; always empty in this slice (see the class-level note).</summary>
public sealed record ChatSuggestedPromptView(string Id, string Text);

/// <summary><c>GET /api/v1/assistants/{id}/chat</c> response.</summary>
public sealed record AssistantChatView(
    Guid AssistantId,
    string AssistantName,
    string Purpose,
    Guid? ThreadId,
    string Title,
    string HistoryMode,
    string Welcome,
    string PrivacyNotice,
    IReadOnlyList<ChatSuggestedPromptView> SuggestedPrompts,
    IReadOnlyList<ChatMessageView> Messages);

/// <summary>Side-rail summary of one thread; never carries any message text (mapping §5.4).</summary>
public sealed record ChatThreadSummaryView(Guid Id, string Title, int MessageCount, DateTimeOffset UpdatedAt);

/// <summary><c>GET/DELETE .../conversations</c> response: the caller's own threads with this
/// assistant, newest activity first.</summary>
public sealed record ChatThreadListView(
    Guid AssistantId,
    string AssistantName,
    string HistoryMode,
    IReadOnlyList<ChatThreadSummaryView> Threads,
    string HistoryNotice);

/// <summary><c>PATCH .../conversations/{threadId}</c> request.</summary>
public sealed record RenameChatThreadRequest(string? Title);

/// <summary><c>GET .../chat/citations/{messageId}/{ordinal}</c> response: the citation's full
/// snapshot, for the citation drawer's 原文 (M3 plan §4).</summary>
public sealed record ChatCitationDetailView(
    string Id, string KnowledgeBaseName, string DocumentName, string LocationLabel, int VersionNumber, string UpdatedLabel, string Text);

/// <summary>One row of <c>GET /api/v1/chat/recent-conversations</c> (assistant-workspace-model
/// ADR's cross-assistant sidebar): the ten most recently active threads across every assistant
/// the caller may currently use.</summary>
public sealed record RecentConversationView(
    Guid AssistantId, string AssistantName, Guid ThreadId, string Title, int MessageCount, DateTimeOffset UpdatedAt);

/// <summary>
/// Conversation thread CRUD and history reads (M3 plan §4/§5 Slice 6; mapping §2.4). Sending a
/// message and streaming a reply are <see cref="ChatRunEndpoints"/> (#77), which reuses this
/// class's access checks and views so a streamed reply looks exactly like the same message read
/// back through <c>GET chat</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint needs a signed-in account and, beyond that, that the caller may <b>use</b> the
/// assistant right now (<see cref="AssistantUseAccess.UsableBy"/>): not the owner-only
/// <c>manage-assistants</c> gate the settings endpoints use. An assistant id that does not exist,
/// belongs to another organization, is paused for a non-owner, or is not shared with the caller
/// gets <see cref="ForbiddenReason.AssistantUse"/> — byte-identical in every case, including
/// right after the caller's own share is revoked (#73/#76 acceptance).
/// </para>
/// <para>
/// A <c>threadId</c> is always untrusted input (mapping §5.4): resolving one always goes through
/// <see cref="ChatThreadAccess.OwnedBy"/>, so another account's thread — even the assistant
/// owner's own attempt to read someone else's — gets <see cref="ForbiddenReason.ChatThread"/>,
/// byte-identical to an unknown id. The same rule covers <c>messageId</c>/<c>ordinal</c> on the
/// citation endpoint: it never reveals whether a message exists, only whether the caller's own
/// thread contains it.
/// </para>
/// </remarks>
public static class ChatEndpoints
{
    private const string SavedHistoryNotice = "只有你自己看得到這裡的對話紀錄，助理擁有者無法讀取對話內容。";
    private const string NotSavedHistoryNotice = "這個助理的規則關閉了保存對話，離開這個畫面後就不會留下紀錄。";
    private const string DefaultThreadTitle = ChatRunRules.DefaultThreadTitle;

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var assistantChat = endpoints.MapGroup("/api/v1/assistants/{id:guid}/chat")
            .RequireAuthorization();

        assistantChat.MapGet("/conversations", ListConversationsAsync)
            .Produces<ChatThreadListView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        assistantChat.MapPost("/conversations", CreateConversationAsync)
            .Produces<AssistantChatView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistantChat.MapPatch("/conversations/{threadId:guid}", RenameConversationAsync)
            .Produces<ChatThreadSummaryView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        assistantChat.MapDelete("/conversations/{threadId:guid}", DeleteConversationAsync)
            .Produces<ChatThreadListView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        assistantChat.MapGet("", GetChatAsync)
            .Produces<AssistantChatView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        assistantChat.MapGet("/citations/{messageId:guid}/{ordinal:int}", GetCitationAsync)
            .Produces<ChatCitationDetailView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        endpoints.MapGet("/api/v1/chat/recent-conversations", RecentConversationsAsync)
            .RequireAuthorization()
            .Produces<IReadOnlyList<RecentConversationView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return endpoints;
    }

    internal static async Task<IResult> ListConversationsAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        if (!assistant.KeepConversations)
        {
            return Results.Ok(NotSavedThreadList(assistant));
        }

        var threads = await ThreadsForAsync(dbContext, assistant.Id, viewerId, cancellationToken);
        return Results.Ok(ToThreadList(assistant, threads));
    }

    /// <summary>
    /// A blank new thread; refused with <c>422</c> when the assistant's rules do not keep
    /// conversations at all (#76 acceptance) — there would be nothing to create.
    /// </summary>
    internal static async Task<IResult> CreateConversationAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        if (!assistant.KeepConversations)
        {
            const string message = "這個助理的規則已經關閉「保存對話」，無法建立新的對話。";
            return ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { ["conversation"] = [message] });
        }

        var now = clock.GetUtcNow();
        var thread = new ChatThread(assistant, viewerId, DefaultThreadTitle, now);
        dbContext.ChatThreads.Add(thread);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/api/v1/assistants/{assistant.Id}/chat?conversation={thread.Id}",
            ToChatView(assistant, thread, []));
    }

    /// <summary>Order of checks mirrors <c>AssistantEndpoints</c>: assistant usability, then
    /// thread ownership (both <c>403</c>), then title validity (<c>422</c>) — mapping §5.5
    /// ("改名的權限檢查在驗證之前").</summary>
    internal static async Task<IResult> RenameConversationAsync(
        Guid id,
        Guid threadId,
        RenameChatThreadRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        var thread = await FindThreadAsync(dbContext, assistant.Id, viewerId, threadId, cancellationToken);
        if (thread is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.ChatThread);
        }

        var validated = ChatThreadTitleRules.Validate(request.Title);
        if (!validated.IsValid)
        {
            return ApiErrors.ValidationFailed(validated.Failures);
        }

        thread.Rename(validated.Value, clock.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToSummary(thread));
    }

    internal static async Task<IResult> DeleteConversationAsync(
        Guid id,
        Guid threadId,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        var thread = await FindThreadAsync(dbContext, assistant.Id, viewerId, threadId, cancellationToken);
        if (thread is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.ChatThread);
        }

        dbContext.ChatThreads.Remove(thread);
        await dbContext.SaveChangesAsync(cancellationToken);

        var remaining = await ThreadsForAsync(dbContext, assistant.Id, viewerId, cancellationToken);
        return Results.Ok(ToThreadList(assistant, remaining));
    }

    /// <summary>
    /// <paramref name="conversation"/> omitted opens the most recently active thread, or a
    /// blank one (<c>threadId: null</c>) when there is none yet or the assistant does not keep
    /// conversations at all — never <c>404</c> (mapping §2.4 "省略時開啟最後活動的那一段").
    /// </summary>
    internal static async Task<IResult> GetChatAsync(
        Guid id,
        Guid? conversation,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        if (!assistant.KeepConversations)
        {
            return Results.Ok(ToChatView(assistant, null, []));
        }

        ChatThread? thread;
        if (conversation is { } threadId)
        {
            thread = await FindThreadAsync(dbContext, assistant.Id, viewerId, threadId, cancellationToken);
            if (thread is null)
            {
                return ApiErrors.NotFound(ForbiddenReason.ChatThread);
            }
        }
        else
        {
            thread = await dbContext.ChatThreads
                .Where(ChatThreadAccess.OwnedBy(viewerId, assistant.Id))
                .OrderByDescending(candidate => candidate.LastActivityAt)
                .ThenByDescending(candidate => candidate.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var messages = thread is null ? [] : await LoadMessagesAsync(dbContext, thread.Id, cancellationToken);
        return Results.Ok(ToChatView(assistant, thread, messages));
    }

    /// <summary>
    /// A citation's full snapshot text. <paramref name="messageId"/> and
    /// <paramref name="ordinal"/> are resolved only within threads the caller owns for this
    /// assistant, so this can never be used to read another account's citation
    /// (<see cref="ForbiddenReason.ChatThread"/> either way — see the class remarks).
    /// </summary>
    internal static async Task<IResult> GetCitationAsync(
        Guid id,
        Guid messageId,
        int ordinal,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindUsableAsync(dbContext, permissions, id, viewerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        }

        var citation = await (
            from thread in dbContext.ChatThreads.Where(ChatThreadAccess.OwnedBy(viewerId, assistant.Id))
            join message in dbContext.ChatMessages on thread.Id equals message.ThreadId
            join c in dbContext.ChatMessageCitations on message.Id equals c.MessageId
            where message.Id == messageId && c.Ordinal == ordinal
            select c)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (citation is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.ChatThread);
        }

        return Results.Ok(ToCitationDetail(citation));
    }

    /// <summary>The ten most recently active threads across every assistant the caller may
    /// currently use, across the whole organization's assistants (assistant-workspace-model
    /// ADR). An assistant that is no longer usable (unshared, deleted, paused for a non-owner)
    /// silently drops its threads from this list, even though they still exist.</summary>
    internal static async Task<IResult> RecentConversationsAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var granted = await permissions.GetAsync(viewerId, cancellationToken);
        var hasUseSharedAssistants = granted.Contains(AccountPermission.UseSharedAssistants);
        var usableAssistants = await dbContext.Assistants
            .AsNoTracking()
            .Where(AssistantUseAccess.UsableBy(viewerId, hasUseSharedAssistants, dbContext.AssistantShares))
            .Select(assistant => new { assistant.Id, assistant.Name })
            .ToListAsync(cancellationToken);
        var usableIds = usableAssistants.Select(assistant => assistant.Id).ToList();
        var nameById = usableAssistants.ToDictionary(assistant => assistant.Id, assistant => assistant.Name);

        var recent = await dbContext.ChatThreads
            .AsNoTracking()
            .Where(thread => thread.AccountId == viewerId && usableIds.Contains(thread.AssistantId))
            .OrderByDescending(thread => thread.LastActivityAt)
            .ThenByDescending(thread => thread.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        IReadOnlyList<RecentConversationView> view =
        [
            .. recent.Select(thread => new RecentConversationView(
                thread.AssistantId, nameById[thread.AssistantId], thread.Id, thread.Title, thread.MessageCount, thread.LastActivityAt)),
        ];
        return Results.Ok(view);
    }

    internal static async Task<Assistant?> FindUsableAsync(
        AppDbContext dbContext, RequestAccountPermissions permissions, Guid assistantId, Guid viewerId, CancellationToken cancellationToken)
    {
        var granted = await permissions.GetAsync(viewerId, cancellationToken);
        var hasUseSharedAssistants = granted.Contains(AccountPermission.UseSharedAssistants);
        return await dbContext.Assistants
            .Where(AssistantUseAccess.UsableBy(viewerId, hasUseSharedAssistants, dbContext.AssistantShares))
            .SingleOrDefaultAsync(assistant => assistant.Id == assistantId, cancellationToken);
    }

    internal static Task<ChatThread?> FindThreadAsync(
        AppDbContext dbContext, Guid assistantId, Guid viewerId, Guid threadId, CancellationToken cancellationToken) =>
        dbContext.ChatThreads
            .Where(ChatThreadAccess.OwnedBy(viewerId, assistantId))
            .SingleOrDefaultAsync(thread => thread.Id == threadId, cancellationToken);

    private static Task<List<ChatThread>> ThreadsForAsync(
        AppDbContext dbContext, Guid assistantId, Guid viewerId, CancellationToken cancellationToken) =>
        dbContext.ChatThreads
            .AsNoTracking()
            .Where(ChatThreadAccess.OwnedBy(viewerId, assistantId))
            .OrderByDescending(thread => thread.LastActivityAt)
            .ThenByDescending(thread => thread.CreatedAt)
            .ToListAsync(cancellationToken);

    private static async Task<IReadOnlyList<ChatMessageView>> LoadMessagesAsync(
        AppDbContext dbContext, Guid threadId, CancellationToken cancellationToken)
    {
        // CreatedAt alone is not a stable order: two turns saved in the same save (or the same
        // millisecond) can tie. Id (a version-7 GUID) is monotonic with creation order, so it
        // breaks ties the same way insertion order did.
        var messages = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(message => message.ThreadId == threadId)
            .OrderBy(message => message.Sequence)
            .ToListAsync(cancellationToken);
        if (messages.Count == 0)
        {
            return [];
        }

        var messageIds = messages.Select(message => message.Id).ToList();
        var citations = await dbContext.ChatMessageCitations
            .AsNoTracking()
            .Where(citation => messageIds.Contains(citation.MessageId))
            .OrderBy(citation => citation.Ordinal)
            .ToListAsync(cancellationToken);
        var citationsByMessage = citations.GroupBy(citation => citation.MessageId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ChatMessageCitation>)[.. group]);

        return [.. messages.Select(message =>
            ToMessageView(message, citationsByMessage.TryGetValue(message.Id, out var found) ? found : []))];
    }

    internal static ChatMessageView ToMessageView(ChatMessage message, IReadOnlyList<ChatMessageCitation> citations) =>
        message.Author == ChatMessageAuthor.Account
            ? new ChatMessageView(message.Id, "account", message.Text, null, message.CreatedAt)
            : new ChatMessageView(message.Id, "assistant", null, ToReplyView(message, citations), message.CreatedAt);

    private static ChatReplyView ToReplyView(ChatMessage message, IReadOnlyList<ChatMessageCitation> citations) =>
        message.ReplyKind switch
        {
            ChatReplyKind.CompanyData => new ChatReplyView(
                "company-data", message.Text, [.. citations.Select(ToCitationView)], null, []),
            ChatReplyKind.GeneralKnowledge => new ChatReplyView(
                "general-knowledge", message.Text, [], message.Notice, []),
            ChatReplyKind.NoResult => new ChatReplyView(
                "no-result", message.Text, [], null, message.NextSteps),
            _ => throw new InvalidOperationException("An assistant message must have a reply kind."),
        };

    internal static ChatCitationView ToCitationView(ChatMessageCitation citation) =>
        new(CitationId(citation), citation.KnowledgeBaseName, citation.DocumentName, citation.Excerpt, UpdatedLabel(citation.VersionEffectiveFrom));

    private static ChatCitationDetailView ToCitationDetail(ChatMessageCitation citation) =>
        new(
            CitationId(citation),
            citation.KnowledgeBaseName,
            citation.DocumentName,
            citation.LocationLabel,
            citation.VersionNumber,
            UpdatedLabel(citation.VersionEffectiveFrom),
            citation.Text);

    private static string CitationId(ChatMessageCitation citation) => CitationId(citation.MessageId, citation.Ordinal);

    internal static string CitationId(Guid messageId, int ordinal) => $"citation-{messageId}-{ordinal}";

    internal static string UpdatedLabel(DateTimeOffset? effectiveFrom) =>
        effectiveFrom?.ToString("yyyy-MM-dd") ?? string.Empty;

    private static AssistantChatView ToChatView(Assistant assistant, ChatThread? thread, IReadOnlyList<ChatMessageView> messages)
    {
        var historyMode = assistant.KeepConversations ? "saved" : "not-saved";
        return new AssistantChatView(
            assistant.Id,
            assistant.Name,
            assistant.Purpose,
            thread?.Id,
            thread?.Title ?? DefaultThreadTitle,
            historyMode,
            $"你好，我是「{assistant.Name}」，{assistant.Purpose}有什麼我可以幫忙的嗎？",
            assistant.KeepConversations ? SavedHistoryNotice : NotSavedHistoryNotice,
            [],
            messages);
    }

    private static ChatThreadSummaryView ToSummary(ChatThread thread) =>
        new(thread.Id, thread.Title, thread.MessageCount, thread.LastActivityAt);

    private static ChatThreadListView ToThreadList(Assistant assistant, IReadOnlyList<ChatThread> threads) =>
        new(assistant.Id, assistant.Name, "saved", [.. threads.Select(ToSummary)], SavedHistoryNotice);

    private static ChatThreadListView NotSavedThreadList(Assistant assistant) =>
        new(assistant.Id, assistant.Name, "not-saved", [], NotSavedHistoryNotice);
}
