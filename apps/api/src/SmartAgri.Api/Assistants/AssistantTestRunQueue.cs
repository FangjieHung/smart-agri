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
/// retention of <see cref="AssistantTestRun.KeptPerAssistant"/> runs hold for every trigger
/// (Slice 3, #125, is expected to call <see cref="RequestAsync"/> too).
/// </summary>
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
