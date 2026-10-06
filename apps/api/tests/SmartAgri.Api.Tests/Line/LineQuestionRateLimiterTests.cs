using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Line;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Tests.Authentication;

namespace SmartAgri.Api.Tests.Line;

/// <summary>The LINE question rate limits (M5b plan §3 F, decision E, #232) on their own: which
/// partition refuses, and that each partition is told once per window.</summary>
public class LineQuestionRateLimiterTests
{
    private static readonly Guid Assistant = Guid.NewGuid();

    [Fact]
    public void A_user_over_the_minute_limit_is_refused_and_told_once_while_another_user_passes()
    {
        var clock = new TestClock();
        using var limiter = Limiter(clock, limits => limits.LineQuestionsPerUserPerMinute = 2);

        Acquire(limiter, "Ua", oneToOne: true).Acquired.ShouldBeTrue();
        Acquire(limiter, "Ua", oneToOne: true).Acquired.ShouldBeTrue();
        var first = Acquire(limiter, "Ua", oneToOne: true);
        var second = Acquire(limiter, "Ua", oneToOne: true);

        first.RefusedBy.ShouldBe(LineQuestionRateLimit.UserPerMinute);
        first.Notify.ShouldBeTrue();
        second.RefusedBy.ShouldBe(LineQuestionRateLimit.UserPerMinute);
        second.Notify.ShouldBeFalse("one notice per partition and window");
        Acquire(limiter, "Ub", oneToOne: true).Acquired.ShouldBeTrue("another user");

        // A minute later on the notice's clock (the sliding window counts real time, so it still
        // refuses), the refusal is told again.
        clock.Advance(TimeSpan.FromSeconds(61));
        var later = Acquire(limiter, "Ua", oneToOne: true);
        later.RefusedBy.ShouldBe(LineQuestionRateLimit.UserPerMinute);
        later.Notify.ShouldBeTrue();
    }

    [Fact]
    public void The_hour_layer_refuses_when_the_minute_layer_does_not()
    {
        using var limiter = Limiter(new TestClock(), limits => limits.LineQuestionsPerUserPerHour = 1);

        Acquire(limiter, "Ua", oneToOne: true).Acquired.ShouldBeTrue();
        var refused = Acquire(limiter, "Ua", oneToOne: true);

        refused.RefusedBy.ShouldBe(LineQuestionRateLimit.UserPerHour);
        refused.Notify.ShouldBeTrue();
    }

    [Fact]
    public void A_group_is_limited_as_a_whole_and_not_by_the_user_limits()
    {
        using var limiter = Limiter(new TestClock(), limits =>
        {
            limits.LineQuestionsPerUserPerMinute = 1;
            limits.LineQuestionsPerGroupPerMinute = 2;
        });

        Acquire(limiter, "Cgroup", oneToOne: false).Acquired.ShouldBeTrue();
        Acquire(limiter, "Cgroup", oneToOne: false).Acquired.ShouldBeTrue("the user limit of 1 does not apply to a group");
        var refused = Acquire(limiter, "Cgroup", oneToOne: false);

        refused.RefusedBy.ShouldBe(LineQuestionRateLimit.GroupPerMinute);
        refused.Notify.ShouldBeTrue();
        Acquire(limiter, "Cother", oneToOne: false).Acquired.ShouldBeTrue("another group");
    }

    [Fact]
    public void The_assistant_limit_spans_chats_and_is_told_once_for_the_whole_assistant()
    {
        using var limiter = Limiter(new TestClock(), limits => limits.LineQuestionsPerAssistantPerMinute = 2);

        Acquire(limiter, "Ua", oneToOne: true).Acquired.ShouldBeTrue();
        Acquire(limiter, "Cgroup", oneToOne: false).Acquired.ShouldBeTrue();
        var first = Acquire(limiter, "Ub", oneToOne: true);
        var second = Acquire(limiter, "Uc", oneToOne: true);

        first.RefusedBy.ShouldBe(LineQuestionRateLimit.AssistantPerMinute);
        first.Notify.ShouldBeTrue();
        second.RefusedBy.ShouldBe(LineQuestionRateLimit.AssistantPerMinute);
        second.Notify.ShouldBeFalse();
        limiter.TryAcquire(Guid.NewGuid(), "Ua", oneToOne: true).Acquired.ShouldBeTrue("another assistant");
    }

    [Fact]
    public void The_concurrency_permit_is_held_until_the_answer_is_sent()
    {
        using var limiter = Limiter(new TestClock(), limits => limits.LineMaxConcurrentQuestionsPerAssistant = 1);

        var answering = Acquire(limiter, "Ua", oneToOne: true);
        answering.Acquired.ShouldBeTrue();
        var refused = Acquire(limiter, "Ub", oneToOne: true);
        refused.RefusedBy.ShouldBe(LineQuestionRateLimit.AssistantConcurrency);
        refused.Notify.ShouldBeTrue();
        Acquire(limiter, "Uc", oneToOne: true).Notify.ShouldBeFalse();

        answering.Dispose();
        using var next = Acquire(limiter, "Ub", oneToOne: true);
        next.Acquired.ShouldBeTrue();
    }

    private static LineQuestionPermit Acquire(LineQuestionRateLimiter limiter, string chatId, bool oneToOne) =>
        limiter.TryAcquire(Assistant, chatId, oneToOne);

    private static LineQuestionRateLimiter Limiter(TimeProvider clock, Action<PublicRateLimitOptions> configure)
    {
        var options = new PublicChannelsOptions();
        configure(options.RateLimits);
        return new LineQuestionRateLimiter(Options.Create(options), clock);
    }
}
