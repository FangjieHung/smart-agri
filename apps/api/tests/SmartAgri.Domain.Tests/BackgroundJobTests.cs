using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Jobs;

namespace SmartAgri.Domain.Tests;

/// <summary>Enqueueing and the guards on job state changes. Claiming and the transitions
/// after it are SQL and runner behaviour, covered by <c>SmartAgri.Api.Tests.Jobs</c>.</summary>
public class BackgroundJobTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_job_is_queued_for_now_with_its_payload_as_web_json()
    {
        var job = BackgroundJob.Create(Organization, "knowledge.process-document", new { DocumentVersionId = Guid.Empty }, Now);

        job.Id.ShouldNotBe(Guid.Empty);
        job.OrganizationId.ShouldBe(Organization);
        job.Kind.ShouldBe("knowledge.process-document");
        job.Payload.ShouldBe("""{"documentVersionId":"00000000-0000-0000-0000-000000000000"}""");
        job.Status.ShouldBe(BackgroundJobStatus.Queued);
        job.Attempts.ShouldBe(0);
        job.MaxAttempts.ShouldBe(BackgroundJob.DefaultMaxAttempts);
        job.RunAfter.ShouldBe(Now);
        job.LockedUntil.ShouldBeNull();
        job.LastError.ShouldBeNull();
        job.CreatedAt.ShouldBe(Now);
        job.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public void A_job_can_be_scheduled_for_later_with_its_own_attempt_limit()
    {
        var job = BackgroundJob.Create(Organization, "reports.weekly", new { }, Now, Now.AddHours(1), maxAttempts: 3);

        job.RunAfter.ShouldBe(Now.AddHours(1));
        job.MaxAttempts.ShouldBe(3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Knowledge.process")]
    [InlineData("knowledge process")]
    [InlineData(".knowledge")]
    [InlineData("knowledge.")]
    [InlineData("knowledge..process")]
    [InlineData("knowledge_process")]
    public void Kinds_are_lower_case_words_separated_by_dots_or_dashes(string kind)
    {
        Should.Throw<ArgumentException>(() => BackgroundJob.Create(Organization, kind, new { }, Now));
    }

    [Fact]
    public void Invalid_organizations_attempt_limits_and_payloads_are_rejected()
    {
        Should.Throw<ArgumentException>(() => BackgroundJob.Create(Guid.Empty, "a", new { }, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => BackgroundJob.Create(Organization, "a", new { }, Now, maxAttempts: 0));
        Should.Throw<ArgumentOutOfRangeException>(
            () => BackgroundJob.Create(Organization, "a", new { }, Now, maxAttempts: BackgroundJob.MaxAttemptsLimit + 1));
        Should.Throw<ArgumentException>(() => BackgroundJob.Create(Organization, "a", JsonDocument.Parse("null").RootElement, Now));
        Should.Throw<ArgumentException>(() => BackgroundJob.Create(Organization, new string('a', BackgroundJob.KindMaxLength + 1), new { }, Now));
    }

    [Fact]
    public void Only_a_running_job_can_succeed_fail_retry_or_be_released()
    {
        var job = BackgroundJob.Create(Organization, "a", new { }, Now);

        Should.Throw<InvalidOperationException>(() => job.Succeed(Now));
        Should.Throw<InvalidOperationException>(() => job.Fail("error", Now));
        Should.Throw<InvalidOperationException>(() => job.ScheduleRetry("error", Now));
        Should.Throw<InvalidOperationException>(() => job.Release(Now));
    }

    [Fact]
    public void Statuses_are_stored_by_wire_name()
    {
        // JobClaimer's SQL compares against these names.
        WireNames<BackgroundJobStatus>.All.ShouldBe(["queued", "running", "succeeded", "failed"]);
    }
}
