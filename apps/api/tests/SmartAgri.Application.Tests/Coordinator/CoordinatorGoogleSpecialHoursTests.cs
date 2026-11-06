using System.Text.Json;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// Google 特殊營業時間's pure parts (#378, design Q21, Q22): the periods a command sets — from the shop's own weekly hours
/// (<see cref="CoordinatorTestShop"/>: 11:00–14:00 and 17:00–21:00, Saturday's dinner until 01:00) — and merging them
/// into a location's special hours without touching any other date.
/// </summary>
public class CoordinatorGoogleSpecialHoursTests
{
    private static readonly ShopSettingsDocument Shop = CoordinatorTestShop.Settings();

    private static readonly DateOnly Monday = new(2026, 11, 2);
    private static readonly DateOnly Saturday = new(2026, 11, 7);

    [Fact]
    public void A_closure_is_the_whole_day_closed()
    {
        CoordinatorGoogleSpecialHours.PeriodsFor(Shop, ShopScenario.TemporaryClosure, Monday, null)
            .ShouldBe([GoogleSpecialHourPeriod.ClosedOn(Monday)]);
    }

    [Fact]
    public void Closing_early_keeps_the_periods_before_the_time_and_cuts_the_one_running_then()
    {
        Periods(ShopScenario.CloseEarly, Monday, 19, 0).ShouldBe(["2026-11-02 11:00–14:00", "2026-11-02 17:00–19:00"]);
        Periods(ShopScenario.CloseEarly, Monday, 15, 30).ShouldBe(["2026-11-02 11:00–14:00"], "between lunch and dinner: lunch as usual");
        Periods(ShopScenario.CloseEarly, Monday, 12, 0).ShouldBe(["2026-11-02 11:00–12:00"]);
        Periods(ShopScenario.CloseEarly, Monday, 17, 0).ShouldBe(["2026-11-02 11:00–14:00"], "closing when dinner would open");

        // Saturday's overnight dinner: cut before midnight, or in its small hours (the next day's date).
        Periods(ShopScenario.CloseEarly, Saturday, 23, 0).ShouldBe(["2026-11-07 11:00–14:00", "2026-11-07 17:00–23:00"]);
        Periods(ShopScenario.CloseEarly, Saturday, 0, 30).ShouldBe(["2026-11-07 11:00–14:00", "2026-11-07 17:00–2026-11-08 00:30"]);
        Periods(ShopScenario.CloseEarly, Saturday, 0, 0).ShouldBe(["2026-11-07 11:00–14:00", "2026-11-07 17:00–24:00"], "midnight is 24:00 of the day");
    }

    [Fact]
    public void Opening_late_keeps_the_periods_after_the_time_and_starts_the_one_running_then_at_it()
    {
        Periods(ShopScenario.OpenLate, Monday, 12, 0).ShouldBe(["2026-11-02 12:00–14:00", "2026-11-02 17:00–21:00"]);
        Periods(ShopScenario.OpenLate, Monday, 16, 0).ShouldBe(["2026-11-02 17:00–21:00"], "dinner as usual");
        Periods(ShopScenario.OpenLate, Monday, 18, 0).ShouldBe(["2026-11-02 18:00–21:00"]);
        Periods(ShopScenario.OpenLate, Saturday, 18, 0).ShouldBe(["2026-11-07 18:00–2026-11-08 01:00"], "overnight: endDate the next day");

        // In the small hours only the next day would be open: that date's own hours are not touched, so the day is closed.
        Periods(ShopScenario.OpenLate, Saturday, 0, 30).ShouldBe(["2026-11-07 closed"]);
    }

    [Fact]
    public void The_times_come_from_the_shops_settings_not_from_google_and_a_day_the_settings_no_longer_open_is_closed()
    {
        // Wednesday is a regular day off: an early closing confirmed before the settings changed writes it closed.
        CoordinatorGoogleSpecialHours.PeriodsFor(Shop, ShopScenario.CloseEarly, new DateOnly(2026, 11, 4), new TimeOnly(19, 0))
            .ShouldBe([GoogleSpecialHourPeriod.ClosedOn(new DateOnly(2026, 11, 4))]);
        Should.Throw<ArgumentException>(() => CoordinatorGoogleSpecialHours.PeriodsFor(Shop, ShopScenario.CloseEarly, Monday, null));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorGoogleSpecialHours.PeriodsFor(Shop, ShopScenario.Reopen, Monday, null));
    }

    [Fact]
    public void Replacing_a_day_writes_every_other_date_back_exactly_as_google_sent_it()
    {
        const string christmas = """{"startDate": {"year": 2026, "month": 12, "day": 25}, "closed": true}""";
        const string midnight = """{"startDate":{"year":2026,"month":11,"day":20},"openTime":{},"closeTime":{"hours":24}, "futureField": "kept"}""";
        const string oldMonday = """{"startDate":{"year":2026,"month":11,"day":2},"openTime":{"hours":9},"closeTime":{"hours":12}}""";
        using var read = JsonDocument.Parse($$$"""{"specialHourPeriods": [{{{christmas}}}, {{{oldMonday}}}, {{{midnight}}}, {"startDate": {"year": 2026}}]}""");
        var ours = CoordinatorGoogleSpecialHours.PeriodsFor(Shop, ShopScenario.CloseEarly, Monday, new TimeOnly(19, 0));

        var patch = CoordinatorGoogleSpecialHours.Replace(read.RootElement, Monday, ours);

        (patch.Removed, patch.Kept).ShouldBe((1, 3));
        patch.Replaced.ShouldBe([oldMonday], "the day's entry as Google sent it, for the undo");
        patch.Body.ShouldBe(
            $$$"""{"specialHours":{"specialHourPeriods":[{{{christmas}}},{{{midnight}}},{"startDate": {"year": 2026}},""" +
            """{"startDate":{"year":2026,"month":11,"day":2},"openTime":{"hours":11,"minutes":0},"endDate":{"year":2026,"month":11,"day":2},"closeTime":{"hours":14,"minutes":0}},""" +
            """{"startDate":{"year":2026,"month":11,"day":2},"openTime":{"hours":17,"minutes":0},"endDate":{"year":2026,"month":11,"day":2},"closeTime":{"hours":19,"minutes":0}}]}}""");

        // A closure, from a location without special hours; then removing it again.
        var closed = CoordinatorGoogleSpecialHours.Replace(null, Monday, [GoogleSpecialHourPeriod.ClosedOn(Monday)]);
        closed.Body.ShouldBe("""{"specialHours":{"specialHourPeriods":[{"startDate":{"year":2026,"month":11,"day":2},"closed":true}]}}""");
        using var written = JsonDocument.Parse(closed.Body);
        var specialHours = written.RootElement.GetProperty("specialHours");
        CoordinatorGoogleSpecialHours.HasPeriodOn(specialHours, Monday).ShouldBeTrue();
        CoordinatorGoogleSpecialHours.HasPeriodOn(specialHours, Saturday).ShouldBeFalse();
        var cleared = CoordinatorGoogleSpecialHours.Replace(specialHours, Monday, []);
        (cleared.Body, cleared.Removed, cleared.Kept).ShouldBe(("{}", 1, 0), "nothing left: the masked field is cleared");

        // An undo puts the owner's own entry back, raw.
        CoordinatorGoogleSpecialHours.ReplaceRaw(specialHours, Monday, [oldMonday]).Body
            .ShouldBe($$$"""{"specialHours":{"specialHourPeriods":[{{{oldMonday}}}]}}""");
        CoordinatorGoogleSpecialHours.PeriodsOn(read.RootElement, Monday).ShouldBe([oldMonday]);
        CoordinatorGoogleSpecialHours.Replace(read.RootElement, Monday, []).Body.ShouldBe(
            $$$"""{"specialHours":{"specialHourPeriods":[{{{christmas}}},{{{midnight}}},{"startDate": {"year": 2026}}]}}""");
    }

    [Fact]
    public void A_location_has_regular_hours_only_with_a_period_and_the_external_ref_round_trips()
    {
        using var hours = JsonDocument.Parse("""{"some":{"periods":[{"openDay":"MONDAY","openTime":{"hours":11},"closeDay":"MONDAY","closeTime":{"hours":21}}]},"none":{"periods":[]},"empty":{}}""");
        CoordinatorGoogleSpecialHours.HasRegularHours(hours.RootElement.GetProperty("some")).ShouldBeTrue();
        CoordinatorGoogleSpecialHours.HasRegularHours(hours.RootElement.GetProperty("none")).ShouldBeFalse();
        CoordinatorGoogleSpecialHours.HasRegularHours(hours.RootElement.GetProperty("empty")).ShouldBeFalse();
        CoordinatorGoogleSpecialHours.HasRegularHours(null).ShouldBeFalse();

        // Nothing replaced: the date and the location.
        var reference = CoordinatorGoogleSpecialHours.ExternalRef(Saturday, "locations/1234567890", [], out var kept);
        (reference, kept).ShouldBe(("2026-11-07|locations/1234567890", true));
        CoordinatorGoogleSpecialHours.TryReadExternalRef(reference, out var applied).ShouldBeTrue();
        (applied.Date, applied.Location, applied.Previous.Count).ShouldBe((Saturday, "locations/1234567890", 0));

        // The owner's entries of the day: as Google sent them when they fit, without white space when only that fits,
        // else left out (the undo then removes the day).
        const string spaced = """{"startDate": {"year": 2026, "month": 11, "day": 7}, "openTime": {"hours": 9}, "closeTime": {"hours": 12}}""";
        reference = CoordinatorGoogleSpecialHours.ExternalRef(Saturday, "locations/1234567890", [spaced], out kept);
        (reference, kept).ShouldBe(($"2026-11-07|locations/1234567890|[{spaced}]", true));
        CoordinatorGoogleSpecialHours.TryReadExternalRef(reference, out applied).ShouldBeTrue();
        applied.Previous.ShouldBe([spaced]);

        var padded = """{"startDate": {"year": 2026, "month": 11, "day": 7},""" + new string(' ', 120) + """ "closed": true}""";
        reference = CoordinatorGoogleSpecialHours.ExternalRef(Saturday, "locations/1234567890", [padded, padded], out kept);
        (reference, kept).ShouldBe(("""2026-11-07|locations/1234567890|[{"startDate":{"year":2026,"month":11,"day":7},"closed":true},{"startDate":{"year":2026,"month":11,"day":7},"closed":true}]""", true));
        reference.Length.ShouldBeLessThanOrEqualTo(CoordinatorCommandAction.ExternalRefMaxLength);

        var many = Enumerable.Repeat(spaced, 3).ToList();
        reference = CoordinatorGoogleSpecialHours.ExternalRef(Saturday, "locations/1234567890", many, out kept);
        (reference, kept).ShouldBe(("2026-11-07|locations/1234567890", false));

        foreach (var bad in new[]
                 {
                     null, "", "post-9", "2026-13-01|locations/1", "2026-11-07|", "2026-11-07|locations/1|x", "2026-11-07|has space",
                     """2026-11-07|locations/1|{"startDate":{"year":2026,"month":11,"day":7},"closed":true}""",
                     """2026-11-07|locations/1|[{"startDate":{"year":2026,"month":11,"day":8},"closed":true}]""",
                 })
        {
            CoordinatorGoogleSpecialHours.TryReadExternalRef(bad, out _).ShouldBeFalse(bad ?? "null");
        }
    }

    [Fact]
    public void Periods_are_compared_as_Google_means_them()
    {
        var ours = CoordinatorGoogleSpecialHours.PeriodsFor(Shop, ShopScenario.CloseEarly, Monday, new TimeOnly(19, 0));
        string[] google =
        [
            """{"startDate":{"year":2026,"month":11,"day":2},"openTime":{"hours":17},"closeTime":{"hours":19}}""",
            """{"startDate":{"year":2026,"month":11,"day":2},"openTime":{"hours":11},"endDate":{"year":2026,"month":11,"day":2},"closeTime":{"hours":14}}""",
        ];
        CoordinatorGoogleSpecialHours.SameAs(google, ours).ShouldBeTrue("00:00 left out, no endDate, another order");
        CoordinatorGoogleSpecialHours.SameAs([.. ours.Select(CoordinatorGoogleSpecialHours.Json)], ours).ShouldBeTrue();
        CoordinatorGoogleSpecialHours.SameAs(google[..1], ours).ShouldBeFalse();
        CoordinatorGoogleSpecialHours.SameAs(
            ["""{"startDate":{"year":2026,"month":11,"day":2},"closed":true,"openTime":{"hours":9}}"""],
            [GoogleSpecialHourPeriod.ClosedOn(Monday)]).ShouldBeTrue("a closed day's times are ignored");
        CoordinatorGoogleSpecialHours.SameAs([], Array.Empty<GoogleSpecialHourPeriod>()).ShouldBeTrue();
        CoordinatorGoogleSpecialHours.SameAs(["""{"startDate":{"year":2026,"month":11,"day":2},"openTime":{"hours":9}}"""], ours).ShouldBeFalse("unreadable");
        CoordinatorGoogleSpecialHours.SameAs(google, [.. Enumerable.Reverse(google)]).ShouldBeTrue();
        CoordinatorGoogleSpecialHours.SameAs(["""{"odd":1}"""], ["""{"odd":1}"""]).ShouldBeTrue("unreadable ones match as text");
        CoordinatorGoogleSpecialHours.SameAs(["""{"odd":1}"""], ["""{"odd":2}"""]).ShouldBeFalse();
    }

    [Fact]
    public void Google_failures_are_explained()
    {
        CoordinatorFailureCodes.Reason(CoordinatorFailureCodes.GoogleNoRegularHours).ShouldBe("Google 商家沒有設定一般營業時間，無法自動設定特殊營業時間，先以提醒處理");
        CoordinatorFailureCodes.Reason(CoordinatorFailureCodes.GoogleAccessPending).ShouldBe("Google 商家 API 尚未開通，先以提醒處理");
        CoordinatorFailureCodes.Reason(CoordinatorFailureCodes.GoogleUnavailable).ShouldBe("Google 暫時無法使用");
        CoordinatorFailureCodes.Reason(CoordinatorFailureCodes.GoogleAccessPending, ShopActionKind.GoogleSpecialHours)
            .ShouldBe("Google 商家 API 尚未開通，先以提醒處理");
        foreach (var code in new[] { CoordinatorFailureCodes.GoogleNoRegularHours, CoordinatorFailureCodes.GoogleAccessPending, CoordinatorFailureCodes.GoogleUnavailable })
        {
            CoordinatorCommandAction.IsValidFailureCode(code).ShouldBeTrue(code);
        }
    }

    private static List<string> Periods(ShopScenario scenario, DateOnly date, int hour, int minute) =>
        [.. CoordinatorGoogleSpecialHours.PeriodsFor(Shop, scenario, date, new TimeOnly(hour, minute)).Select(period => period.ToString())];
}
