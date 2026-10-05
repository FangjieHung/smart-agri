using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Answers;
using SmartAgri.Api.Assistants;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Answers;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Operations;

/// <summary>One assistant's knowledge-base answer rates within the range (M3.5 plan §3, Slice 6):
/// <see cref="AnswerReplyKind.DatabaseQuery"/> rows are never counted here (M4 #178 — see
/// <see cref="DatabaseQueryOperationsView"/>). Trial answers made
/// before any assistant existed (no <c>assistantId</c>) never appear here — only
/// <see cref="AssistantAnalyticsEndpoints"/>'s per-assistant breakdown could show them, and it
/// does not either, by definition.</summary>
/// <param name="NoResultRate"><c>no-result</c> replies over <paramref name="TotalReplies"/>; 0
/// when there were none.</param>
/// <param name="RejectedCitationRate">Replies rejected specifically over a citation problem
/// (<see cref="AnswerRejectionReason.CitationOutOfRange"/> or
/// <see cref="AnswerRejectionReason.NoCitation"/>) over <paramref name="TotalReplies"/>; 0 when
/// there were none. Narrower than <see cref="NoResultRate"/> on purpose: a
/// <see cref="AnswerRejectionReason.BelowThreshold"/> refusal is "nothing relevant enough was
/// found", not "the model cited badly".</param>
public sealed record AssistantOperationsView(
    Guid AssistantId, string AssistantName, int TotalReplies, double NoResultRate, double RejectedCitationRate);

/// <summary>Knowledge-base health for the operations summary, read straight off the current
/// <c>KnowledgeDocumentVersions</c> table (M3.5 plan §3): not date-ranged, since it is the
/// current backlog, not something that happened within the period.</summary>
/// <param name="ProcessingFailedCount">Documents whose latest version's processing status is
/// <see cref="KnowledgeDocumentStatus.Failed"/>.</param>
/// <param name="OverduePendingReviewCount">Documents whose latest version is still
/// <see cref="KnowledgeReviewState.PendingReview"/> more than
/// <see cref="OperationsSummaryEndpoints.OverduePendingReviewDays"/> days after it was
/// uploaded.</param>
public sealed record KnowledgeOperationsView(int ProcessingFailedCount, int OverduePendingReviewCount);

/// <summary>Handling-issue numbers for the operations summary (issue #126).</summary>
/// <param name="OpenCount">Every unresolved issue (<c>open</c> or <c>in-progress</c>) in the
/// organization right now — like <see cref="KnowledgeOperationsView"/>, the current backlog, not
/// date-ranged.</param>
/// <param name="AverageResolutionHours">Average hours from creation to resolution of the issues
/// resolved within the range; <see langword="null"/> when none was.</param>
public sealed record IssuesSummaryView(int OpenCount, double? AverageResolutionHours);

/// <summary>
/// The organization's conversation database query answers within the range (M4 #178): every
/// <see cref="AnswerReplyKind.DatabaseQuery"/> outcome, by result. Counted on its own and never
/// mixed into <see cref="AssistantOperationsView"/>'s replies or rates. Like
/// <see cref="OperationsSummaryView.MostCitedDocuments"/>, includes the rows of assistants
/// deleted since.
/// </summary>
/// <param name="TotalCount">Every database query answer in the range.</param>
/// <param name="AnsweredCount"><see cref="AnswerDatabaseQueryResult.Answered"/>.</param>
/// <param name="NotPermittedCount"><see cref="AnswerDatabaseQueryResult.NotPermitted"/>.</param>
/// <param name="InsufficientRecordsCount"><see cref="AnswerDatabaseQueryResult.InsufficientRecords"/>.</param>
/// <param name="FailedCount"><see cref="AnswerDatabaseQueryResult.Failed"/>.</param>
/// <param name="FailureRate"><paramref name="FailedCount"/> over <paramref name="TotalCount"/>; 0
/// when there were none.</param>
public sealed record DatabaseQueryOperationsView(
    int TotalCount, int AnsweredCount, int NotPermittedCount, int InsufficientRecordsCount, int FailedCount, double FailureRate);

/// <summary><c>GET /api/v1/operations/summary</c>'s response (M3.5 plan §3, Slice 6;
/// <see cref="DatabaseQueries"/> since M4 #178).</summary>
public sealed record OperationsSummaryView(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<AssistantOperationsView> Assistants,
    IReadOnlyList<CitedDocumentCountView> MostCitedDocuments,
    KnowledgeOperationsView Knowledge,
    IssuesSummaryView Issues,
    DatabaseQueryOperationsView DatabaseQueries)
{
    public const int MostCitedDocumentsLimit = 10;
}

/// <summary>
/// <c>GET /api/v1/operations/summary?from=&amp;to=</c> (M3.5 plan §3, Slice 6; issue #128):
/// the organization-level operational rollup — every assistant's reply and rejection rates,
/// the organization's most-cited documents, knowledge-base processing health, issue counts, and
/// (M4 #178) the conversation database query answers by result, kept apart from the reply rates. <c>manage-assistants</c> only, no ownership check: unlike an
/// assistant's own analytics, this is an organization-wide view, so any account that may manage
/// assistants may see every assistant's numbers, not only its own. Never any conversation
/// content — only what <see cref="AnswerOutcome"/> already discarded content to record.
/// </summary>
public static class OperationsSummaryEndpoints
{
    /// <summary>A pending-review version older than this (M3.5 plan §7, decision left open;
    /// this ticket's default) counts as overdue.</summary>
    public const int OverduePendingReviewDays = 7;

    public static IEndpointRouteBuilder MapOperationsSummaryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/operations/summary", GetAsync)
            .RequireAuthorization()
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.OperationsSummary)
            .Produces<OperationsSummaryView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    internal static async Task<IResult> GetAsync(
        DateOnly? from,
        DateOnly? to,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var (range, error) = AnswerAnalyticsRange.Resolve(from, to, now);
        if (range is null)
        {
            return ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity, AssistantAnalyticsEndpoints.InvalidDateRangeReason, error!, field: "to");
        }

        var rows = await dbContext.AnswerOutcomes.AsNoTracking()
            .Where(outcome => outcome.At >= range.FromUtc && outcome.At < range.ToExclusiveUtc)
            .Select(outcome => new
            {
                outcome.AssistantId, outcome.ReplyKind, outcome.RejectionReason, outcome.DatabaseQueryResult, outcome.CitedDocumentIds,
            })
            .ToListAsync(cancellationToken);
        var queryResults = rows
            .Where(row => row.ReplyKind == AnswerReplyKind.DatabaseQuery)
            .Select(row => row.DatabaseQueryResult)
            .ToList();
        rows = [.. rows.Where(row => row.ReplyKind != AnswerReplyKind.DatabaseQuery)];

        var assistantNames = await dbContext.Assistants.AsNoTracking()
            .Select(assistant => new { assistant.Id, assistant.Name })
            .ToDictionaryAsync(assistant => assistant.Id, assistant => assistant.Name, cancellationToken);

        var assistants = rows
            .Where(row => row.AssistantId.HasValue)
            .GroupBy(row => row.AssistantId!.Value)
            .Where(group => assistantNames.ContainsKey(group.Key))
            .Select(group =>
            {
                var total = group.Count();
                var noResult = group.Count(row => row.RejectionReason is not null);
                var rejectedCitation = group.Count(row =>
                    row.RejectionReason is AnswerRejectionReason.CitationOutOfRange or AnswerRejectionReason.NoCitation);
                return new AssistantOperationsView(
                    group.Key,
                    assistantNames[group.Key],
                    total,
                    total == 0 ? 0 : (double)noResult / total,
                    total == 0 ? 0 : (double)rejectedCitation / total);
            })
            .OrderByDescending(view => view.TotalReplies)
            .ThenBy(view => view.AssistantName, StringComparer.Ordinal)
            .ToList();

        var mostCited = await AssistantAnalyticsEndpoints.MostCitedDocumentsAsync(
            dbContext, rows.SelectMany(row => row.CitedDocumentIds), OperationsSummaryView.MostCitedDocumentsLimit, cancellationToken);

        var knowledge = await KnowledgeOperationsAsync(dbContext, now, cancellationToken);

        var (openIssues, averageResolutionHours) = await AssistantIssueEndpoints.OperationsNumbersAsync(
            dbContext, range.FromUtc, range.ToExclusiveUtc, cancellationToken);

        return Results.Ok(new OperationsSummaryView(
            range.From,
            range.To,
            assistants,
            mostCited,
            knowledge,
            new IssuesSummaryView(openIssues, averageResolutionHours),
            DatabaseQueriesOf(queryResults)));
    }

    private static DatabaseQueryOperationsView DatabaseQueriesOf(IReadOnlyCollection<AnswerDatabaseQueryResult?> results)
    {
        var failed = results.Count(result => result == AnswerDatabaseQueryResult.Failed);
        return new DatabaseQueryOperationsView(
            results.Count,
            results.Count(result => result == AnswerDatabaseQueryResult.Answered),
            results.Count(result => result == AnswerDatabaseQueryResult.NotPermitted),
            results.Count(result => result == AnswerDatabaseQueryResult.InsufficientRecords),
            failed,
            results.Count == 0 ? 0 : (double)failed / results.Count);
    }

    /// <summary>Each document's latest version only (highest <c>VersionNumber</c>), the same
    /// join <c>KnowledgeItemStates.Of</c> uses.</summary>
    private static async Task<KnowledgeOperationsView> KnowledgeOperationsAsync(
        AppDbContext dbContext, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var latestVersions = dbContext.KnowledgeDocumentVersions.AsNoTracking()
            .Where(version => version.VersionNumber == dbContext.KnowledgeDocumentVersions
                .Where(candidate => candidate.DocumentId == version.DocumentId)
                .Max(candidate => candidate.VersionNumber));

        var processingFailedCount = await latestVersions
            .CountAsync(version => version.ProcessingStatus == KnowledgeDocumentStatus.Failed, cancellationToken);

        var overdueBefore = now.AddDays(-OverduePendingReviewDays);
        var overduePendingReviewCount = await latestVersions
            .CountAsync(
                version => version.ReviewState == KnowledgeReviewState.PendingReview && version.UploadedAt <= overdueBefore,
                cancellationToken);

        return new KnowledgeOperationsView(processingFailedCount, overduePendingReviewCount);
    }
}
