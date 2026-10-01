using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain;
using SmartAgri.Domain.Assistants;
using Status = SmartAgri.Domain.Assistants.AssistantTestRunStatus;
using Trigger = SmartAgri.Domain.Assistants.AssistantTestRunTrigger;

namespace SmartAgri.Application.Tests.Assistants;

public sealed class AssistantAcceptanceRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

    private static AssistantTestRunSnapshot Run(
        Status status, Trigger trigger, int minutes, int failed = 0, Trigger? rerunTrigger = null) =>
        new(status, trigger, rerunTrigger, T0.AddMinutes(minutes), failed);

    [Fact]
    public void Without_test_cases_it_is_not_accepted_whatever_ran_before()
    {
        AssistantAcceptanceRules.Derive(false, [Run(Status.Completed, Trigger.Manual, 0)])
            .ShouldBe(AssistantAcceptanceStatus.NotAccepted);
    }

    [Fact]
    public void Until_a_run_completes_it_is_not_accepted()
    {
        AssistantAcceptanceRules.Derive(true, []).ShouldBe(AssistantAcceptanceStatus.NotAccepted);
        AssistantAcceptanceRules.Derive(true, [Run(Status.Failed, Trigger.Manual, 0), Run(Status.Running, Trigger.KnowledgeChanged, 1)])
            .ShouldBe(AssistantAcceptanceStatus.NotAccepted);
    }

    [Fact]
    public void The_latest_completed_run_decides_passed_or_failed()
    {
        AssistantAcceptanceRules.Derive(true, [Run(Status.Completed, Trigger.Manual, 0, failed: 1), Run(Status.Completed, Trigger.Manual, 1)])
            .ShouldBe(AssistantAcceptanceStatus.Passed);
        AssistantAcceptanceRules.Derive(true, [Run(Status.Completed, Trigger.Manual, 0), Run(Status.Completed, Trigger.KnowledgeChanged, 1, failed: 2)])
            .ShouldBe(AssistantAcceptanceStatus.Failed);
    }

    [Theory]
    [InlineData(Status.Queued)]
    [InlineData(Status.Running)]
    [InlineData(Status.Failed)]
    public void A_later_automatic_run_not_completed_makes_it_outdated(Status status)
    {
        AssistantAcceptanceRules.Derive(true, [Run(Status.Completed, Trigger.Manual, 0), Run(status, Trigger.AssistantChanged, 1)])
            .ShouldBe(AssistantAcceptanceStatus.Outdated);
    }

    [Fact]
    public void An_automatic_rerun_asked_of_a_later_manual_run_makes_it_outdated_but_a_manual_run_alone_does_not()
    {
        AssistantAcceptanceRules.Derive(true, [Run(Status.Completed, Trigger.Manual, 0), Run(Status.Running, Trigger.Manual, 1)])
            .ShouldBe(AssistantAcceptanceStatus.Passed);
        AssistantAcceptanceRules.Derive(
                true,
                [Run(Status.Completed, Trigger.Manual, 0), Run(Status.Running, Trigger.Manual, 1, rerunTrigger: Trigger.KnowledgeChanged)])
            .ShouldBe(AssistantAcceptanceStatus.Outdated);
    }

    [Fact]
    public void An_automatic_run_before_the_latest_completed_one_is_covered_by_it()
    {
        AssistantAcceptanceRules.Derive(true, [Run(Status.Failed, Trigger.KnowledgeChanged, 0), Run(Status.Completed, Trigger.Manual, 1, failed: 1)])
            .ShouldBe(AssistantAcceptanceStatus.Failed);
    }

    [Fact]
    public void Wire_names_are_the_plans_four_states()
    {
        WireNames<AssistantAcceptanceStatus>.All.ShouldBe(["not-accepted", "passed", "failed", "outdated"]);
    }
}
