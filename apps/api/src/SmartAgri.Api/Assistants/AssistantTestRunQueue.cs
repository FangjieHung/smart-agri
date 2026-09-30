using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Jobs;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Api.Assistants;

/// <summary>
/// Queues test-set runs (M3.5 plan §3, issue #124): the one place a run and its
/// <see cref="RunAssistantTestSetJob"/> are created, so the "at most one active run per
/// assistant; otherwise only <see cref="AssistantTestRun.RerunRequested"/>" rule and the
/// retention of <see cref="AssistantTestRun.KeptPerAssistant"/> runs hold for every trigger:
/// 「全部重跑」 (#124) and the automatic reruns of #125 (<see cref="RequestForKnowledgeBaseAsync"/>,
/// <see cref="RequestIfTestedAsync"/>, <see cref="ScheduleForKnowledgeBase"/>).
/// </summary>
/// <remarks>
/// An automatic rerun is asked for in the same database transaction as the change that causes
/// it, after that change's own save succeeded: the caller opens the transaction, saves, calls one
/// of these, and commits — the pattern <see cref="RunAssistantTestSetHandler"/> uses to queue a
/// follow-up run with a run's completion. The change and its rerun then commit (or roll back)
/// together. <see cref="RequestAsync"/> cannot simply join the change's <c>SaveChanges</c>: it
/// reads the active run and retries after a lost race (clearing the change tracker), which is safe
/// inside the transaction because EF Core rolls a failed <c>SaveChanges</c> back to a savepoint.
/// </remarks>
internal static class AssistantTestRunQueue
{
    private const int MaxSaveAttempts = 3;

    /// <summary>
    /// Queues a run for <paramref name="assistantId"/>, or — when one is already queued or
    /// running — only sets that run's <see cref="AssistantTestRun.RerunRequested"/>. Saves
    /// <paramref name="dbContext"/> (clearing its change tracker if a concurrent request wins a
    /// race, then deciding again). Returns the run that will cover the request.
    /// </summary>
    public static async Task<AssistantTestRun> RequestAsync(
        AppDbContext dbContext,
        Guid organizationId,
        Guid assistantId,
        AssistantTestRunTrigger trigger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var active = await dbContext.AssistantTestRuns
                .SingleOrDefaultAsync(
                    run => run.AssistantId == assistantId
                        && (run.Status == AssistantTestRunStatus.Queued || run.Status == AssistantTestRunStatus.Running),
                    cancellationToken);
            try
            {
                if (active is not null)
                {
                    active.RequestRerun(trigger);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return active;
                }

                var queued = AddQueued(dbContext, organizationId, assistantId, trigger, now);
                await dbContext.SaveChangesAsync(cancellationToken);
                await PruneAsync(dbContext, assistantId, cancellationToken);
                return queued;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
            {
                // The active run changed (started, finished) since it was read.
                dbContext.ChangeTracker.Clear();
            }
            catch (DbUpdateException exception) when (attempt < MaxSaveAttempts && DatabaseErrors.IsUniqueViolation(exception))
            {
                // A concurrent request queued a run first; ask that one to run again instead.
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Asks for a rerun (<see cref="RequestAsync"/>) of every assistant connected to
    /// <paramref name="knowledgeBaseId"/> that has at least one test case — the ones a change to
    /// that knowledge base affects (plan §3: 「連接了這個知識庫、而且有題組的所有助理」). Returns
    /// how many were asked for.
    /// </summary>
    public static async Task<int> RequestForKnowledgeBaseAsync(
        AppDbContext dbContext,
        Guid organizationId,
        Guid knowledgeBaseId,
        AssistantTestRunTrigger trigger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var assistantIds = await dbContext.AssistantKnowledgeBases
            .AsNoTracking()
            .Where(link => link.KnowledgeBaseId == knowledgeBaseId)
            .Select(link => link.AssistantId)
            .Where(assistantId => dbContext.AssistantTestCases.Any(testCase => testCase.AssistantId == assistantId))
            .Distinct()
            .OrderBy(assistantId => assistantId)
            .ToListAsync(cancellationToken);
        foreach (var assistantId in assistantIds)
        {
            await RequestAsync(dbContext, organizationId, assistantId, trigger, now, cancellationToken);
        }

        return assistantIds.Count;
    }

    /// <summary>Asks for a rerun of <paramref name="assistantId"/> (<see cref="RequestAsync"/>)
    /// only if it has at least one test case; returns whether it did.</summary>
    public static async Task<bool> RequestIfTestedAsync(
        AppDbContext dbContext,
        Guid organizationId,
        Guid assistantId,
        AssistantTestRunTrigger trigger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.AssistantTestCases.AnyAsync(testCase => testCase.AssistantId == assistantId, cancellationToken))
        {
            return false;
        }

        await RequestAsync(dbContext, organizationId, assistantId, trigger, now, cancellationToken);
        return true;
    }

    /// <summary>Adds (not saved) a <see cref="RequestAssistantTestRunsJob"/> that asks, at
    /// <paramref name="runAfter"/>, for the reruns <see cref="RequestForKnowledgeBaseAsync"/> would
    /// ask for now: a version approved to take effect in the future. Saved with the approval.</summary>
    public static BackgroundJob ScheduleForKnowledgeBase(
        AppDbContext dbContext, Guid organizationId, Guid knowledgeBaseId, DateTimeOffset now, DateTimeOffset runAfter)
    {
        var job = BackgroundJob.Create(
            organizationId, RequestAssistantTestRunsJob.Kind, new RequestAssistantTestRunsJob(knowledgeBaseId), now, runAfter);
        dbContext.BackgroundJobs.Add(job);
        return job;
    }

    /// <summary>Adds a new queued run and its job to <paramref name="dbContext"/> (not saved):
    /// they commit together. The caller must know no other run of the assistant is active.</summary>
    public static AssistantTestRun AddQueued(
        AppDbContext dbContext, Guid organizationId, Guid assistantId, AssistantTestRunTrigger trigger, DateTimeOffset now)
    {
        var run = AssistantTestRun.Queue(organizationId, assistantId, trigger, now);
        dbContext.AssistantTestRuns.Add(run);
        dbContext.BackgroundJobs.Add(BackgroundJob.Create(
            organizationId, RunAssistantTestSetJob.Kind, new RunAssistantTestSetJob(run.Id), now,
            maxAttempts: RunAssistantTestSetJob.MaxAttempts));
        return run;
    }

    /// <summary>Deletes (with their results) the assistant's runs beyond the newest
    /// <see cref="AssistantTestRun.KeptPerAssistant"/>; an active run is never deleted.</summary>
    public static async Task PruneAsync(AppDbContext dbContext, Guid assistantId, CancellationToken cancellationToken)
    {
        var stale = (await dbContext.AssistantTestRuns
                .AsNoTracking()
                .Where(run => run.AssistantId == assistantId)
                .OrderByDescending(run => run.QueuedAt)
                .ThenByDescending(run => run.Id)
                .Select(run => new { run.Id, run.Status })
                .ToListAsync(cancellationToken))
            .Skip(AssistantTestRun.KeptPerAssistant)
            .Where(run => run.Status is AssistantTestRunStatus.Completed or AssistantTestRunStatus.Failed)
            .Select(run => run.Id)
            .ToList();
        if (stale.Count > 0)
        {
            await dbContext.AssistantTestRuns.Where(run => stale.Contains(run.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
