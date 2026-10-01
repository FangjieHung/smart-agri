using Shouldly;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Domain.Tests;

public sealed class AssistantTestRunTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_queued_run_starts_completes_and_records_what_it_ran_with()
    {
        var run = AssistantTestRun.Queue(Guid.NewGuid(), Guid.NewGuid(), AssistantTestRunTrigger.Manual, Now);
        (run.Status, run.IsActive, run.RerunRequested, run.StartedAt).ShouldBe((AssistantTestRunStatus.Queued, true, false, (DateTimeOffset?)null));

        run.Start("prompt/1", "fake-chat", 0.3, Now.AddSeconds(1));
        (run.Status, run.PromptVersion, run.Model, run.MinScore).ShouldBe((AssistantTestRunStatus.Running, "prompt/1", "fake-chat", (double?)0.3));

        run.Complete(passedCount: 2, failedCount: 1, Now.AddSeconds(2));
        (run.Status, run.IsActive, run.PassedCount, run.FailedCount, run.CompletedAt)
            .ShouldBe((AssistantTestRunStatus.Completed, false, 2, 1, (DateTimeOffset?)Now.AddSeconds(2)));
    }

    [Fact]
    public void A_rerun_asked_for_while_queued_is_satisfied_by_starting_but_one_asked_for_while_running_is_kept()
    {
        var run = AssistantTestRun.Queue(Guid.NewGuid(), Guid.NewGuid(), AssistantTestRunTrigger.Manual, Now);
        run.RequestRerun(AssistantTestRunTrigger.KnowledgeChanged);
        (run.RerunRequested, run.RerunTrigger).ShouldBe((true, (AssistantTestRunTrigger?)AssistantTestRunTrigger.KnowledgeChanged));

        run.Start("prompt/1", "fake-chat", 0.3, Now);
        (run.RerunRequested, run.RerunTrigger).ShouldBe((false, (AssistantTestRunTrigger?)null));

        run.RequestRerun(AssistantTestRunTrigger.AssistantChanged);
        run.Complete(1, 0, Now);
        (run.RerunRequested, run.RerunTrigger, run.Trigger)
            .ShouldBe((true, (AssistantTestRunTrigger?)AssistantTestRunTrigger.AssistantChanged, AssistantTestRunTrigger.Manual));
    }

    [Theory]
    [InlineData(null, AssistantTestRunTrigger.Manual, AssistantTestRunTrigger.Manual)]
    [InlineData(null, AssistantTestRunTrigger.KnowledgeChanged, AssistantTestRunTrigger.KnowledgeChanged)]
    [InlineData(AssistantTestRunTrigger.Manual, AssistantTestRunTrigger.AssistantChanged, AssistantTestRunTrigger.AssistantChanged)]
    [InlineData(AssistantTestRunTrigger.KnowledgeChanged, AssistantTestRunTrigger.AssistantChanged, AssistantTestRunTrigger.AssistantChanged)]
    [InlineData(AssistantTestRunTrigger.AssistantChanged, AssistantTestRunTrigger.KnowledgeChanged, AssistantTestRunTrigger.KnowledgeChanged)]
    [InlineData(AssistantTestRunTrigger.KnowledgeChanged, AssistantTestRunTrigger.Manual, AssistantTestRunTrigger.KnowledgeChanged)]
    public void The_latest_rerun_trigger_wins_but_manual_never_replaces_an_automatic_one(
        AssistantTestRunTrigger? earlier, AssistantTestRunTrigger requested, AssistantTestRunTrigger expected)
    {
        AssistantTestRun.CombineTriggers(earlier, requested).ShouldBe(expected);
    }

    [Fact]
    public void Only_an_active_run_can_fail_or_be_asked_to_rerun_and_only_a_running_one_can_complete()
    {
        var run = AssistantTestRun.Queue(Guid.NewGuid(), Guid.NewGuid(), AssistantTestRunTrigger.Manual, Now);
        Should.Throw<InvalidOperationException>(() => run.Complete(0, 0, Now));

        run.Fail(Now);
        run.Status.ShouldBe(AssistantTestRunStatus.Failed);
        Should.Throw<InvalidOperationException>(() => run.Fail(Now));
        Should.Throw<InvalidOperationException>(() => run.RequestRerun(AssistantTestRunTrigger.Manual));
        Should.Throw<InvalidOperationException>(() => run.Start("prompt/1", "fake-chat", 0.3, Now));
    }

    [Fact]
    public void A_result_snapshots_its_test_case_and_passes_exactly_without_a_failure_reason()
    {
        var organizationId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var run = AssistantTestRun.Queue(organizationId, assistantId, AssistantTestRunTrigger.Manual, Now);
        var testCase = new AssistantTestCase(
            organizationId, assistantId, "退貨期限？", AssistantTestCaseCategory.Common, AssistantTestExpectedKind.CompanyData,
            [documentId], null, ordinal: 3, Now);

        var passed = AssistantTestResult.Record(
            run, testCase, AnswerReplyKind.CompanyData, "七天 [1]", [documentId, documentId], null, 0.5, null);
        (passed.RunId, passed.TestCaseId, passed.Ordinal, passed.QuestionSnapshot, passed.Passed)
            .ShouldBe((run.Id, testCase.Id, 3, "退貨期限？", true));
        passed.CitedDocumentIds.ShouldBe([documentId]);
        passed.ExpectedDocumentIds.ShouldBe([documentId]);

        var failed = AssistantTestResult.Record(
            run, testCase, AnswerReplyKind.NoResult, "查無資料", [], AnswerRejectionReason.BelowThreshold, null,
            AssistantTestFailureReason.KindMismatch);
        (failed.Passed, failed.FailureReason).ShouldBe((false, (AssistantTestFailureReason?)AssistantTestFailureReason.KindMismatch));

        var otherAssistantsCase = new AssistantTestCase(
            organizationId, Guid.NewGuid(), "問題？", AssistantTestCaseCategory.Common, AssistantTestExpectedKind.NoResult, [], null, 1, Now);
        Should.Throw<ArgumentException>(() => AssistantTestResult.Record(
            run, otherAssistantsCase, AnswerReplyKind.NoResult, "x", [], null, null, null));
    }

    [Fact]
    public void Wire_names_are_the_plans()
    {
        WireNames<AssistantTestRunTrigger>.All.ShouldBe(["manual", "knowledge-changed", "assistant-changed"]);
        WireNames<AssistantTestRunStatus>.All.ShouldBe(["queued", "running", "completed", "failed"]);
        WireNames<AssistantTestFailureReason>.All.ShouldBe(["kind-mismatch", "missing-document"]);
        WireNames<ModelInvocationPurpose>.ToWire(ModelInvocationPurpose.AssistantTest).ShouldBe("assistant-test");
    }
}
