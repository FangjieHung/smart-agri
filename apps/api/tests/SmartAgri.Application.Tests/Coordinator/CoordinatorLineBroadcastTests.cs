using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// LINE 群發's pure parts (#376, design Q14): the estimate from LINE's quota, consumption and friend count, the card's
/// note, the day whose friend count is asked for (UTC+9), the messages sent and how LINE's failures are reported.
/// </summary>
public class CoordinatorLineBroadcastTests
{
    private static readonly LineFollowersInsight Ready = new("ready", 160, 150, 10);

    [Fact]
    public void The_estimate_is_friends_minus_blocks_against_the_months_limit_minus_what_was_sent()
    {
        var estimate = LineBroadcastEstimate.Of(new LineMessageQuota(true, 200), new LineQuotaConsumption(30), Ready);

        (estimate.Remaining, estimate.Recipients, estimate.Insufficient).ShouldBe(((long?)170, (long?)150, false));
        LineBroadcastEstimate.Of(new LineMessageQuota(true, 200), new LineQuotaConsumption(260), Ready).Remaining.ShouldBe((long?)0, "LINE's count is approximate");
        LineBroadcastEstimate.Of(new LineMessageQuota(false, null), null, Ready).Remaining.ShouldBeNull();
        LineBroadcastEstimate.Of(new LineMessageQuota(true, 200), new LineQuotaConsumption(0), new LineFollowersInsight("unready", null, null, null))
            .Recipients.ShouldBeNull();
        LineBroadcastEstimate.Of(new LineMessageQuota(true, 200), new LineQuotaConsumption(0), new LineFollowersInsight("ready", 5, 0, 9))
            .Recipients.ShouldBe((long?)0);
        Should.Throw<ArgumentNullException>(() => LineBroadcastEstimate.Of(new LineMessageQuota(true, 200), null, Ready));
    }

    [Fact]
    public void The_note_says_how_many_it_uses_and_how_many_are_left_or_that_it_cannot_be_sent()
    {
        CoordinatorLineBroadcast.Note(new LineBroadcastEstimate(170, 160, 10)).ShouldBe("約 150 則（好友 160－封鎖 10），本月剩 170 則");
        CoordinatorLineBroadcast.Note(new LineBroadcastEstimate(150, 160, 10)).ShouldBe("約 150 則（好友 160－封鎖 10），本月剩 150 則", "exactly enough");
        CoordinatorLineBroadcast.Note(new LineBroadcastEstimate(null, 1200, 30)).ShouldBe("約 1170 則（好友 1200－封鎖 30），本月不限則數");
        CoordinatorLineBroadcast.Note(new LineBroadcastEstimate(170, null, null)).ShouldBe("則數無法估計（LINE 尚未提供好友數），本月剩 170 則");
        CoordinatorLineBroadcast.Note(new LineBroadcastEstimate(null, null, null)).ShouldBe("則數無法估計（LINE 尚未提供好友數），本月不限則數");
        CoordinatorLineBroadcast.Note(new LineBroadcastEstimate(100, 160, 10)).ShouldBe("額度不足，無法群發：約 150 則（好友 160－封鎖 10），本月剩 100 則");
        CoordinatorLineBroadcast.Note(new LineBroadcastEstimate(0, null, null)).ShouldBe("額度不足，無法群發：則數無法估計（LINE 尚未提供好友數），本月剩 0 則");

        new LineBroadcastEstimate(170, null, null).Insufficient.ShouldBeFalse("an unknown estimate with quota left is allowed");
        new LineBroadcastEstimate(0, null, null).Insufficient.ShouldBeTrue("nothing left");
        new LineBroadcastEstimate(null, 9999, 0).Insufficient.ShouldBeFalse("no limit");
    }

    [Theory]
    [InlineData("2026-11-02T10:00:00+08:00", "2026-11-01")]
    [InlineData("2026-11-01T23:30:00+08:00", "2026-11-01", "00:30 in Tokyo is already 11/2")]
    [InlineData("2026-11-01T22:59:00+08:00", "2026-10-31")]
    [InlineData("2026-11-01T00:00:00+00:00", "2026-10-31")]
    public void The_friend_count_is_asked_for_yesterday_in_UTC_plus_9(string now, string expected, string? because = null)
    {
        CoordinatorLineBroadcast.FollowersDate(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), because);
    }

    [Fact]
    public void A_broadcast_is_the_announcement_and_its_image_as_original_and_preview()
    {
        var messages = CoordinatorLineBroadcast.Messages("【王記麵店】今天臨時店休。", "https://example.com/closed.jpg");

        messages.Count.ShouldBe(2);
        (messages[0]["type"]!.GetValue<string>(), messages[0]["text"]!.GetValue<string>()).ShouldBe(("text", "【王記麵店】今天臨時店休。"));
        messages[1].ToJsonString().ShouldBe(
            """{"type":"image","originalContentUrl":"https://example.com/closed.jpg","previewImageUrl":"https://example.com/closed.jpg"}""");
        CoordinatorLineBroadcast.Messages("更正：照常營業。", null).ShouldHaveSingleItem()["type"]!.GetValue<string>().ShouldBe("text");
        Should.Throw<ArgumentException>(() => CoordinatorLineBroadcast.Messages(" ", null));
    }

    [Fact]
    public void LINEs_failures_have_their_codes_and_LINEs_409_and_monthly_limit_are_recognized()
    {
        static LineApiResult Result(LineApiOutcome outcome, int? status, string? message = null) => new(outcome, status, "req", message);

        CoordinatorLineBroadcast.FailureCodeOf(Result(LineApiOutcome.RateLimited, 429, "You have reached your monthly limit."))
            .ShouldBe(CoordinatorFailureCodes.QuotaExhausted);
        CoordinatorLineBroadcast.FailureCodeOf(Result(LineApiOutcome.RateLimited, 429, "Exceeded the target limit for sending messages this month."))
            .ShouldBe(CoordinatorFailureCodes.QuotaExhausted);
        CoordinatorLineBroadcast.FailureCodeOf(Result(LineApiOutcome.RateLimited, 429, "The API rate limit has been exceeded. Try again later."))
            .ShouldBe(CoordinatorFailureCodes.RateLimited);
        CoordinatorLineBroadcast.FailureCodeOf(Result(LineApiOutcome.HttpError, 500)).ShouldBe(CoordinatorFailureCodes.LineUnavailable);
        CoordinatorLineBroadcast.FailureCodeOf(Result(LineApiOutcome.TimedOut, null)).ShouldBe(CoordinatorFailureCodes.LineUnavailable);
        CoordinatorLineBroadcast.FailureCodeOf(Result(LineApiOutcome.NetworkError, null)).ShouldBe(CoordinatorFailureCodes.LineUnavailable);
        CoordinatorLineBroadcast.FailureCodeOf(Result(LineApiOutcome.HttpError, 200, "Unexpected response body.")).ShouldBe(CoordinatorFailureCodes.LineUnavailable);
        CoordinatorLineBroadcast.FailureCodeOf(Result(LineApiOutcome.HttpError, 400, "The request body has 1 error(s)")).ShouldBe(CoordinatorFailureCodes.Rejected);
        Should.Throw<ArgumentException>(() => CoordinatorLineBroadcast.FailureCodeOf(Result(LineApiOutcome.Unauthorized, 401)));
        Should.Throw<ArgumentException>(() => CoordinatorLineBroadcast.FailureCodeOf(Result(LineApiOutcome.Succeeded, 200)));

        Result(LineApiOutcome.HttpError, 409, "The retry key is already accepted").IsRetryKeyAccepted.ShouldBeTrue();
        Result(LineApiOutcome.HttpError, 400).IsRetryKeyAccepted.ShouldBeFalse();
        Result(LineApiOutcome.RateLimited, 429, null).IsMonthlyLimit.ShouldBeFalse();
    }
}
