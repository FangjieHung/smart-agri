using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>What <see cref="AssistantAcceptanceRules.Derive"/> needs of one test run.</summary>
public sealed record AssistantTestRunSnapshot(
    AssistantTestRunStatus Status,
    AssistantTestRunTrigger Trigger,
    AssistantTestRunTrigger? RerunTrigger,
    DateTimeOffset QueuedAt,
    int FailedCount);

/// <summary>
/// Derives <see cref="AssistantAcceptanceStatus"/> (M3.5 plan §3's table; issue #125) from the
/// assistant's kept test runs.
/// </summary>
/// <remarks>
/// <para>
/// Every change the plan calls 「已過期」 — a connected knowledge base's version taking effect, a
/// document disabled or restored, the assistant's answering settings or sources changing — asks
/// for a run with an automatic trigger (<see cref="AssistantTestRunTrigger.KnowledgeChanged"/>,
/// <see cref="AssistantTestRunTrigger.AssistantChanged"/>) at the moment it happens (a future
/// effective date: when it takes effect). So "changed since the latest completed run" is exactly
/// "a run queued after it carries an automatic trigger (its own, or a rerun asked for while it is
/// active), and none has completed since" — whether that run is still queued, running, or failed.
/// </para>
/// <para>
/// A 「全部重跑」 (<see cref="AssistantTestRunTrigger.Manual"/>) in progress does not make it
/// outdated: nothing changed, the latest completed result still stands. Known gap: a queued
/// manual run that absorbed an automatic request (only <see cref="AssistantTestRun.RerunRequested"/>
/// is set, and cleared when it starts) shows the previous result again while it runs, until it
/// completes with the fresh one.
/// </para>
/// </remarks>
public static class AssistantAcceptanceRules
{
    public static AssistantAcceptanceStatus Derive(bool hasTestCases, IEnumerable<AssistantTestRunSnapshot> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (!hasTestCases)
        {
            return AssistantAcceptanceStatus.NotAccepted;
        }

        var all = runs.ToList();
        var latestCompleted = all
            .Where(run => run.Status == AssistantTestRunStatus.Completed)
            .OrderByDescending(run => run.QueuedAt)
            .FirstOrDefault();
        if (latestCompleted is null)
        {
            return AssistantAcceptanceStatus.NotAccepted;
        }

        var changedSince = all.Any(run =>
            run.QueuedAt > latestCompleted.QueuedAt
            && run.Status != AssistantTestRunStatus.Completed
            && (IsAutomatic(run.Trigger) || (run.RerunTrigger is { } rerun && IsAutomatic(rerun))));
        if (changedSince)
        {
            return AssistantAcceptanceStatus.Outdated;
        }

        return latestCompleted.FailedCount > 0 ? AssistantAcceptanceStatus.Failed : AssistantAcceptanceStatus.Passed;
    }

    private static bool IsAutomatic(AssistantTestRunTrigger trigger) => trigger != AssistantTestRunTrigger.Manual;
}
