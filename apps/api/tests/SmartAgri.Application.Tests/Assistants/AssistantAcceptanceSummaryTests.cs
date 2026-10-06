using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;
using Status = SmartAgri.Domain.Assistants.AssistantTestRunStatus;
using Trigger = SmartAgri.Domain.Assistants.AssistantTestRunTrigger;

namespace SmartAgri.Application.Tests.Assistants;

/// <summary><see cref="AssistantAcceptanceRules.Summarize"/>: the status is exactly
/// <see cref="AssistantAcceptanceRules.Derive"/>'s, plus whether the latest completed run passed
/// every case (M5a plan §3 C; issue #194).</summary>
public sealed class AssistantAcceptanceSummaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    private static AssistantTestRunSnapshot Run(Status status, Trigger trigger, int minutes, int failed = 0) =>
        new(status, trigger, null, T0.AddMinutes(minutes), failed);

    private static readonly (bool HasTestCases, AssistantTestRunSnapshot[] Runs, AssistantAcceptanceStatus Status, bool? LatestPassed)[] Cases =
    [
        (false, [Run(Status.Completed, Trigger.Manual, 0)], AssistantAcceptanceStatus.NotAccepted, null),
        (true, [], AssistantAcceptanceStatus.NotAccepted, null),
        (true, [Run(Status.Running, Trigger.Manual, 0)], AssistantAcceptanceStatus.NotAccepted, null),
        (true, [Run(Status.Completed, Trigger.Manual, 0)], AssistantAcceptanceStatus.Passed, true),
        (true, [Run(Status.Completed, Trigger.Manual, 0, failed: 1)], AssistantAcceptanceStatus.Failed, false),
        (
            true,
            [Run(Status.Completed, Trigger.Manual, 0), Run(Status.Queued, Trigger.KnowledgeChanged, 1)],
            AssistantAcceptanceStatus.Outdated, true
        ),
        (
            true,
            [Run(Status.Completed, Trigger.Manual, 0, failed: 2), Run(Status.Failed, Trigger.AssistantChanged, 1)],
            AssistantAcceptanceStatus.Outdated, false
        ),
        (
            // A later failed run (no results) does not hide the latest completed one.
            true,
            [Run(Status.Completed, Trigger.Manual, 0, failed: 1), Run(Status.Completed, Trigger.Manual, 1), Run(Status.Failed, Trigger.Manual, 2)],
            AssistantAcceptanceStatus.Passed, true
        ),
    ];

    [Fact]
    public void Summarize_reports_the_derived_status_and_the_latest_completed_runs_outcome()
    {
        for (var index = 0; index < Cases.Length; index++)
        {
            var (hasTestCases, runs, status, latestPassed) = Cases[index];
            var summary = AssistantAcceptanceRules.Summarize(hasTestCases, runs);

            summary.Status.ShouldBe(status, $"case {index}");
            summary.LatestCompletedRunPassed.ShouldBe(latestPassed, $"case {index}");
            AssistantAcceptanceRules.Derive(hasTestCases, runs).ShouldBe(status, $"case {index}");
        }
    }
}
