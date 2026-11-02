using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// The server's checks of what the model read from a command (#374, design §3 Q6/Q20): the six phrases of the
/// acceptance, each as the model's tool call would give it, the question for 「下週休一天」, and every refusal.
/// </summary>
public class CoordinatorCommandPlannerTests
{
    private static readonly ShopSettingsDocument Shop = CoordinatorTestShop.Settings();
    private static readonly DateTimeOffset Now = CoordinatorTestShop.Now;

    [Theory]
    [InlineData("今天臨時店休", "2026-11-02", "【王記麵店】11/2（一） 臨時店休，11/3（二）11:00 恢復營業。")]
    [InlineData("明天休息", "2026-11-03", "【王記麵店】11/3（二） 臨時店休，11/5（四）11:00 恢復營業。")]
    [InlineData("11/15 店休", "2026-11-15", "【王記麵店】11/15（日） 臨時店休，11/16（一）11:00 恢復營業。")]
    [InlineData("下週一店休", "2026-11-09", "【王記麵店】11/9（一） 臨時店休，11/10（二）11:00 恢復營業。")]
    public void A_closure_on_a_business_day_is_previewed_with_the_reopening(string phrase, string date, string announcement)
    {
        var plan = CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.TemporaryClosure, DateOnly.Parse(date), null, reason: null);

        plan.Kind.ShouldBe(CoordinatorPlanKind.Preview, phrase);
        plan.Date.ShouldBe(DateOnly.Parse(date));
        plan.Time.ShouldBeNull();
        plan.Announcement.ShouldBe(announcement, "the reason line is left out without a reason");
        plan.Message.ShouldBeNull();
    }

    [Fact]
    public void Close_early_and_open_late_are_previewed_with_their_time()
    {
        var early = CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.CloseEarly, new DateOnly(2026, 11, 2), new TimeOnly(16, 0), "家裡有事");
        early.Kind.ShouldBe(CoordinatorPlanKind.Preview, "今天提早四點打烊");
        early.Time.ShouldBe(new TimeOnly(16, 0));
        early.Announcement.ShouldBe("【王記麵店】11/2（一） 提早到 16:00 打烊。\n原因：家裡有事");

        var late = CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.OpenLate, new DateOnly(2026, 11, 3), new TimeOnly(12, 0), null);
        late.Kind.ShouldBe(CoordinatorPlanKind.Preview, "明天延後到中午開");
        late.Announcement.ShouldBe("【王記麵店】11/3（二） 延後到 12:00 開店。");
    }

    [Fact]
    public void A_missing_date_is_asked_for_with_the_models_question_or_a_default()
    {
        var asked = CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.TemporaryClosure, null, null, null, "下週要休哪一天？");
        asked.Kind.ShouldBe(CoordinatorPlanKind.AskDate, "下週休一天");
        asked.Message.ShouldBe("下週要休哪一天？");

        CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.TemporaryClosure, null, null, null).Message.ShouldBe("要休哪一天？");
        CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.CloseEarly, null, new TimeOnly(16, 0), null).ShouldSatisfyAllConditions(
            plan => plan.Kind.ShouldBe(CoordinatorPlanKind.AskDate),
            plan => plan.Time.ShouldBe(new TimeOnly(16, 0), "what is known is kept"),
            plan => plan.Message.ShouldBe("哪一天要提早打烊？"));
    }

    [Fact]
    public void A_missing_time_is_asked_for()
    {
        var plan = CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.CloseEarly, new DateOnly(2026, 11, 2), null, null);

        plan.Kind.ShouldBe(CoordinatorPlanKind.AskTime);
        plan.Date.ShouldBe(new DateOnly(2026, 11, 2));
        plan.Message.ShouldBe("要提早到幾點打烊？");
        CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.OpenLate, new DateOnly(2026, 11, 3), null, null).Message.ShouldBe("要延後到幾點開店？");
    }

    [Fact]
    public void A_date_before_today_or_after_90_days_is_asked_again()
    {
        var past = CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 1), null, null);
        past.Kind.ShouldBe(CoordinatorPlanKind.AskDate);
        past.Date.ShouldBeNull();
        past.Message.ShouldBe("11/1（日） 已經過了。要休哪一天？");

        var last = new DateOnly(2027, 1, 31);
        CoordinatorCommandPlanner.LastDate(Shop, Now).ShouldBe(last);
        CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.TemporaryClosure, last, null, null).Kind.ShouldBe(CoordinatorPlanKind.Preview);
        var far = CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.TemporaryClosure, last.AddDays(1), null, null);
        far.Kind.ShouldBe(CoordinatorPlanKind.AskDate);
        far.Message.ShouldBe("最遠只能排到 1/31（日）。要休哪一天？");
    }

    [Fact]
    public void Today_is_the_shops_local_date()
    {
        // 2026-11-01 17:00 UTC is already Monday 01:00 in Taipei.
        var utcSunday = new DateTimeOffset(2026, 11, 1, 17, 0, 0, TimeSpan.Zero);

        CoordinatorCommandPlanner.Today(Shop, utcSunday).ShouldBe(new DateOnly(2026, 11, 2));
        CoordinatorCommandPlanner.Plan(Shop, utcSunday, ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 1), null, null).Kind
            .ShouldBe(CoordinatorPlanKind.AskDate);
    }

    [Theory]
    [InlineData(ShopScenario.TemporaryClosure, null)]
    [InlineData(ShopScenario.CloseEarly, "16:00")]
    [InlineData(ShopScenario.OpenLate, "12:00")]
    public void A_regular_day_off_needs_no_card(ShopScenario scenario, string? time)
    {
        var plan = CoordinatorCommandPlanner.Plan(Shop, Now, scenario, new DateOnly(2026, 11, 4), time is null ? null : TimeOnly.Parse(time), null);

        plan.Kind.ShouldBe(CoordinatorPlanKind.DayOff);
        plan.Message.ShouldBe("11/4（三） 本來就是公休日。");
        plan.Announcement.ShouldBeNull();
    }

    [Theory]
    [InlineData(ShopScenario.CloseEarly, "10:30")]
    [InlineData(ShopScenario.CloseEarly, "11:00")]
    [InlineData(ShopScenario.CloseEarly, "21:00")]
    [InlineData(ShopScenario.CloseEarly, "22:00")]
    [InlineData(ShopScenario.OpenLate, "09:00")]
    [InlineData(ShopScenario.OpenLate, "21:30")]
    public void A_time_outside_the_first_opening_and_the_last_closing_is_asked_again(ShopScenario scenario, string time)
    {
        var plan = CoordinatorCommandPlanner.Plan(Shop, Now, scenario, new DateOnly(2026, 11, 3), TimeOnly.Parse(time), null);

        plan.Kind.ShouldBe(CoordinatorPlanKind.AskTime);
        plan.Time.ShouldBeNull();
        plan.Message!.ShouldStartWith("11/3（二） 的營業時間是 11:00–14:00、17:00–21:00，");
        plan.Message.ShouldContain("要在 11:00 之後、21:00 之前。");
    }

    [Theory]
    [InlineData("12:30")]
    [InlineData("15:00")]
    [InlineData("20:59")]
    public void A_time_after_the_first_opening_and_before_the_last_closing_is_fine_even_between_periods(string time)
    {
        CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.CloseEarly, new DateOnly(2026, 11, 3), TimeOnly.Parse(time), null).Kind
            .ShouldBe(CoordinatorPlanKind.Preview);
    }

    [Fact]
    public void The_small_hours_of_an_overnight_period_belong_to_the_day_it_opens()
    {
        var saturday = new DateOnly(2026, 11, 7);

        CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.CloseEarly, saturday, new TimeOnly(0, 30), null).Kind.ShouldBe(CoordinatorPlanKind.Preview);
        var tooLate = CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.CloseEarly, saturday, new TimeOnly(1, 30), null);
        tooLate.Kind.ShouldBe(CoordinatorPlanKind.AskTime);
        tooLate.Message!.ShouldContain("要在 11:00 之後、01:00 之前。");
    }

    [Fact]
    public void A_time_that_has_passed_today_is_asked_again()
    {
        var evening = new DateTimeOffset(2026, 11, 2, 17, 30, 0, TimeSpan.FromHours(8));

        var plan = CoordinatorCommandPlanner.Plan(Shop, evening, ShopScenario.CloseEarly, new DateOnly(2026, 11, 2), new TimeOnly(16, 0), null);

        plan.Kind.ShouldBe(CoordinatorPlanKind.AskTime);
        plan.Message.ShouldBe("11/2（一） 16:00 已經過了。要提早到幾點打烊？");
        CoordinatorCommandPlanner.Plan(Shop, evening, ShopScenario.CloseEarly, new DateOnly(2026, 11, 2), new TimeOnly(20, 0), null).Kind
            .ShouldBe(CoordinatorPlanKind.Preview);
    }

    [Fact]
    public void A_scenario_the_shop_does_not_offer_is_said_so()
    {
        var json = CoordinatorTestShop.Json.Replace(
            "\"open-late\": {\n      \"enabled\": true,", "\"open-late\": {\n      \"enabled\": false,", StringComparison.Ordinal);
        json.ShouldNotBe(CoordinatorTestShop.Json);
        var shop = CoordinatorTestShop.Settings(json);

        var plan = CoordinatorCommandPlanner.Plan(shop, Now, ShopScenario.OpenLate, new DateOnly(2026, 11, 3), new TimeOnly(12, 0), null);

        plan.Kind.ShouldBe(CoordinatorPlanKind.Disabled);
        plan.Message.ShouldBe("這家店沒有開啟「延後開店」。");
        Should.Throw<ArgumentException>(() => CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.Reopen, null, null, null));
    }

    [Fact]
    public void A_reopen_announcement_is_rendered_only_when_the_shop_has_one()
    {
        CoordinatorCommandPlanner.ReopenAnnouncement(Shop, new DateOnly(2026, 11, 15), null).ShouldBe("【王記麵店】11/15（日） 照常營業。");

        var json = CoordinatorTestShop.Json.Replace(
            "\"reopen\": { \"enabled\": true, \"announcement\": { \"template\": \"【{店名}】{日期} 照常營業。\" } }",
            "\"reopen\": { \"enabled\": true }",
            StringComparison.Ordinal);
        CoordinatorCommandPlanner.ReopenAnnouncement(CoordinatorTestShop.Settings(json), new DateOnly(2026, 11, 15), null).ShouldBeNull();
    }

    [Fact]
    public void The_plan_never_prints_the_announcement()
    {
        var plan = CoordinatorCommandPlanner.Plan(Shop, Now, ShopScenario.CloseEarly, new DateOnly(2026, 11, 2), new TimeOnly(16, 0), "家裡有事");

        plan.ToString().ShouldNotContain("家裡有事");
    }
}
