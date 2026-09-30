using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

/// <summary>One test run (「全部重跑」 once), without its results.</summary>
/// <param name="RerunRequested">Another run was asked for while this one was queued or running;
/// one more run follows it (see <see cref="AssistantTestRun"/>).</param>
/// <param name="PromptVersion"><see langword="null"/> until the run starts, as are
/// <paramref name="Model"/> and <paramref name="MinScore"/>.</param>
public sealed record AssistantTestRunView(
    Guid Id,
    Guid AssistantId,
    AssistantTestRunTrigger Trigger,
    AssistantTestRunStatus Status,
    bool RerunRequested,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int PassedCount,
    int FailedCount,
    string? PromptVersion,
    string? Model,
    double? MinScore);

/// <summary>One test case's outcome in a run: a snapshot of the question and what it expected,
/// and what the assistant actually answered.</summary>
/// <param name="TopScore">The best retrieval score for the question, to compare with the run's
/// <c>minScore</c>; <see langword="null"/> when nothing was found.</param>
/// <param name="FailureReason"><see langword="null"/> exactly when <paramref name="Passed"/>.</param>
public sealed record AssistantTestResultView(
    Guid Id,
    Guid TestCaseId,
    int Ordinal,
    string Question,
    AssistantTestExpectedKind ExpectedKind,
    IReadOnlyList<Guid> ExpectedDocumentIds,
    AnswerReplyKind ActualKind,
    string AnswerText,
    IReadOnlyList<Guid> CitedDocumentIds,
    AnswerRejectionReason? RejectionReason,
    double? TopScore,
    bool Passed,
    AssistantTestFailureReason? FailureReason);

/// <summary><c>GET /api/v1/assistants/{id}/test-runs/{runId}</c>: the run and its results in
/// test-case order (empty until the run completes, and for a failed run).</summary>
public sealed record AssistantTestRunDetailView(AssistantTestRunView Run, IReadOnlyList<AssistantTestResultView> Results);

/// <summary>
/// 「全部重跑」 and its history (M3.5 plan §3/§5 Slice 2, issue #124). Same access rule as the
/// assistant's settings and test set (<c>S+MA+OWN</c>, <see cref="ForbiddenReason.AssistantConfiguration"/>):
/// an assistant (or run) id that does not exist, belongs to another organization or is not the
/// caller's own gets the exact same <c>403</c> as <c>AssistantTestCaseEndpoints</c>.
/// </summary>
public static class AssistantTestRunEndpoints
{
    /// <summary>The 422 message when the assistant has no test case to run.</summary>
    public const string NoTestCasesMessage = "這個助理還沒有任何測試題，請先新增題目再重跑。";

    public static IEndpointRouteBuilder MapAssistantTestRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var testRuns = endpoints.MapGroup("/api/v1/assistants/{id:guid}/test-runs")
            .RequireAuthorization()
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration);

        testRuns.MapPost("", RequestAsync)
            .Produces<AssistantTestRunView>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        testRuns.MapGet("", ListAsync)
            .Produces<IReadOnlyList<AssistantTestRunView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        testRuns.MapGet("/{runId:guid}", GetAsync)
            .Produces<AssistantTestRunDetailView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>
    /// Queues a <c>manual</c> run (<c>202</c> with the new run). When a run is already queued or
    /// running, no second one is created: that run gets <c>rerunRequested: true</c> and is
    /// returned instead (still <c>202</c>). No test case at all is <c>422</c>.
    /// </summary>
    internal static async Task<IResult> RequestAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        if (!await dbContext.AssistantTestCases.AnyAsync(testCase => testCase.AssistantId == assistant.Id, cancellationToken))
        {
            return ApiErrors.ValidationFailed(
                NoTestCasesMessage, new Dictionary<string, string[]> { ["testCases"] = [NoTestCasesMessage] });
        }

        var run = await AssistantTestRunQueue.RequestAsync(
            dbContext, assistant.OrganizationId, assistant.Id, AssistantTestRunTrigger.Manual, clock.GetUtcNow(), cancellationToken);
        return Results.Accepted($"/api/v1/assistants/{assistant.Id}/test-runs/{run.Id}", ToView(run));
    }

    /// <summary>The assistant's runs, newest first (at most
    /// <see cref="AssistantTestRun.KeptPerAssistant"/> are kept).</summary>
    internal static async Task<IResult> ListAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var runs = await dbContext.AssistantTestRuns.AsNoTracking()
            .Where(run => run.AssistantId == assistant.Id)
            .OrderByDescending(run => run.QueuedAt)
            .ThenByDescending(run => run.Id)
            .ToListAsync(cancellationToken);
        return Results.Ok(runs.ConvertAll(ToView));
    }

    internal static async Task<IResult> GetAsync(
        Guid id,
        Guid runId,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var run = await dbContext.AssistantTestRuns.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id && candidate.Id == runId, cancellationToken);
        if (run is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var results = await dbContext.AssistantTestResults.AsNoTracking()
            .Where(result => result.RunId == run.Id)
            .OrderBy(result => result.Ordinal)
            .ThenBy(result => result.Id)
            .ToListAsync(cancellationToken);
        return Results.Ok(new AssistantTestRunDetailView(ToView(run), results.ConvertAll(ToView)));
    }

    private static Task<Assistant?> FindManageableAsync(
        AppDbContext dbContext, Guid id, Guid callerId, CancellationToken cancellationToken) =>
        dbContext.Assistants
            .AsNoTracking()
            .Where(AssistantAccess.ManageableBy(callerId))
            .SingleOrDefaultAsync(assistant => assistant.Id == id, cancellationToken);

    private static AssistantTestRunView ToView(AssistantTestRun run) =>
        new(
            run.Id,
            run.AssistantId,
            run.Trigger,
            run.Status,
            run.RerunRequested,
            run.QueuedAt,
            run.StartedAt,
            run.CompletedAt,
            run.PassedCount,
            run.FailedCount,
            run.PromptVersion,
            run.Model,
            run.MinScore);

    private static AssistantTestResultView ToView(AssistantTestResult result) =>
        new(
            result.Id,
            result.TestCaseId,
            result.Ordinal,
            result.QuestionSnapshot,
            result.ExpectedKind,
            result.ExpectedDocumentIds,
            result.ActualKind,
            result.AnswerText,
            result.CitedDocumentIds,
            result.RejectionReason,
            result.TopScore,
            result.Passed,
            result.FailureReason);
}
