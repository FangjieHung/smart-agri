using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Tests.Line;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>At most 5 failed binding attempts per LINE user per 15 minutes (#372).</summary>
public class CoordinatorBindingThrottleTests
{
    private const string User = "Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly ManualClock _clock = new();

    [Fact]
    public void Five_failures_block_until_the_oldest_is_fifteen_minutes_old()
    {
        var throttle = new CoordinatorBindingThrottle(_clock);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            throttle.IsBlocked(User).ShouldBeFalse($"before failure {attempt}");
            throttle.RecordFailure(User);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        throttle.IsBlocked(User).ShouldBeTrue();

        // The first failure was at 0:00; it is 5:00 now. At 15:00 it no longer counts.
        _clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        throttle.IsBlocked(User).ShouldBeTrue();
        _clock.Advance(TimeSpan.FromSeconds(1));
        throttle.IsBlocked(User).ShouldBeFalse();

        // One more failure fills the window again (four are still within it).
        throttle.RecordFailure(User);
        throttle.IsBlocked(User).ShouldBeTrue();
    }

    [Fact]
    public void Users_are_counted_apart()
    {
        var throttle = new CoordinatorBindingThrottle(_clock);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            throttle.RecordFailure(User);
        }

        throttle.IsBlocked(User).ShouldBeTrue();
        throttle.IsBlocked("Ubbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").ShouldBeFalse();
    }

    [Fact]
    public void The_limits_are_five_per_fifteen_minutes()
    {
        CoordinatorBindingThrottle.MaxFailures.ShouldBe(5);
        CoordinatorBindingThrottle.Window.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Expired_users_are_swept_when_many_are_remembered()
    {
        var throttle = new CoordinatorBindingThrottle(_clock);
        for (var user = 0; user < CoordinatorBindingThrottle.SweepThreshold; user++)
        {
            throttle.RecordFailure($"U{user}");
        }

        _clock.Advance(CoordinatorBindingThrottle.Window);
        throttle.RecordFailure(User);

        throttle.IsBlocked("U0").ShouldBeFalse();
        throttle.IsBlocked(User).ShouldBeFalse();
    }
}
