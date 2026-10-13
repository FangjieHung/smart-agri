using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Accounts;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Notifications;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Notifications;

/// <summary>One 轉真人 notification (#355): which assistant, which channel, when — and who marked it
/// handled. Nothing of the visitor.</summary>
public sealed record VisitorHandoffView(
    Guid Id,
    Guid AssistantId,
    string AssistantName,
    VisitorHandoffChannel Channel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? HandledAt,
    Guid? HandledByAccountId,
    string? HandledByName);

/// <summary>The side-nav count: pending handoffs of the caller's assistants.</summary>
public sealed record VisitorHandoffAttentionView(int PendingCount);

/// <summary>
/// 轉真人 notifications (#355) for the people responsible for them: the <b>owners of the assistants</b>
/// whose visitors were given the refusal message. The owner published the website and LINE channels
/// and wrote that message, and is already the default handler of a member's 轉給專人; so each owner sees
/// (and marks) only the handoffs of their own assistants. Anything else — another owner's, another
/// organization's, an id that does not exist — is the same <c>403 handoff</c>.
/// </summary>
public static class VisitorHandoffEndpoints
{
    public const string Path = "/api/v1/handoffs";

    /// <summary>At most this many rows per list (newest first); pending ones are rarely that many.</summary>
    public const int ListLimit = 200;

    /// <summary>The <c>status</c> values of <c>GET /api/v1/handoffs</c>.</summary>
    public static readonly IReadOnlyList<string> Statuses = ["pending", "handled", "all"];

    public static IEndpointRouteBuilder MapVisitorHandoffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handoffs = endpoints.MapGroup(Path).RequireAuthorization();

        handoffs.MapGet(string.Empty, ListAsync)
            .Produces<List<VisitorHandoffView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        handoffs.MapGet("/attention", AttentionAsync)
            .Produces<VisitorHandoffAttentionView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        handoffs.MapPost("/{id:guid}:handle", HandleAsync)
            .Produces<VisitorHandoffView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>The caller's handoffs, newest first: <c>status</c> <c>pending</c> (the default),
    /// <c>handled</c> or <c>all</c>; anything else is <c>422</c>.</summary>
    internal static async Task<IResult> ListAsync(string? status, HttpContext httpContext, AppDbContext dbContext, CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var filter = string.IsNullOrEmpty(status) ? "pending" : status;
        if (!Statuses.Contains(filter))
        {
            const string message = "status 只能是 pending、handled 或 all。";
            return ApiErrors.ValidationFailed([new ValidationFailure("status", message)]);
        }

        var rows = Owned(dbContext.VisitorHandoffs.AsNoTracking(), dbContext, callerId);
        rows = filter switch
        {
            "pending" => rows.Where(handoff => handoff.HandledAt == null),
            "handled" => rows.Where(handoff => handoff.HandledAt != null),
            _ => rows,
        };
        var found = await rows
            .OrderByDescending(handoff => handoff.CreatedAt)
            .ThenByDescending(handoff => handoff.Id)
            .Take(ListLimit)
            .ToListAsync(cancellationToken);
        var assistantIds = found.Select(handoff => handoff.AssistantId).Distinct().ToList();
        var assistantNames = await dbContext.Assistants.AsNoTracking()
            .Where(assistant => assistantIds.Contains(assistant.Id))
            .ToDictionaryAsync(assistant => assistant.Id, assistant => assistant.Name, cancellationToken);
        var names = await AccountNames.LoadAsync(dbContext, found.Select(handoff => handoff.HandledByAccountId), cancellationToken);
        return Results.Ok(found.Select(handoff => View(handoff, assistantNames[handoff.AssistantId], names)).ToList());
    }

    internal static async Task<IResult> AttentionAsync(HttpContext httpContext, AppDbContext dbContext, CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var pending = await Owned(dbContext.VisitorHandoffs.AsNoTracking(), dbContext, callerId)
            .CountAsync(handoff => handoff.HandledAt == null, cancellationToken);
        return Results.Ok(new VisitorHandoffAttentionView(pending));
    }

    /// <summary>Marks one handoff handled (<c>200</c>, its view). Already handled: unchanged, the same
    /// <c>200</c> — whoever marked it first stays.</summary>
    internal static async Task<IResult> HandleAsync(
        Guid id, HttpContext httpContext, AppDbContext dbContext, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var handoff = await Owned(dbContext.VisitorHandoffs, dbContext, callerId)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (handoff is null)
        {
            return ApiErrors.Forbidden(ForbiddenReason.Handoff);
        }

        if (handoff.MarkHandled(callerId, clock.GetUtcNow()))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var assistantName = await dbContext.Assistants.AsNoTracking()
            .Where(assistant => assistant.Id == handoff.AssistantId)
            .Select(assistant => assistant.Name)
            .SingleAsync(cancellationToken);
        var names = await AccountNames.LoadAsync(dbContext, [handoff.HandledByAccountId], cancellationToken);
        return Results.Ok(View(handoff, assistantName, names));
    }

    /// <summary>The handoffs of the assistants <paramref name="callerId"/> owns (under the organization filter).</summary>
    private static IQueryable<VisitorHandoff> Owned(IQueryable<VisitorHandoff> source, AppDbContext dbContext, Guid callerId) =>
        source.Where(handoff => dbContext.Assistants.Any(
            assistant => assistant.Id == handoff.AssistantId && assistant.OwnerAccountId == callerId));

    private static VisitorHandoffView View(VisitorHandoff handoff, string assistantName, AccountNameLookup names) =>
        new(
            handoff.Id,
            handoff.AssistantId,
            assistantName,
            handoff.Channel,
            handoff.CreatedAt,
            handoff.HandledAt,
            handoff.HandledByAccountId,
            names.NameOf(handoff.HandledByAccountId));
}
