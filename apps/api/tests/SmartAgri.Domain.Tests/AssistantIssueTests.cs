using Shouldly;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Domain.Tests;

/// <summary><see cref="AssistantIssue"/>'s state machine and history numbering (issue #126).</summary>
public class AssistantIssueTests
{
    private static readonly Guid OrganizationId = Guid.CreateVersion7();
    private static readonly Guid AssistantId = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Handler = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Opening_from_a_failed_result_snapshots_it_and_numbers_the_created_event_1()
    {
        var (run, result) = Result(passed: false, question: "第一行\n第二行");

        var (issue, created) = AssistantIssue.OpenFromTestFailure(AssistantId, run, result, "  ", Owner, Handler, null, Now);

        (issue.Source, issue.Status, issue.Title).ShouldBe((AssistantIssueSource.TestFailure, AssistantIssueStatus.Open, "第一行 第二行"));
        (issue.TestRunId, issue.TestResultId, issue.TestFailureReason).ShouldBe((run.Id, result.Id, AssistantTestFailureReason.KindMismatch));
        (issue.QuestionSnapshot, issue.AnswerSnapshot).ShouldBe(("第一行\n第二行", "答案"));
        (created.Ordinal, created.Action, created.AssigneeAccountId, created.Status)
            .ShouldBe((1, AssistantIssueEventAction.Created, Handler, AssistantIssueStatus.Open));
        issue.EventCount.ShouldBe(1);
    }

    [Fact]
    public void A_passed_result_or_one_of_another_assistant_cannot_open_an_issue()
    {
        var (passedRun, passed) = Result(passed: true);
        Should.Throw<ArgumentException>(() =>
            AssistantIssue.OpenFromTestFailure(AssistantId, passedRun, passed, null, Owner, null, null, Now));

        var (run, failed) = Result(passed: false);
        Should.Throw<ArgumentException>(() =>
            AssistantIssue.OpenFromTestFailure(Guid.CreateVersion7(), run, failed, null, Owner, null, null, Now));
    }

    [Fact]
    public void Status_changes_record_events_resolve_and_reopen_and_a_no_op_records_nothing()
    {
        var (run, result) = Result(passed: false);
        var (issue, _) = AssistantIssue.OpenFromTestFailure(AssistantId, run, result, "標題", Owner, null, null, Now);

        issue.ChangeStatus(AssistantIssueStatus.Open, null, Owner, Now).ShouldBeNull();
        issue.Assign(null, Owner, Now).ShouldBeNull();
        issue.SetDueAt(null, Owner, Now).ShouldBeNull();

        var assigned = issue.Assign(Handler, Owner, Now)!;
        var resolved = issue.ChangeStatus(AssistantIssueStatus.Resolved, " 已修正 ", Handler, Now.AddHours(2))!;
        (issue.ResolvedAt, issue.ResolutionNote).ShouldBe((Now.AddHours(2), "已修正"));
        var comment = issue.Comment("補充", Handler, Now.AddHours(3));
        var reopened = issue.ChangeStatus(AssistantIssueStatus.InProgress, null, Owner, Now.AddHours(4))!;

        new[] { assigned.Ordinal, resolved.Ordinal, comment.Ordinal, reopened.Ordinal }.ShouldBe([2, 3, 4, 5]);
        (resolved.Status, resolved.Note).ShouldBe((AssistantIssueStatus.Resolved, "已修正"));
        (issue.Status, issue.ResolvedAt, issue.ResolutionNote).ShouldBe((AssistantIssueStatus.InProgress, null, null));
        issue.UpdatedAt.ShouldBe(Now.AddHours(4));
        Should.Throw<ArgumentException>(() => issue.Comment(" ", Owner, Now));
    }

    [Fact]
    public void Wire_names_are_kebab_case()
    {
        WireNames<AssistantIssueSource>.All.ShouldBe(["test-failure", "handoff"]);
        WireNames<AssistantIssueStatus>.All.ShouldBe(["open", "in-progress", "resolved"]);
        WireNames<AssistantIssueEventAction>.All.ShouldBe(["created", "assigned", "status-changed", "commented", "due-date-changed"]);
    }

    private static (AssistantTestRun Run, AssistantTestResult Result) Result(bool passed, string question = "問題")
    {
        var testCase = new AssistantTestCase(
            OrganizationId, AssistantId, question, AssistantTestCaseCategory.ShouldRefuse, AssistantTestExpectedKind.NoResult,
            [], null, 1, Now);
        var run = AssistantTestRun.Queue(OrganizationId, AssistantId, AssistantTestRunTrigger.Manual, Now);
        run.Start("v1", "model", 0.3, Now);
        run.Complete(passed ? 1 : 0, passed ? 0 : 1, Now);
        var result = AssistantTestResult.Record(
            run, testCase, AnswerReplyKind.CompanyData, "答案", [], null, 0.9,
            passed ? null : AssistantTestFailureReason.KindMismatch);
        return (run, result);
    }
}
