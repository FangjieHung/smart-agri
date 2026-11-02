using System.Reflection;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// When and how each action of a confirmed command runs (#375, design Q7, Q20, Q21): ordering reminders 30 minutes before
/// the opening (or the early closing), never before now; and what a reopen drops, undoes and reminds.
/// </summary>
public class CoordinatorActionScheduleTests
{
    private static readonly ShopSettingsDocument Shop = CoordinatorTestShop.Settings();
    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);
    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");
    private static readonly Guid Command = Guid.Parse("0194a100-0000-7000-8000-0000000000d1");
    private const string Owner = "Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static TheoryData<string, string, string?, string, string, string> Times => new()
    {
        // Scenario, date, time, now (Taipei), pause, resume.
        { "temporary-closure", "2026-11-02", null, "2026-11-02 10:00", "2026-11-02 10:30", "2026-11-03 10:30" },
        // Wednesday is a regular day off: resume before Thursday's opening.
        { "temporary-closure", "2026-11-03", null, "2026-11-02 10:00", "2026-11-03 10:30", "2026-11-05 10:30" },
        // Saturday's dinner runs until 01:00: it is part of the closed day; resume before Sunday's lunch.
        { "temporary-closure", "2026-11-07", null, "2026-11-02 10:00", "2026-11-07 10:30", "2026-11-08 10:30" },
        // Confirmed after today's opening: the pause reminder is due now.
        { "temporary-closure", "2026-11-02", null, "2026-11-02 12:00", "2026-11-02 12:00", "2026-11-03 10:30" },
        // A date weeks ahead.
        { "temporary-closure", "2026-11-26", null, "2026-11-02 10:00", "2026-11-26 10:30", "2026-11-27 10:30" },
        // 提早打烊: pause before the new closing time, resume before a later day's opening — not tonight's dinner.
        { "close-early", "2026-11-02", "16:00", "2026-11-02 10:00", "2026-11-02 15:30", "2026-11-03 10:30" },
        { "close-early", "2026-11-03", "13:00", "2026-11-02 10:00", "2026-11-03 12:30", "2026-11-05 10:30" },
        { "close-early", "2026-11-07", "23:00", "2026-11-02 10:00", "2026-11-07 22:30", "2026-11-08 10:30" },
        // The small hours of Saturday's overnight period are Sunday's clock.
        { "close-early", "2026-11-07", "00:30", "2026-11-02 10:00", "2026-11-08 00:00", "2026-11-08 10:30" },
        { "close-early", "2026-11-02", "16:00", "2026-11-02 15:50", "2026-11-02 15:50", "2026-11-03 10:30" },
        // 延後開店: pause before the usual opening, resume before the new one.
        { "open-late", "2026-11-03", "12:00", "2026-11-02 10:00", "2026-11-03 10:30", "2026-11-03 11:30" },
        { "open-late", "2026-11-02", "12:00", "2026-11-02 10:45", "2026-11-02 10:45", "2026-11-02 11:30" },
    };

    [Theory]
    [MemberData(nameof(Times))]
    public void Ordering_is_reminded_30_minutes_before_the_opening_never_before_now(
        string scenario, string date, string? time, string now, string pause, string resume)
    {
        var (paused, resumed) = CoordinatorActionSchedule.OrderingTimes(
            Shop, Domain.WireNames<ShopScenario>.Parse(scenario), DateOnly.Parse(date), time is null ? null : TimeOnly.Parse(time), At(now));

        (paused, resumed).ShouldBe((At(pause), At(resume)));
    }

    [Fact]
    public void A_closure_applies_its_actions_now_and_reminds_each_ordering_system_twice()
    {
        var now = At("2026-11-02 10:00");
        var modes = new[] { ShopActionMode.Auto, ShopActionMode.Reminder, ShopActionMode.Reminder, ShopActionMode.Reminder, ShopActionMode.Reminder };

        var plans = CoordinatorActionSchedule.Plan(Shop, ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 2), null, modes, now);

        plans.Select(plan => (plan.Kind, plan.Name, plan.Step, plan.Mode, plan.DueAt, plan.StepsText)).ShouldBe(
        [
            (ShopActionKind.LineBroadcast, null, CoordinatorActionStep.Apply, ShopActionMode.Auto, now, "群發公告。"),
            (ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, ShopActionMode.Reminder, now, "Google 設為休息。"),
            (ShopActionKind.FacebookPost, null, CoordinatorActionStep.Apply, ShopActionMode.Reminder, now, "FB 發文。"),
            (ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Pause, ShopActionMode.Reminder, At("2026-11-02 10:30"), "關閉 Xurf。"),
            (ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Resume, ShopActionMode.Reminder, At("2026-11-03 10:30"), "打開 Xurf。"),
            (ShopActionKind.Ordering, "foodpanda", CoordinatorActionStep.Pause, ShopActionMode.Reminder, At("2026-11-02 10:30"), "暫停。"),
            (ShopActionKind.Ordering, "foodpanda", CoordinatorActionStep.Resume, ShopActionMode.Reminder, At("2026-11-03 10:30"), "恢復。"),
        ]);
        plans.ShouldAllBe(plan => plan.ImageUrl == null && plan.UndoesActionId == null, "this shop's closure has no image");
        Should.Throw<ArgumentException>(() => CoordinatorActionSchedule.Plan(Shop, ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 2), null, [], now));
    }

    [Fact]
    public void The_image_goes_with_the_LINE_broadcast_and_the_FB_post_only()
    {
        var json = CoordinatorTestShop.Json.Replace(
            "臨時店休，{恢復營業} 恢復營業。\\n原因：{原因}\" }",
            "臨時店休，{恢復營業} 恢復營業。\\n原因：{原因}\", \"imageUrl\": \"https://example.com/closed.jpg\" }",
            StringComparison.Ordinal);
        var shop = CoordinatorTestShop.Settings(json);

        var plans = CoordinatorActionSchedule.Plan(
            shop, ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 2), null, [.. Enumerable.Repeat(ShopActionMode.Reminder, 5)], At("2026-11-02 10:00"));

        plans.Select(plan => plan.ImageUrl).ShouldBe(["https://example.com/closed.jpg", null, "https://example.com/closed.jpg", null, null, null, null]);
    }

    [Fact]
    public void The_actions_of_recorded_rows_are_what_the_settings_were_at_confirmation()
    {
        var rows = Rows(At("2026-11-02 10:00"), ShopActionMode.Auto);

        var actions = CoordinatorActionSchedule.ActionsOf(rows);

        actions.ShouldBe(
        [
            new ShopAction(ShopActionKind.LineBroadcast, ShopActionMode.Auto, Steps: "群發公告。"),
            new ShopAction(ShopActionKind.GoogleSpecialHours, ShopActionMode.Reminder, Steps: "Google 設為休息。"),
            new ShopAction(ShopActionKind.FacebookPost, ShopActionMode.Reminder, Steps: "FB 發文。"),
            new ShopAction(ShopActionKind.Ordering, ShopActionMode.Reminder, Name: "Xurf", PauseSteps: "關閉 Xurf。", ResumeSteps: "打開 Xurf。"),
            new ShopAction(ShopActionKind.Ordering, ShopActionMode.Reminder, Name: "foodpanda", PauseSteps: "暫停。", ResumeSteps: "恢復。"),
        ]);
    }

    [Fact]
    public void A_reopen_drops_what_waits_undoes_what_happened_and_resumes_ordering_that_paused()
    {
        var confirmed = At("2026-11-02 10:00");
        var rows = Rows(confirmed, ShopActionMode.Auto);
        var (broadcast, google, facebook, xurfPause, xurfResume, pandaPause, pandaResume) = (rows[0], rows[1], rows[2], rows[3], rows[4], rows[5], rows[6]);
        SetStatus(broadcast, CoordinatorActionStatus.Succeeded);
        SetStatus(google, CoordinatorActionStatus.DoneManually);
        SetStatus(facebook, CoordinatorActionStatus.Failed);
        SetStatus(xurfPause, CoordinatorActionStatus.AwaitingManual);

        // 10:40: Xurf's pause was reminded; foodpanda's (due 10:30, its job not run yet) counts as having happened.
        var now = At("2026-11-02 10:40");
        var autoModes = new Dictionary<ShopActionKind, ShopActionMode> { [ShopActionKind.LineBroadcast] = ShopActionMode.Auto };
        var plan = CoordinatorActionSchedule.PlanReopen(
            rows, ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 2), null, correction: true, "https://example.com/open.jpg", autoModes, now);

        plan.Cancel.ShouldBe([facebook.Id, xurfResume.Id, pandaPause.Id, pandaResume.Id], ignoreOrder: true);
        plan.Actions.Select(action => (action.Kind, action.Name, action.Step, action.Mode, action.DueAt, action.StepsText, action.ImageUrl, action.UndoesActionId)).ShouldBe(
        [
            (ShopActionKind.LineBroadcast, null, CoordinatorActionStep.Apply, ShopActionMode.Auto, now, "群發公告。", "https://example.com/open.jpg", null),
            (ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Undo, ShopActionMode.Reminder, now,
                "到 Google 商家檔案刪除 11/2（一） 的特殊營業時間。", null, google.Id),
            (ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Resume, ShopActionMode.Reminder, now, "打開 Xurf。", null, xurfPause.Id),
            (ShopActionKind.Ordering, "foodpanda", CoordinatorActionStep.Resume, ShopActionMode.Reminder, now, "恢復。", null, pandaPause.Id),
        ]);
    }

    [Fact]
    public void A_reopen_undoes_an_automatic_success_automatically_only_while_its_executor_is_available()
    {
        var rows = Rows(At("2026-11-02 10:00"), ShopActionMode.Auto);
        SetStatus(rows[1], CoordinatorActionStatus.Succeeded);
        SetStatus(rows[2], CoordinatorActionStatus.Succeeded);
        var now = At("2026-11-02 10:05");

        var available = CoordinatorActionSchedule.PlanReopen(
            rows, ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 2), null, correction: false, null,
            new Dictionary<ShopActionKind, ShopActionMode> { [ShopActionKind.FacebookPost] = ShopActionMode.Auto }, now);
        available.Actions.Select(action => (action.Kind, action.Step, action.Mode)).ShouldBe(
        [
            (ShopActionKind.GoogleSpecialHours, CoordinatorActionStep.Undo, ShopActionMode.Reminder),
            (ShopActionKind.FacebookPost, CoordinatorActionStep.Undo, ShopActionMode.Auto),
        ]);
        available.Actions[1].StepsText.ShouldBe("到 FB 粉專刪除 11/2（一） 臨時店休的公告貼文。");

        // Nothing paused yet (due 10:30): no resume. The LINE 群發 still waiting for its job is dropped, and there is no correction.
        available.Cancel.ShouldBe([rows[0].Id, rows[3].Id, rows[4].Id, rows[5].Id, rows[6].Id], ignoreOrder: true);
        available.Actions.ShouldNotContain(action => action.Kind == ShopActionKind.Ordering || action.Kind == ShopActionKind.LineBroadcast);
    }

    [Fact]
    public void A_reopen_after_the_resume_came_does_not_remind_it_again()
    {
        var rows = Rows(At("2026-11-02 10:00"), ShopActionMode.Reminder);
        foreach (var row in rows.Where(row => row.Kind == ShopActionKind.Ordering))
        {
            SetStatus(row, CoordinatorActionStatus.DoneManually);
        }

        var plan = CoordinatorActionSchedule.PlanReopen(
            rows, ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 2), null, correction: false, null,
            new Dictionary<ShopActionKind, ShopActionMode>(), At("2026-11-03 10:40"));

        plan.Actions.ShouldNotContain(action => action.Kind == ShopActionKind.Ordering);
        plan.Cancel.ShouldBeEmpty();
    }

    /// <summary>The rows of a closure on Monday 11/2 of <see cref="CoordinatorTestShop"/>, confirmed at
    /// <paramref name="now"/>, LINE 群發 in <paramref name="broadcastMode"/>.</summary>
    private static List<CoordinatorCommandAction> Rows(DateTimeOffset now, ShopActionMode broadcastMode)
    {
        var modes = new[] { broadcastMode, ShopActionMode.Reminder, ShopActionMode.Reminder, ShopActionMode.Reminder, ShopActionMode.Reminder };
        return
        [
            .. CoordinatorActionSchedule.Plan(Shop, ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 2), null, modes, now)
                .Select((plan, position) => CoordinatorCommandAction.Create(
                    Organization, Command, position, plan.Kind, plan.Name, plan.Step, plan.Mode, plan.DueAt, plan.StepsText, plan.ImageUrl,
                    Owner, plan.UndoesActionId, now)),
        ];
    }

    /// <summary>What the persistence layer's compare-and-set would have done.</summary>
    private static void SetStatus(CoordinatorCommandAction row, CoordinatorActionStatus status) =>
        typeof(CoordinatorCommandAction).GetProperty(nameof(CoordinatorCommandAction.Status), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(row, status);

    private static DateTimeOffset At(string taipei) =>
        new(DateTime.ParseExact(taipei, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), Taipei);
}
