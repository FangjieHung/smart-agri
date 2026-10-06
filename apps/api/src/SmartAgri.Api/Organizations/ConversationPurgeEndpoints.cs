using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Organizations;

/// <summary>One row of <c>GET /api/v1/organization/retention/assistants</c>: an assistant of the
/// organization and the conversations its members keep on it — numbers only, never what anyone
/// wrote (the assistant's owner cannot read the members' conversations either).</summary>
/// <param name="KeepConversations">The assistant's 「保留使用者自己的對話紀錄」 switch.</param>
/// <param name="ThreadCount">Saved threads of every member on this assistant.</param>
/// <param name="AccountCount">Accounts with at least one of them.</param>
/// <param name="LastActivityAt">The newest of their last messages; <see langword="null"/> when
/// there is no saved thread.</param>
public sealed record OrganizationRetentionAssistantView(
    Guid AssistantId,
    string AssistantName,
    bool KeepConversations,
    int ThreadCount,
    int AccountCount,
    DateTimeOffset? LastActivityAt);

/// <summary><c>GET /api/v1/assistants/{id}/chat/conversations/summary</c>: how many saved threads
/// (and of how many members) the assistant has, for the button and the note beside its switch.</summary>
/// <param name="CanPurge">Whether the caller is the organization's manager, who alone may purge
/// them (the frontend shows the button, or else 「請聯絡管理者」).</param>
public sealed record AssistantConversationSummaryView(int ThreadCount, int AccountCount, bool CanPurge);

/// <summary><c>POST /api/v1/assistants/{id}/chat/conversations:purge</c>'s response.</summary>
public sealed record AssistantConversationPurgeView(int DeletedThreadCount);

/// <summary>
/// Deleting an assistant's saved conversations at once (M6 plan §3 H, §5 Slice 5, issue #242;
/// decision E: the manager only). Three endpoints, numbers only — no response ever carries a
/// thread's title or a message:
/// <list type="bullet">
/// <item>the settings page's per-assistant list (manager);</item>
/// <item>one assistant's summary (manager, or whoever may read the assistant's settings —
/// <c>manage-assistants</c> and the owner, <see cref="AssistantAccess.ManageableBy"/>);</item>
/// <item>the purge itself (manager), in batches like the daily cleanup
/// (<see cref="RetentionCleanupService.DeleteThreadsInBatchesAsync"/>).</item>
/// </list>
/// </summary>
/// <remarks>
/// The list and the purge look at the manager only, never at the assistant's owner or who may use
/// it: anyone else — the owner included — and an assistant that does not exist or belongs to
/// another organization get the same <c>403 organization-settings</c>. The purge works whether the
/// switch is on or off, leaves <c>AnswerOutcome</c>s (they hold no content and expire by the
/// retention) and the handoff copies in issues alone, writes one <c>conversations-purged</c>
/// activity, and notifies nobody.
/// </remarks>
public static class ConversationPurgeEndpoints
{
    public const string AssistantsPath = OrganizationRetentionEndpoints.Path + "/assistants";

    public static IEndpointRouteBuilder MapConversationPurgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AssistantsPath, ListAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<IReadOnlyList<OrganizationRetentionAssistantView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        var conversations = endpoints.MapGroup("/api/v1/assistants/{id:guid}/chat")
            .RequireAuthorization();

        conversations.MapGet("/conversations/summary", SummaryAsync)
            .Produces<AssistantConversationSummaryView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        conversations.MapPost("/conversations:purge", PurgeAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<AssistantConversationPurgeView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        return endpoints;
    }

    /// <summary>Every assistant of the organization, oldest first, with its saved threads counted
    /// in one grouped query.</summary>
    internal static async Task<IResult> ListAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var assistants = await dbContext.Assistants
            .AsNoTracking()
            .OrderBy(assistant => assistant.CreatedAt)
            .ThenBy(assistant => assistant.Id)
            .Select(assistant => new { assistant.Id, assistant.Name, assistant.KeepConversations })
            .ToListAsync(cancellationToken);
        var counts = await CountsAsync(dbContext.ChatThreads, cancellationToken);
        return Results.Ok(assistants.ConvertAll(assistant =>
        {
            var count = counts.GetValueOrDefault(assistant.Id);
            return new OrganizationRetentionAssistantView(
                assistant.Id, assistant.Name, assistant.KeepConversations,
                count?.ThreadCount ?? 0, count?.AccountCount ?? 0, count?.LastActivityAt);
        }));
    }

    /// <summary>
    /// The manager reads any of the organization's assistants; anyone else needs
    /// <c>manage-assistants</c> and to own it, exactly like <c>GET …/settings</c>. Everything else —
    /// including an id that does not exist or belongs to another organization — is the same
    /// <c>403 assistant-configuration</c>.
    /// </summary>
    internal static async Task<IResult> SummaryAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountRole roles,
        RequestAccountPermissions permissions,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var isAdmin = await roles.IsOrganizationAdminAsync(viewerId, cancellationToken);
        var assistants = dbContext.Assistants.AsNoTracking().Where(assistant => assistant.Id == id);
        if (!isAdmin)
        {
            var granted = await permissions.GetAsync(viewerId, cancellationToken);
            if (!granted.Contains(AccountPermission.ManageAssistants))
            {
                return ApiErrors.Forbidden(ForbiddenReason.AssistantConfiguration);
            }

            assistants = assistants.Where(AssistantAccess.ManageableBy(viewerId));
        }

        if (!await assistants.AnyAsync(cancellationToken))
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var count = (await CountsAsync(dbContext.ChatThreads.Where(thread => thread.AssistantId == id), cancellationToken))
            .GetValueOrDefault(id);
        return Results.Ok(new AssistantConversationSummaryView(count?.ThreadCount ?? 0, count?.AccountCount ?? 0, isAdmin));
    }

    /// <summary>
    /// After the manager check (the policy): the assistant must be the organization's (else the same
    /// <c>403 organization-settings</c>). Deletes every member's threads on it in batches, then
    /// records the purge — also one that found nothing, since it is the manager's own action.
    /// </summary>
    internal static async Task<IResult> PurgeAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        RetentionCleanupService cleanup,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await dbContext.Assistants
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new { candidate.Id, candidate.OrganizationId, candidate.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.OrganizationSettings);
        }

        var deleted = await cleanup.DeleteThreadsInBatchesAsync(
            dbContext.ChatThreads.Where(thread => thread.AssistantId == assistant.Id), cancellationToken);

        dbContext.OrganizationActivities.Add(OrganizationActivity.ConversationsPurged(
            assistant.OrganizationId, callerId, clock.GetUtcNow(), assistant.Id, assistant.Name, deleted.Count));
        await dbContext.SaveChangesAsync(cancellationToken);

        loggerFactory.CreateLogger(typeof(ConversationPurgeEndpoints)).LogInformation(
            "Account {AccountId} purged {ThreadCount} conversation threads of assistant {AssistantId} in {Batches} batches.",
            callerId, deleted.Count, assistant.Id, deleted.Batches);
        return Results.Ok(new AssistantConversationPurgeView(deleted.Count));
    }

    private sealed record ThreadCounts(int ThreadCount, int AccountCount, DateTimeOffset LastActivityAt);

    /// <summary><paramref name="threads"/> counted per assistant (<c>COUNT</c>, <c>COUNT(DISTINCT
    /// "AccountId")</c>, <c>MAX("LastActivityAt")</c>); an assistant without threads is absent.</summary>
    private static async Task<Dictionary<Guid, ThreadCounts>> CountsAsync(
        IQueryable<ChatThread> threads, CancellationToken cancellationToken) =>
        await threads
            .GroupBy(thread => thread.AssistantId)
            .Select(group => new
            {
                AssistantId = group.Key,
                ThreadCount = group.Count(),
                AccountCount = group.Select(thread => thread.AccountId).Distinct().Count(),
                LastActivityAt = group.Max(thread => thread.LastActivityAt),
            })
            .ToDictionaryAsync(
                row => row.AssistantId,
                row => new ThreadCounts(row.ThreadCount, row.AccountCount, row.LastActivityAt),
                cancellationToken);
}
