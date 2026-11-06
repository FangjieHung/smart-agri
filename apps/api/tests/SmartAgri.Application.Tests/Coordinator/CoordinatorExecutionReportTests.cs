using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// The execution report, follow-up and pushed reminder (#375, design §1, Q8, Q18): one line per action by status, the
/// announcement to copy only when the owner posts it by hand, buttons that route back to their action, within LINE's
/// limits; and the effective mode shared by the card and the execution.
/// </summary>
public class CoordinatorExecutionReportTests
{
    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");
    private static readonly DateOnly Monday = new(2026, 11, 2);
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8));
    private static readonly TimeZoneInfo Zone = CoordinatorTestShop.Settings().TimeZone;
    private const string Announcement = "【王記麵店】11/2（一） 臨時店休。";

    [Fact]
    public void Each_status_has_its_line_and_only_failures_and_reminders_have_buttons()
    {
        var lines = new[]
        {
            Line(ShopActionKind.LineBroadcast, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.Succeeded),
            Line(ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.Failed, failure: CoordinatorFailureCodes.Unauthorized),
            Line(ShopActionKind.FacebookPost, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.AwaitingManual, image: "https://example.com/a.jpg"),
            Line(ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Pause, CoordinatorActionStatus.Scheduled, due: Now.AddMinutes(30)),
            Line(ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Resume, CoordinatorActionStatus.DoneManually),
            Line(ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.Scheduled, mode: ShopActionMode.Auto),
            Line(ShopActionKind.Ordering, "foodpanda", CoordinatorActionStep.Pause, CoordinatorActionStatus.Cancelled),
            Line(ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.Failed, failure: "something-new"),
        };

        var messages = CoordinatorExecutionReport.Confirmation(Content(lines));

        messages.Count.ShouldBe(2);
        var report = messages[0];
        report["altText"]!.GetValue<string>().ShouldBe("已確認：王記麵店 11/2（一） 臨時店休。");
        Texts(report["contents"]!["header"]!).ShouldBe(["王記麵店｜11/2（一）臨時店休"]);
        Texts(report["contents"]!["body"]!).ShouldBe(
        [
            "已確認，逐項結果：",
            "✅ LINE 群發公告",
            "❌ Google 特殊營業時間：11/2（一） 休息：授權失效，請重新授權",
            "⏳ FB 粉專貼文", "　步驟。", "　圖片：https://example.com/a.jpg",
            "🕒 Xurf 暫停接單", "　11/2（一）10:30 會提醒你關閉接單",
            "✅ Xurf 重新開啟接單（已手動完成）",
            "⌛ Google 特殊營業時間：11/2（一） 休息：執行中…",
            "🚫 foodpanda 暫停接單：已取消排程",
            "❌ Google 特殊營業時間：11/2（一） 休息：執行失敗",
        ]);
        var buttons = Buttons(report);
        buttons.Select(button => button.Label).ShouldBe(["重試", "已完成", "重試"]);
        Route(buttons[0].Data).ShouldBe(new CoordinatorIntent(CoordinatorIntentKind.RetryAction, OrganizationId: Organization, ActionId: lines[1].ActionId));
        Route(buttons[1].Data).ShouldBe(new CoordinatorIntent(CoordinatorIntentKind.CompleteAction, OrganizationId: Organization, ActionId: lines[2].ActionId));
        messages[1].ToJsonString().ShouldBe(LineMessages.Text(Announcement).ToJsonString(), "the announcement to copy, as plain text");
        WithinLimits(report);
    }

    [Fact]
    public void The_announcement_follows_only_when_a_LINE_broadcast_or_FB_post_is_the_owners_to_post()
    {
        CoordinatorExecutionReport.Confirmation(Content(
        [
            Line(ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.AwaitingManual),
            Line(ShopActionKind.LineBroadcast, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.Succeeded),
            Line(ShopActionKind.FacebookPost, null, CoordinatorActionStep.Undo, CoordinatorActionStatus.AwaitingManual),
        ])).Count.ShouldBe(1);
        CoordinatorExecutionReport.FollowUp(Content(
            [Line(ShopActionKind.LineBroadcast, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.AwaitingManual)])).Count.ShouldBe(2);
        var empty = CoordinatorExecutionReport.Confirmation(Content([]));
        empty.ShouldHaveSingleItem();
        Texts(empty[0]["contents"]!["body"]!).ShouldBe(["已確認，逐項結果：", CoordinatorExecutionReport.NothingToDo]);
    }

    [Fact]
    public void A_reopen_report_names_what_it_undoes_and_its_correction_and_undo_lines()
    {
        var content = Content(
            [
                Line(ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Pause, CoordinatorActionStatus.Cancelled, scenario: ShopScenario.CloseEarly, time: new TimeOnly(16, 0)),
                Line(ShopActionKind.LineBroadcast, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.AwaitingManual, scenario: ShopScenario.Reopen),
                Line(ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Undo, CoordinatorActionStatus.Scheduled, ShopActionMode.Auto, scenario: ShopScenario.Reopen),
                Line(ShopActionKind.FacebookPost, null, CoordinatorActionStep.Undo, CoordinatorActionStatus.Succeeded, scenario: ShopScenario.Reopen),
            ],
            ShopScenario.Reopen) with
        {
            Undone = (ShopScenario.CloseEarly, Monday, new TimeOnly(16, 0)),
        };

        var follow = CoordinatorExecutionReport.FollowUp(content);

        follow[0]["altText"]!.GetValue<string>().ShouldBe("執行結果：王記麵店 11/2（一） 恢復營業");
        Texts(follow[0]["contents"]!["body"]!).ShouldBe(
        [
            "執行結果：",
            "取消 11/2（一） 提早打烊 16:00",
            "🚫 Xurf 暫停接單：已取消排程",
            "⏳ LINE 群發更正公告", "　步驟。",
            "⌛ Google 特殊營業時間：移除 11/2（一） 的設定：執行中…",
            "✅ FB 粉專：刪除公告貼文",
        ]);
    }

    [Fact]
    public void A_pushed_reminder_has_the_steps_the_done_button_and_for_a_broadcast_the_announcement()
    {
        var xurf = Line(ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Resume, CoordinatorActionStatus.AwaitingManual, scenario: ShopScenario.CloseEarly, time: new TimeOnly(16, 0));

        var messages = CoordinatorExecutionReport.Reminder(Organization, "王記麵店", xurf, Announcement);

        var reminder = messages.ShouldHaveSingleItem();
        reminder["altText"]!.GetValue<string>().ShouldBe("提醒：王記麵店 Xurf 重新開啟接單");
        Texts(reminder["contents"]!["header"]!).ShouldBe(["王記麵店｜提醒"]);
        Texts(reminder["contents"]!["body"]!).ShouldBe(["11/2（一） 提早打烊 16:00", "⏳ Xurf 重新開啟接單", "步驟。"]);
        var done = Buttons(reminder).ShouldHaveSingleItem();
        Route(done.Data).ShouldBe(new CoordinatorIntent(CoordinatorIntentKind.CompleteAction, OrganizationId: Organization, ActionId: xurf.ActionId));
        WithinLimits(reminder);

        var broadcast = CoordinatorExecutionReport.Reminder(
            Organization, "王記麵店",
            Line(ShopActionKind.LineBroadcast, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.AwaitingManual, image: "https://example.com/a.jpg"),
            Announcement);
        broadcast.Count.ShouldBe(2);
        Texts(broadcast[0]["contents"]!["body"]!).ShouldContain("圖片：https://example.com/a.jpg");
        broadcast[1]["text"]!.GetValue<string>().ShouldBe(Announcement);
    }

    [Fact]
    public void An_automatic_action_that_became_a_reminder_says_why_above_its_steps()
    {
        var revoked = Line(
            ShopActionKind.LineBroadcast, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.AwaitingManual,
            failure: CoordinatorFailureCodes.TokenRevoked, image: "https://example.com/a.jpg");

        var messages = CoordinatorExecutionReport.FollowUp(Content([revoked]));

        Texts(messages[0]["contents"]!["body"]!).ShouldBe(
        [
            "執行結果：",
            "⏳ LINE 群發公告", "　LINE 權杖失效，已改為手動；請平台營運者重新設定權杖", "　步驟。", "　圖片：https://example.com/a.jpg",
        ]);
        Buttons(messages[0]).Select(button => button.Label).ShouldBe(["已完成"]);
        messages[1]["text"]!.GetValue<string>().ShouldBe(Announcement);
        WithinLimits(messages[0]);
    }

    [Fact]
    public void A_refused_FB_or_Google_authorization_says_the_owner_authorizes_again_and_LINEs_says_the_operator()
    {
        var facebook = Line(ShopActionKind.FacebookPost, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.AwaitingManual, failure: CoordinatorFailureCodes.TokenRevoked);
        var google = Line(ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, CoordinatorActionStatus.Failed, failure: CoordinatorFailureCodes.TokenRevoked);

        var body = Texts(CoordinatorExecutionReport.FollowUp(Content([facebook, google]))[0]["contents"]!["body"]!);

        body.ShouldContain("　FB 粉專授權失效，已改為手動；請老闆用統籌助理傳的連結重新授權");
        body.ShouldContain("❌ Google 特殊營業時間：11/2（一） 休息：Google 商家授權失效，已改為手動；請老闆用統籌助理傳的連結重新授權");
        CoordinatorFailureCodes.Reason(CoordinatorFailureCodes.TokenRevoked, ShopActionKind.LineBroadcast)
            .ShouldBe(CoordinatorFailureCodes.Reason(CoordinatorFailureCodes.TokenRevoked));
        CoordinatorFailureCodes.Reason(CoordinatorFailureCodes.RateLimited, ShopActionKind.FacebookPost).ShouldBe("服務忙碌中");
        CoordinatorAuthorizations.ServiceOf(ShopActionKind.FacebookPost).ShouldBe(CoordinatorService.Facebook);
        CoordinatorAuthorizations.ServiceOf(ShopActionKind.GoogleSpecialHours).ShouldBe(CoordinatorService.Google);
        CoordinatorAuthorizations.ServiceOf(ShopActionKind.LineBroadcast).ShouldBeNull();
        CoordinatorAuthorizations.ServiceOf(ShopActionKind.Ordering).ShouldBeNull();
    }

    [Fact]
    public void Failure_codes_are_short_codes_of_ours_with_a_reason_each()
    {
        CoordinatorActionResult.Failed(CoordinatorFailureCodes.QuotaExhausted).FailureCode.ShouldBe("quota-exhausted");
        Should.Throw<ArgumentException>(() => CoordinatorActionResult.Failed("Token revoked by the user"));
        var revoked = CoordinatorActionResult.UnavailableBecause(CoordinatorFailureCodes.TokenRevoked);
        (revoked.Outcome, revoked.FailureCode).ShouldBe((CoordinatorActionOutcome.Unavailable, "token-revoked"));
        CoordinatorActionResult.Unavailable.FailureCode.ShouldBeNull();
        Should.Throw<ArgumentException>(() => CoordinatorActionResult.UnavailableBecause("LINE said: Authentication failed"));
        Should.Throw<ArgumentException>(() => CoordinatorActionResult.Failed(new string('a', 33)));
        Should.Throw<ArgumentException>(() => CoordinatorActionResult.Succeeded(new string('x', 257)));
        CoordinatorActionResult.Succeeded(string.Empty).ExternalRef.ShouldBeNull();
        foreach (var code in new[]
                 {
                     CoordinatorFailureCodes.Error, CoordinatorFailureCodes.Unauthorized, CoordinatorFailureCodes.QuotaExhausted,
                     CoordinatorFailureCodes.QuotaInsufficient, CoordinatorFailureCodes.LineUnavailable, CoordinatorFailureCodes.TokenRevoked,
                     CoordinatorFailureCodes.RateLimited, CoordinatorFailureCodes.TimedOut, CoordinatorFailureCodes.NetworkError,
                     CoordinatorFailureCodes.Rejected,
                 })
        {
            CoordinatorCommandAction.IsValidFailureCode(code).ShouldBeTrue(code);
            CoordinatorFailureCodes.Reason(code).ShouldNotBe(CoordinatorFailureCodes.Reason("unknown"), code);
        }
    }

    [Fact]
    public async Task The_effective_mode_is_automatic_only_with_an_available_executor_of_an_action_wanted_automatic()
    {
        var available = new Executor(ShopActionKind.GoogleSpecialHours, available: true);
        var unavailable = new Executor(ShopActionKind.FacebookPost, available: false);
        var modes = new ExecutorActionModes(new CoordinatorActionExecutors([available, unavailable]));
        var none = new ExecutorActionModes(new CoordinatorActionExecutors([]));
        var ct = TestContext.Current.CancellationToken;

        (await modes.ModeOfAsync(Organization, new ShopAction(ShopActionKind.GoogleSpecialHours, ShopActionMode.Auto), ct)).ShouldBe(ShopActionMode.Auto);
        (await modes.ModeOfAsync(Organization, new ShopAction(ShopActionKind.GoogleSpecialHours, ShopActionMode.Reminder), ct)).ShouldBe(ShopActionMode.Reminder);
        (await modes.ModeOfAsync(Organization, new ShopAction(ShopActionKind.FacebookPost, ShopActionMode.Auto), ct)).ShouldBe(ShopActionMode.Reminder);
        (await modes.ModeOfAsync(Organization, new ShopAction(ShopActionKind.LineBroadcast, ShopActionMode.Auto), ct)).ShouldBe(ShopActionMode.Reminder);
        (await none.ModeOfAsync(Organization, new ShopAction(ShopActionKind.GoogleSpecialHours, ShopActionMode.Auto), ct)).ShouldBe(ShopActionMode.Reminder);
        Should.Throw<InvalidOperationException>(() => new CoordinatorActionExecutors([new Executor(ShopActionKind.Ordering, available: true)]));

        // The last one registered for a kind wins.
        var later = new Executor(ShopActionKind.GoogleSpecialHours, available: false);
        new CoordinatorActionExecutors([available, later]).For(ShopActionKind.GoogleSpecialHours).ShouldBeSameAs(later);
    }

    private static CoordinatorReportLine Line(
        ShopActionKind kind,
        string? name,
        CoordinatorActionStep step,
        CoordinatorActionStatus status,
        ShopActionMode mode = ShopActionMode.Reminder,
        DateTimeOffset? due = null,
        string? failure = null,
        string? image = null,
        ShopScenario scenario = ShopScenario.TemporaryClosure,
        TimeOnly? time = null) =>
        new(Guid.CreateVersion7(), kind, name, step, mode, status, due ?? Now, "步驟。", image, failure, scenario, Monday, time);

    private static CoordinatorReportContent Content(IReadOnlyList<CoordinatorReportLine> lines, ShopScenario scenario = ShopScenario.TemporaryClosure) =>
        new(Organization, "王記麵店", Zone, scenario, Monday, null, lines, Announcement, Now);

    private static List<string> Texts(JsonNode node)
    {
        var texts = new List<string>();
        Walk(node, element =>
        {
            if (element["type"]?.GetValue<string>() == "text")
            {
                texts.Add(element["text"]!.GetValue<string>());
            }
        });
        return texts;
    }

    private static List<(string Label, string Data)> Buttons(JsonObject message)
    {
        var buttons = new List<(string, string)>();
        Walk(message["contents"]!, element =>
        {
            if (element["type"]?.GetValue<string>() == "button")
            {
                buttons.Add((element["action"]!["label"]!.GetValue<string>(), element["action"]!["data"]!.GetValue<string>()));
            }
        });
        return buttons;
    }

    private static void WithinLimits(JsonObject message)
    {
        message["altText"]!.GetValue<string>().Length.ShouldBeInRange(1, CoordinatorPreviewCard.AltTextMaxLength);
        Walk(message["contents"]!, element =>
        {
            if (element["type"]?.GetValue<string>() == "text")
            {
                element["text"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
            }

            if (element["action"] is JsonObject action)
            {
                action["label"]!.GetValue<string>().Length.ShouldBeInRange(1, 20);
                action["data"]!.GetValue<string>().Length.ShouldBeLessThanOrEqualTo(300);
            }
        });
    }

    private static void Walk(JsonNode node, Action<JsonObject> visit)
    {
        switch (node)
        {
            case JsonObject element:
                visit(element);
                foreach (var (_, child) in element)
                {
                    if (child is not null)
                    {
                        Walk(child, visit);
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        Walk(child, visit);
                    }
                }

                break;
        }
    }

    private static CoordinatorIntent Route(string data)
    {
        var body = $$$"""
            {"destination":"Ubot","events":[{"type":"postback","mode":"active","timestamp":1727000000000,
              "source":{"type":"user","userId":"Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"webhookEventId":"01J0000000000000000000000R",
              "replyToken":"reply","postback":{"data":{{{JsonSerializer.Serialize(data)}}}}}]}
            """;
        return CoordinatorMessageRouter.Route(LineWebhookPayload.TryParse(Encoding.UTF8.GetBytes(body))!.Events.ShouldHaveSingleItem(), isBound: true);
    }

    private sealed class Executor(ShopActionKind kind, bool available) : ICoordinatorActionExecutor
    {
        public ShopActionKind Kind => kind;

        public ValueTask<bool> IsAvailableAsync(Guid organizationId, CancellationToken cancellationToken) => ValueTask.FromResult(available);

        public Task<CoordinatorActionResult> ExecuteAsync(CoordinatorActionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CoordinatorActionResult.Succeeded());

        public Task<CoordinatorActionResult> UndoAsync(CoordinatorActionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CoordinatorActionResult.Succeeded());
    }
}
