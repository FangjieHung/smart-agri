using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Answers;

/// <summary>One reply kind's count in a range (every <see cref="AnswerReplyKind"/> is always
/// present, zero when nothing of that kind happened, like <c>KnowledgeBaseTally.StatusCounts</c>).</summary>
public sealed record ReplyKindCountView(AnswerReplyKind Kind, int Count);

/// <summary>One rejection reason's count in a range (every <see cref="AnswerRejectionReason"/>
/// is always present).</summary>
public sealed record RejectionReasonCountView(AnswerRejectionReason Reason, int Count);

/// <summary>One document's citation count, newest-cited-most first.</summary>
public sealed record CitedDocumentCountView(Guid DocumentId, string DocumentName, int Count);

/// <summary><c>GET /api/v1/assistants/{id}/analytics</c>'s response (M3.5 plan §3, §4, Slice 6):
/// replaces the frontend's <c>AssistantAnalyticsView</c> mock (conversation count, resolved
/// count, satisfaction) with what the backend can actually derive from <see cref="AnswerOutcome"/>
/// — it never saw those three, so they are not part of this shape (see #132 note in the PR).</summary>
public sealed record AssistantAnalyticsView(
    DateOnly From,
    DateOnly To,
    int TotalReplies,
    IReadOnlyList<ReplyKindCountView> ReplyKinds,
    IReadOnlyList<RejectionReasonCountView> RejectionReasons,
    IReadOnlyList<CitedDocumentCountView> MostCitedDocuments)
{
    /// <summary>How many of <see cref="MostCitedDocuments"/> to return.</summary>
    public const int MostCitedDocumentsLimit = 10;
}

/// <summary>
/// <c>GET /api/v1/assistants/{id}/analytics?from=&amp;to=</c> (M3.5 plan §3, §4, Slice 6; issue
/// #128): one assistant's answer-outcome analytics, aggregated from <see cref="AnswerOutcome"/>
/// rows of every channel (real conversations, trial answers; test-set reruns once #124 exists).
/// <c>S+MA+OWN</c>, exactly like <c>GET .../settings</c> (<see cref="AssistantAccess.ManageableBy"/>):
/// an id that does not exist, belongs to another organization, or is not the caller's own gets the
/// same <c>403 assistant-configuration</c>, never a <c>404</c>. <c>from</c>/<c>to</c> default to
/// the newest 30 days and may span at most 180 (<see cref="AnswerAnalyticsRange"/>); an invalid
/// range is <c>422 invalid-date-range</c>.
/// </summary>
public static class AssistantAnalyticsEndpoints
{
    public const string InvalidDateRangeReason = "invalid-date-range";

    public static IEndpointRouteBuilder MapAssistantAnalyticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/assistants/{id:guid}/analytics", GetAsync)
            .RequireAuthorization()
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration)
            .Produces<AssistantAnalyticsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    internal static async Task<IResult> GetAsync(
        Guid id,
        DateOnly? from,
        DateOnly? to,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await dbContext.Assistants.AsNoTracking()
            .Where(AssistantAccess.ManageableBy(viewerId))
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var (range, error) = AnswerAnalyticsRange.Resolve(from, to, clock.GetUtcNow());
        if (range is null)
        {
            return ApiErrors.WithReason(StatusCodes.Status422UnprocessableEntity, InvalidDateRangeReason, error!, field: "to");
        }

        var rows = await dbContext.AnswerOutcomes.AsNoTracking()
            .Where(outcome => outcome.AssistantId == id && outcome.At >= range.FromUtc && outcome.At < range.ToExclusiveUtc)
            .Select(outcome => new { outcome.ReplyKind, outcome.RejectionReason, outcome.CitedDocumentIds })
            .ToListAsync(cancellationToken);

        var mostCited = await MostCitedDocumentsAsync(
            dbContext, rows.SelectMany(row => row.CitedDocumentIds), AssistantAnalyticsView.MostCitedDocumentsLimit, cancellationToken);

        return Results.Ok(new AssistantAnalyticsView(
            range.From,
            range.To,
            rows.Count,
            [.. Enum.GetValues<AnswerReplyKind>().Select(kind => new ReplyKindCountView(kind, rows.Count(row => row.ReplyKind == kind)))],
            [.. Enum.GetValues<AnswerRejectionReason>()
                .Select(reason => new RejectionReasonCountView(reason, rows.Count(row => row.RejectionReason == reason)))],
            mostCited));
    }

    /// <summary>The most-cited of <paramref name="citedDocumentIds"/> (every citation across the
    /// rows, one entry per row per document), limited to <paramref name="limit"/>, named from
    /// the current <c>KnowledgeDocuments</c> — a document deleted since simply drops out.</summary>
    internal static async Task<IReadOnlyList<CitedDocumentCountView>> MostCitedDocumentsAsync(
        AppDbContext dbContext, IEnumerable<Guid> citedDocumentIds, int limit, CancellationToken cancellationToken)
    {
        var counts = citedDocumentIds
            .GroupBy(documentId => documentId)
            .ToDictionary(group => group.Key, group => group.Count());
        if (counts.Count == 0)
        {
            return [];
        }

        var names = await dbContext.KnowledgeDocuments.AsNoTracking()
            .Where(document => counts.Keys.Contains(document.Id))
            .Select(document => new { document.Id, document.Name })
            .ToDictionaryAsync(document => document.Id, document => document.Name, cancellationToken);

        return
        [
            .. counts
                .Where(pair => names.ContainsKey(pair.Key))
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .Take(limit)
                .Select(pair => new CitedDocumentCountView(pair.Key, names[pair.Key], pair.Value)),
        ];
    }
}
