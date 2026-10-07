using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Assistants;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Assistants;

/// <summary>
/// Handles <see cref="RunAssistantTestSetJob.Kind"/> (M3.5 plan §3, Slice 2, issue #124): starts
/// the run, answers every test case of the assistant (in <see cref="AssistantTestCase.Ordinal"/>
/// order) through the same <see cref="GroundedAnswerService.AnswerAsync"/> path as a built
/// assistant's trial answer — the assistant's current rules and connected knowledge bases,
/// attributed <see cref="ModelInvocationPurpose.AssistantTest"/> to the assistant's owner —
/// judges each with <see cref="AssistantTestJudge"/>, and saves every result with the run's
/// completion in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// A follow-up test case (<see cref="AssistantTestCase.FollowUpOfId"/>) is answered with its
/// parent's question and the reply the parent got in this same run as history — exactly as
/// <c>eval-answers</c> does for <c>followUpOf</c> (M3 plan §7 D). A parent that is not an earlier
/// case of this run (deleted, or reordered) leaves the follow-up standalone.
/// </para>
/// <para>
/// A model or embedding failure (<see cref="ChatGenerationException"/>,
/// <see cref="KnowledgeEmbeddingException"/>) fails the run at once, without retrying
/// (<see cref="PermanentJobFailure"/>; plan §7 技術風險 1: a rerun costs up to 50 model calls).
/// Anything else (the database) is retried by the queue up to
/// <see cref="RunAssistantTestSetJob.MaxAttempts"/>, answering the whole set again; after the
/// last attempt the run fails too (<see cref="OnFinalFailureAsync"/>). Either way only that one
/// run is affected — each job is its own run, in its own scope. When the finished run (completed
/// or failed) has <see cref="AssistantTestRun.RerunRequested"/>, a fresh run is queued in the
/// same transaction.
/// </para>
/// </remarks>
internal sealed class RunAssistantTestSetHandler : IJobHandler
{
    private const int MaxSaveAttempts = 3;

    private readonly AppDbContext _dbContext;
    private readonly GroundedAnswerService _answers;
    private readonly IOrganizationChatModelResolver _chatModel;
    private readonly KnowledgeRetrievalSettings _retrieval;
    private readonly TimeProvider _clock;
    private readonly ILogger<RunAssistantTestSetHandler> _logger;

    public RunAssistantTestSetHandler(
        AppDbContext dbContext,
        GroundedAnswerService answers,
        IOrganizationChatModelResolver chatModel,
        KnowledgeRetrievalSettings retrieval,
        TimeProvider clock,
        ILogger<RunAssistantTestSetHandler> logger)
    {
        _dbContext = dbContext;
        _answers = answers;
        _chatModel = chatModel;
        _retrieval = retrieval;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var run = await FindAsync(job, cancellationToken);
        if (run is null || !run.IsActive)
        {
            // Deleted (with its assistant, or by retention) or already finished by an earlier
            // delivery of this job: nothing to do.
            return;
        }

        var assistant = await _dbContext.Assistants.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == run.AssistantId, cancellationToken);
        if (assistant is null)
        {
            return;
        }

        if (run.Status == AssistantTestRunStatus.Queued)
        {
            var minScore = assistant.MinScore ?? _retrieval.MinScore;
            var candidateMinScore = _retrieval.CandidateFloor(minScore);

            // The model the organization's calls of this scope go to (M6 plan §3 B), not the
            // deployment's: what the run records is what answered it.
            var model = (await _chatModel.ResolveAsync(cancellationToken)).Entry.Model;
            var started = await ChangeRunAsync(
                run,
                current =>
                {
                    if (current.Status != AssistantTestRunStatus.Queued)
                    {
                        return false;
                    }

                    current.Start(GroundedAnswerPrompt.Version, model, minScore, _clock.GetUtcNow(), candidateMinScore);
                    return true;
                },
                cancellationToken);
            if (!started && run.Status != AssistantTestRunStatus.Running)
            {
                return;
            }
        }

        var testCases = await _dbContext.AssistantTestCases.AsNoTracking()
            .Where(testCase => testCase.AssistantId == assistant.Id)
            .OrderBy(testCase => testCase.Ordinal)
            .ThenBy(testCase => testCase.Id)
            .ToListAsync(cancellationToken);
        var knowledgeBaseIds = await _dbContext.AssistantKnowledgeBases.AsNoTracking()
            .Where(link => link.AssistantId == assistant.Id)
            .OrderBy(link => link.ConnectedAt)
            .ThenBy(link => link.KnowledgeBaseId)
            .Select(link => link.KnowledgeBaseId)
            .ToListAsync(cancellationToken);
        var profile = GroundedAnswerProfile.For(assistant, knowledgeBaseIds);

        var replies = new Dictionary<Guid, GroundedReply>();
        var results = new List<AssistantTestResult>(testCases.Count);
        foreach (var testCase in testCases)
        {
            var request = new GroundedAnswerRequest(
                profile, testCase.Question, History(testCase, testCases, replies), assistant.OwnerAccountId, assistant.Id,
                ModelInvocationPurpose.AssistantTest);

            GroundedAnswerResult answered;
            try
            {
                answered = await _answers.AnswerAsync(request, cancellationToken);
            }
            catch (ChatGenerationException exception)
            {
                throw new PermanentJobFailure($"Test run {run.Id}: the chat model failed: {exception.Message}", exception);
            }
            catch (KnowledgeEmbeddingException exception)
            {
                throw new PermanentJobFailure($"Test run {run.Id}: a question could not be embedded: {exception.Message}", exception);
            }

            replies[testCase.Id] = answered.Reply;
            results.Add(ToResult(run, testCase, answered));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        _dbContext.AssistantTestResults.AddRange(results);
        var passed = results.Count(result => result.Passed);
        var completed = await ChangeRunAsync(
            run,
            current =>
            {
                if (current.Status != AssistantTestRunStatus.Running)
                {
                    return false;
                }

                current.Complete(passed, results.Count - passed, _clock.GetUtcNow());
                return true;
            },
            cancellationToken);
        if (!completed)
        {
            // Finished or deleted elsewhere; disposing the transaction discards these results.
            return;
        }

        var followUp = await QueueRequestedRerunAsync(run, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (followUp)
        {
            await AssistantTestRunQueue.PruneAsync(_dbContext, run.AssistantId, cancellationToken);
        }

        _logger.LogInformation(
            "Assistant test run {RunId} completed: {Passed} passed, {Failed} failed.", run.Id, passed, results.Count - passed);
    }

    /// <summary>Never leaves a run stuck <c>queued</c>/<c>running</c>. Runs in the queue's
    /// transaction that marks the job failed.</summary>
    public async Task OnFinalFailureAsync(JobContext job, string error, CancellationToken cancellationToken)
    {
        var run = await FindAsync(job, cancellationToken);
        if (run is null || !run.IsActive)
        {
            return;
        }

        var failed = await ChangeRunAsync(
            run,
            current =>
            {
                if (!current.IsActive)
                {
                    return false;
                }

                current.Fail(_clock.GetUtcNow());
                return true;
            },
            cancellationToken);
        if (!failed)
        {
            return;
        }

        _logger.LogWarning("Assistant test run {RunId} failed: {Error}", run.Id, error);
        if (await QueueRequestedRerunAsync(run, cancellationToken))
        {
            await AssistantTestRunQueue.PruneAsync(_dbContext, run.AssistantId, cancellationToken);
        }
    }

    /// <summary>Queues (and saves) the fresh run a finished <paramref name="run"/> was asked for
    /// while active. The follow-up's trigger is the one it was asked for with
    /// (<see cref="AssistantTestRun.RerunTrigger"/>; issue #125), not the finished run's.
    /// Saved separately from the finished run, so the one-active-run index never sees two.</summary>
    private async Task<bool> QueueRequestedRerunAsync(AssistantTestRun run, CancellationToken cancellationToken)
    {
        if (!run.RerunRequested)
        {
            return false;
        }

        AssistantTestRunQueue.AddQueued(
            _dbContext, run.OrganizationId, run.AssistantId, run.RerunTrigger ?? run.Trigger, _clock.GetUtcNow());
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Applies <paramref name="change"/> and saves. <see cref="AssistantTestRun.Status"/> and
    /// <see cref="AssistantTestRun.RerunRequested"/> are concurrency tokens: when a rerun request
    /// (or another delivery of this job) changed the row meanwhile, reloads it and tries again.
    /// False when <paramref name="change"/> no longer applies or the run is gone.
    /// </summary>
    private async Task<bool> ChangeRunAsync(
        AssistantTestRun run, Func<AssistantTestRun, bool> change, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (!change(run))
            {
                return false;
            }

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
            {
                var entry = _dbContext.Entry(run);
                await entry.ReloadAsync(cancellationToken);
                if (entry.State == EntityState.Detached)
                {
                    return false;
                }
            }
        }
    }

    private static AssistantTestResult ToResult(AssistantTestRun run, AssistantTestCase testCase, GroundedAnswerResult answered)
    {
        var reply = answered.Reply;
        var actualKind = AnswerKinds.ToReplyKind(reply.Kind);
        var cited = reply.Citations.Select(citation => citation.DocumentId).Distinct().ToList();
        var verdict = AssistantTestJudge.Judge(testCase.ExpectedKind, testCase.ExpectedDocumentIds, actualKind, cited);
        var topScore = answered.Retrieval.Passages.Count == 0
            ? (double?)null
            : answered.Retrieval.Passages.Max(passage => passage.Score);
        return AssistantTestResult.Record(
            run,
            testCase,
            actualKind,
            reply.Text,
            cited,
            reply.RejectionReason is { } reason ? AnswerKinds.ToRejectionReason(reason) : null,
            topScore,
            verdict.FailureReason);
    }

    /// <summary>The follow-up's one-turn history: its parent's question and the reply the parent
    /// got earlier in this run; empty for a standalone case (or a parent not answered before it).</summary>
    private static IReadOnlyList<ConversationTurn> History(
        AssistantTestCase testCase, IReadOnlyList<AssistantTestCase> all, IReadOnlyDictionary<Guid, GroundedReply> replies)
    {
        if (testCase.FollowUpOfId is not { } parentId || !replies.TryGetValue(parentId, out var parentReply))
        {
            return [];
        }

        var parent = all.First(candidate => candidate.Id == parentId);
        return
        [
            new ConversationTurn(ConversationAuthor.Account, parent.Question),
            new ConversationTurn(ConversationAuthor.Assistant, parentReply.Text),
        ];
    }

    private Task<AssistantTestRun?> FindAsync(JobContext job, CancellationToken cancellationToken)
    {
        var runId = job.ReadPayload<RunAssistantTestSetJob>().RunId;
        return _dbContext.AssistantTestRuns.SingleOrDefaultAsync(run => run.Id == runId, cancellationToken);
    }
}
