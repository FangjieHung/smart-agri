using Shouldly;
using SmartAgri.Application.Line;

namespace SmartAgri.Application.Tests.Line;

/// <summary>Deduplicating webhook events by <c>webhookEventId</c> for 10 minutes (M5b plan §3 C; #231).</summary>
public class LineWebhookDeduplicatorTests
{
    private static readonly Guid Assistant = Guid.Parse("01a10194-0000-7000-8000-00000000000a");

    private readonly ManualClock _clock = new();

    [Fact]
    public void An_id_is_accepted_once_within_ten_minutes_then_again()
    {
        var deduplicator = new LineWebhookDeduplicator(_clock);

        deduplicator.TryAccept(Assistant, "01JA").ShouldBeTrue();
        deduplicator.TryAccept(Assistant, "01JA").ShouldBeFalse();
        _clock.Advance(TimeSpan.FromMinutes(9));
        deduplicator.TryAccept(Assistant, "01JA").ShouldBeFalse();

        _clock.Advance(TimeSpan.FromMinutes(1));
        deduplicator.TryAccept(Assistant, "01JA").ShouldBeTrue();
    }

    [Fact]
    public void Ids_are_per_assistant_and_a_released_id_is_accepted_again()
    {
        var deduplicator = new LineWebhookDeduplicator(_clock);

        deduplicator.TryAccept(Assistant, "01JA").ShouldBeTrue();
        deduplicator.TryAccept(Guid.NewGuid(), "01JA").ShouldBeTrue();

        deduplicator.Release(Assistant, "01JA");
        deduplicator.TryAccept(Assistant, "01JA").ShouldBeTrue();
        deduplicator.TryAccept(Assistant, "01JA").ShouldBeFalse();
    }

    [Fact]
    public void The_retention_is_ten_minutes()
    {
        LineWebhookDeduplicator.Retention.ShouldBe(TimeSpan.FromMinutes(10));
    }
}
