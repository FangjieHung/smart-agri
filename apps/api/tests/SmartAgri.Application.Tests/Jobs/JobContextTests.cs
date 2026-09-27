using Shouldly;
using SmartAgri.Application.Jobs;

namespace SmartAgri.Application.Tests.Jobs;

public class JobContextTests
{
    private sealed record DocumentPayload(Guid DocumentVersionId);

    [Fact]
    public void The_payload_reads_back_as_enqueued()
    {
        var id = Guid.CreateVersion7();

        Context($$"""{"documentVersionId":"{{id}}"}""").ReadPayload<DocumentPayload>().DocumentVersionId.ShouldBe(id);
    }

    [Theory]
    [InlineData("""{"documentVersionId":"not a guid"}""")]
    [InlineData("null")]
    [InlineData("{")]
    public void An_unreadable_payload_is_a_permanent_failure(string payload)
    {
        Should.Throw<PermanentJobFailure>(() => Context(payload).ReadPayload<DocumentPayload>());
    }

    [Fact]
    public void The_last_attempt_is_the_one_at_the_limit()
    {
        (Context("{}") with { Attempt = 2, MaxAttempts = 3 }).IsLastAttempt.ShouldBeFalse();
        (Context("{}") with { Attempt = 3, MaxAttempts = 3 }).IsLastAttempt.ShouldBeTrue();
    }

    private static JobContext Context(string payload) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "knowledge.process-document", payload, Attempt: 1, MaxAttempts: 3);
}
