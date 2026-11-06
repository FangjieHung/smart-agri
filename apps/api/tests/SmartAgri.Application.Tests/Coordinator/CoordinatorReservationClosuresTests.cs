using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// A closure and the online reservations (store-reservations design decision R6, Slice R4, #392): the preview card's line
/// and its list (none, a few, more than five), a reopen's 「不會恢復」, the reasons, the report's summary and the reminder that
/// lists the customers to call, the cancellation Email word for word, the member's later press — within LINE's limits, and
/// with the customers' details only where the members need them (never in an alt text).
/// </summary>
public class CoordinatorReservationClosuresTests
{
    private const string Owner = "Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Footer = "——\n這封信由線上訂位系統代王記麵店寄出；回覆這封信不會有人讀取。\n";
    private static readonly ShopSettingsDocument Shop = CoordinatorTestShop.Settings();
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");
    private static readonly Guid Command = Guid.Parse("0194a100-0000-7000-8000-0000000000d1");
    private static readonly DateOnly Monday = new(2026, 11, 2);
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8));

    // --- The preview card --------------------------------------------------------------------------------

    [Fact]
    public void A_card_without_reservations_to_cancel_has_no_line()
    {
        var card = CoordinatorPreviewCard.Build(Card(ShopScenario.TemporaryClosure, CoordinatorReservationClosures.Card([], Taipei)));
        var none = CoordinatorPreviewCard.Build(Card(ShopScenario.TemporaryClosure, reservations: null));

        Texts(card["contents"]!["body"]!).ShouldBe(Texts(none["contents"]!["body"]!));
        Texts(card["contents"]!["body"]!).ShouldNotContain(text => text.Contains("訂位", StringComparison.Ordinal));
    }

    [Fact]
    public void Three_reservations_are_counted_by_status_and_listed_with_their_time_and_name()
    {
        var affected = new[]
        {
            Reservation(11, 0, "陳小姐"),
            Reservation(18, 30, "林先生", CoordinatorReservationStatus.Accepted),
            Reservation(0, 30, "王太太", nextDay: true),
        };

        var card = CoordinatorPreviewCard.Build(Card(ShopScenario.TemporaryClosure, CoordinatorReservationClosures.Card(affected, Taipei)));

        var body = Texts(card["contents"]!["body"]!);
        var line = body.IndexOf("・會取消 3 筆訂位（已接受 1 筆、待處理 2 筆）");
        line.ShouldBeGreaterThan(body.IndexOf("・foodpanda 暫停接單（提醒）"), "under the actions");
        body.Skip(line + 1).Take(3).ShouldBe(["　11:00 陳小姐", "　18:30 林先生", "　00:30（隔天） 王太太"]);
        body[line + 4].ShouldBe("公告");
        card["altText"]!.GetValue<string>().ShouldBe("王記麵店 11/2（一） 臨時店休預覽", "no name in the notification");
        WithinLimits(card);
    }

    [Fact]
    public void Seven_reservations_list_the_first_five_and_say_how_many_more()
    {
        var affected = Enumerable.Range(0, 7)
            .Select(index => Reservation(17 + (index / 2), index % 2 * 30, $"客人{index + 1}", index < 2 ? CoordinatorReservationStatus.Accepted : CoordinatorReservationStatus.Pending))
            .ToList();

        var card = CoordinatorPreviewCard.Build(Card(ShopScenario.CloseEarly, CoordinatorReservationClosures.Card(affected, Taipei), new TimeOnly(17, 0)));

        var body = Texts(card["contents"]!["body"]!);
        var line = body.IndexOf("・會取消 7 筆訂位（已接受 2 筆、待處理 5 筆）");
        line.ShouldBeGreaterThanOrEqualTo(0);
        body.Skip(line + 1).Take(6).ShouldBe(
            ["　17:00 客人1", "　17:30 客人2", "　18:00 客人3", "　18:30 客人4", "　19:00 客人5", "　還有 2 筆"]);
        body.ShouldNotContain(text => text.Contains("客人6", StringComparison.Ordinal));
        WithinLimits(card);

        // Five exactly: no 「還有」.
        var five = Texts(CoordinatorPreviewCard.Build(Card(ShopScenario.CloseEarly, CoordinatorReservationClosures.Card(affected.Take(5).ToList(), Taipei), new TimeOnly(17, 0)))["contents"]!["body"]!);
        five.ShouldNotContain(text => text.Contains("還有", StringComparison.Ordinal));
        five.ShouldContain("・會取消 5 筆訂位（已接受 2 筆、待處理 3 筆）");
    }

    [Fact]
    public void A_reopen_card_says_the_cancelled_reservations_stay_cancelled()
    {
        var date = new DateOnly(2026, 11, 15);
        CoordinatorCardContent Reopen(int notRestored) => new(
            Organization, Command, 1, "王記麵店", ShopScenario.Reopen, date, null,
            CoordinatorPreviewCard.ReopenRows(Shop, ShopScenario.TemporaryClosure, date, [.. Shop[ShopScenario.TemporaryClosure].Actions.Select(_ => ShopActionMode.Reminder)], false),
            null,
            new CoordinatorReopenDetails(ShopScenario.TemporaryClosure, date, null, CorrectionOffered: false, CorrectionBroadcast: false),
            new CoordinatorCardReservations(0, 0, [], notRestored));

        var body = Texts(CoordinatorPreviewCard.Build(Reopen(2))["contents"]!["body"]!);
        body[^1].ShouldBe("・因店休取消的 2 筆訂位不會恢復，請視需要聯絡顧客");
        Texts(CoordinatorPreviewCard.Build(Reopen(0))["contents"]!["body"]!).ShouldNotContain(text => text.Contains("訂位", StringComparison.Ordinal));
    }

    // --- The words ---------------------------------------------------------------------------------------

    [Fact]
    public void The_reason_names_the_scenario_and_its_time()
    {
        CoordinatorReservationClosures.Reason(ShopScenario.TemporaryClosure, null).ShouldBe("臨時店休");
        CoordinatorReservationClosures.Reason(ShopScenario.CloseEarly, new TimeOnly(20, 0)).ShouldBe("當天提早在 20:00 打烊");
        CoordinatorReservationClosures.Reason(ShopScenario.OpenLate, new TimeOnly(12, 0)).ShouldBe("當天延後到 12:00 開店");
        CoordinatorReservationClosures.Reason(ShopScenario.CloseEarly, null).ShouldBe("當天提早打烊");
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorReservationClosures.Reason(ShopScenario.Reopen, null));

        CoordinatorReservationCards.CancelledBecause("臨時店休").ShouldBe("這筆訂位因臨時店休已取消。");
        CoordinatorReservationCards.CancelledBecause(CoordinatorReservationClosures.Reason(ShopScenario.CloseEarly, new TimeOnly(20, 0)))
            .ShouldBe("這筆訂位因當天提早在 20:00 打烊已取消。");
        CoordinatorReservationClosures.Cancelled(3, 1).ShouldBe("已取消 3 筆訂位，其中 1 筆會寄信通知顧客。");
        CoordinatorReservationClosures.Cancelled(2, 0).ShouldBe("已取消 2 筆訂位。");
        CoordinatorReservationClosures.NotRestored(2).ShouldBe("因店休取消的 2 筆訂位不會恢復，請視需要聯絡顧客");
    }

    [Fact]
    public void The_cancellation_Email_says_why_and_whom_to_call_and_nothing_the_customer_typed()
    {
        var reservation = Cancelled(Reservation(18, 30, "吳先生", email: "wu@example.com", phone: "0977888999", note: "素食兩位"), ShopScenario.CloseEarly);

        var message = CoordinatorReservationEmail.Compose("王記麵店", "02-2345-6789", Taipei, reservation, "當天提早在 17:00 打烊");

        message.To.ShouldBe("wu@example.com");
        message.FromName.ShouldBe("王記麵店");
        message.Subject.ShouldBe("王記麵店：訂位已取消");
        message.Body.ShouldBe(
            "很抱歉，王記麵店因當天提早在 17:00 打烊，必須取消您 11/2（一） 18:30 的訂位，造成不便，敬請見諒。如有問題請來電 02-2345-6789。\n\n" + Footer);
        foreach (var typed in new[] { "吳先生", "0977888999", "0977-888-999", "素食兩位" })
        {
            (message.Subject + message.Body).ShouldNotContain(typed);
        }

        CoordinatorReservationEmail.Compose("王記麵店", null, Taipei, Cancelled(Reservation(0, 30, "王太太", nextDay: true, email: "w@example.com"), ShopScenario.TemporaryClosure), "臨時店休")
            .Body.ShouldStartWith("很抱歉，王記麵店因臨時店休，必須取消您 11/2（一） 00:30（隔天） 的訂位，造成不便，敬請見諒。如有問題請直接與店家聯絡。\n");
        Should.Throw<ArgumentException>(() => CoordinatorReservationEmail.Compose("王記麵店", "02-2345-6789", Taipei, reservation));
        CoordinatorReservationEmail.Subject("王記麵店", CoordinatorReservationStatus.Cancelled).ShouldBe("王記麵店：訂位已取消");
    }

    [Fact]
    public void Who_is_told_by_Email_is_one_rule()
    {
        var withAddress = Cancelled(Reservation(18, 0, "甲", email: "a@example.com"), ShopScenario.TemporaryClosure);
        var without = Cancelled(Reservation(18, 30, "乙"), ShopScenario.TemporaryClosure);

        CoordinatorReservationEmail.Sends(withAddress, emailConfigured: true).ShouldBeTrue();
        CoordinatorReservationEmail.Sends(withAddress, emailConfigured: false).ShouldBeFalse("no SMTP");
        CoordinatorReservationEmail.Sends(without, emailConfigured: true).ShouldBeFalse("no address");
        CoordinatorReservationEmail.Sends(Reservation(19, 0, "丙", email: "c@example.com"), emailConfigured: true).ShouldBeFalse("pending: nothing to tell");
    }

    // --- The report --------------------------------------------------------------------------------------

    [Fact]
    public void The_report_says_how_many_were_cancelled_and_lists_whom_to_call_above_the_done_button()
    {
        var cancelled = new[]
        {
            Cancelled(Reservation(19, 0, "林先生", phone: "0911222333"), ShopScenario.CloseEarly),
            Cancelled(Reservation(18, 30, "陳小姐", email: "chen@example.com"), ShopScenario.CloseEarly),
            Cancelled(Reservation(0, 30, "王太太", nextDay: true, phone: "0223456789"), ShopScenario.CloseEarly),
        };
        var reservations = CoordinatorReservationClosures.Report(cancelled, emailConfigured: true, Taipei);
        reservations.ShouldSatisfyAllConditions(
            summary => summary.Cancelled.ShouldBe(3),
            summary => summary.Emailed.ShouldBe(1),
            summary => summary.Calls.ShouldBe(["19:00 林先生 0911-222-333", "00:30（隔天） 王太太 02-2345-6789"]));
        reservations.ToString().ShouldNotContain("林先生");

        var calls = Line(ShopActionKind.ReservationCalls, CoordinatorReservationClosures.CallsSteps);
        var messages = CoordinatorExecutionReport.Confirmation(Content([Line(ShopActionKind.LineBroadcast, "群發公告。"), calls], reservations));

        var report = messages[0];
        report["altText"]!.GetValue<string>().ShouldBe("已確認：王記麵店 11/2（一） 提早打烊 17:00。");
        Texts(report["contents"]!["body"]!).ShouldBe(
        [
            "已確認，逐項結果：",
            "已取消 3 筆訂位，其中 1 筆會寄信通知顧客。",
            "⏳ LINE 群發公告", "　群發公告。",
            "⏳ 打電話通知顧客訂位已取消", "　這些顧客沒有留 Email，請來電告知訂位已取消：",
            "　19:00 林先生 0911-222-333\n　00:30（隔天） 王太太 02-2345-6789",
        ]);
        Buttons(report).Select(button => button.Label).ShouldBe(["已完成", "已完成"]);
        CoordinatorExecutionReport.Label(ShopActionKind.ReservationCalls, null, CoordinatorActionStep.Apply, ShopScenario.CloseEarly, Monday, new TimeOnly(17, 0))
            .ShouldBe("打電話通知顧客訂位已取消");
        WithinLimits(report);

        // Without SMTP everyone is called; the follow-up has no summary.
        var withoutEmail = CoordinatorReservationClosures.Report(cancelled, emailConfigured: false, Taipei);
        (withoutEmail.Emailed, withoutEmail.Calls.Count).ShouldBe((0, 3));
        var body = Texts(CoordinatorExecutionReport.Confirmation(Content([Line(ShopActionKind.ReservationCalls, CoordinatorReservationClosures.StepsOf(false))], withoutEmail))[0]["contents"]!["body"]!);
        body[1].ShouldBe("已取消 3 筆訂位。");
        body.ShouldContain("　平台沒有設定寄信，請來電告知這些顧客訂位已取消：");
        Texts(CoordinatorExecutionReport.FollowUp(Content([Line(ShopActionKind.LineBroadcast, "群發公告。")]))[0]["contents"]!["body"]!)
            .ShouldNotContain(text => text.Contains("訂位", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hundred_calls_list_the_first_fifty_within_a_bubbles_size()
    {
        var cancelled = Enumerable.Range(0, 100)
            .Select(index => Cancelled(Reservation(17, 0, new string('名', ReservationContact.NameMaxLength)), ShopScenario.TemporaryClosure))
            .ToList();
        var message = CoordinatorExecutionReport.Confirmation(Content(
            [Line(ShopActionKind.ReservationCalls, CoordinatorReservationClosures.CallsSteps)],
            CoordinatorReservationClosures.Report(cancelled, emailConfigured: true, Taipei)))[0];

        Encoding.UTF8.GetByteCount(message.ToJsonString()).ShouldBeLessThan(30_000, "LINE's limit on a bubble");
        var listed = Texts(message["contents"]!["body"]!)[^1].Split('\n');
        listed.Length.ShouldBe(CoordinatorReservationClosures.ReportCallsMax + 1);
        listed[^1].ShouldBe("　還有 50 筆沒有列出，請聯絡平台營運者");
    }

    [Fact]
    public void A_reopen_report_says_what_stays_cancelled()
    {
        var content = new CoordinatorReportContent(
            Organization, "王記麵店", Taipei, ShopScenario.Reopen, Monday, null, [], null, Now, (ShopScenario.TemporaryClosure, Monday, null),
            new CoordinatorReportReservations(0, 0, [], NotRestored: 4));

        Texts(CoordinatorExecutionReport.Confirmation(content)[0]["contents"]!["body"]!).ShouldBe(
            ["已確認，逐項結果：", "取消 11/2（一） 臨時店休", "因店休取消的 4 筆訂位不會恢復，請視需要聯絡顧客。", CoordinatorExecutionReport.NothingToDo]);
    }

    [Fact]
    public void The_reminder_to_call_is_not_an_action_a_reopen_undoes_or_describes()
    {
        var rows = new[]
        {
            Row(0, ShopActionKind.LineBroadcast, "群發公告。"),
            Row(1, ShopActionKind.ReservationCalls, CoordinatorReservationClosures.CallsSteps),
        };

        CoordinatorActionSchedule.ActionsOf(rows).Select(action => action.Kind).ShouldBe([ShopActionKind.LineBroadcast]);
        var plan = CoordinatorActionSchedule.PlanReopen(
            rows, ShopScenario.TemporaryClosure, Monday, null, correction: false, null, new Dictionary<ShopActionKind, ShopActionMode>(), Now);
        plan.Cancel.ShouldBeEmpty("awaiting by hand: the calls are still due");
        plan.Actions.ShouldBeEmpty();
    }

    // --- Helpers -----------------------------------------------------------------------------------------

    private static CoordinatorCardContent Card(ShopScenario scenario, CoordinatorCardReservations? reservations, TimeOnly? time = null) =>
        new(
            Organization, Command, 1, "王記麵店", scenario, Monday, time,
            CoordinatorPreviewCard.Rows(Shop, scenario, Monday, time, [.. Shop[scenario].Actions.Select(_ => ShopActionMode.Reminder)]),
            "【王記麵店】11/2（一） 臨時店休。",
            Reservations: reservations);

    /// <summary>A pending (or <paramref name="status"/>) reservation of Monday 11/2 at <paramref name="hour"/>:<paramref name="minute"/>
    /// in Taipei (the small hours of the next calendar day with <paramref name="nextDay"/>).</summary>
    private static CoordinatorReservation Reservation(
        int hour,
        int minute,
        string name,
        CoordinatorReservationStatus status = CoordinatorReservationStatus.Pending,
        bool nextDay = false,
        string phone = "0912345678",
        string? email = null,
        string? note = null)
    {
        var day = nextDay ? Monday.AddDays(1) : Monday;
        var at = new DateTimeOffset(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.FromHours(8));
        var reservation = CoordinatorReservation.Submit(
            Organization, new ReservationSlot(Monday, at, at.DateTime), 2, name, phone, email, note, CoordinatorReservation.NewSubmissionKey(), at.AddDays(-3));
        Set(reservation, nameof(CoordinatorReservation.Status), status);
        return reservation;
    }

    /// <summary>As the persistence layer's compare-and-set leaves it (#392).</summary>
    private static CoordinatorReservation Cancelled(CoordinatorReservation reservation, ShopScenario scenario)
    {
        Set(reservation, nameof(CoordinatorReservation.Status), CoordinatorReservationStatus.Cancelled);
        Set(reservation, nameof(CoordinatorReservation.CancelReason), (ShopScenario?)scenario);
        Set(reservation, nameof(CoordinatorReservation.CancelledByCommandId), (Guid?)Command);
        return reservation;
    }

    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(target, value);

    private static CoordinatorCommandAction Row(int position, ShopActionKind kind, string steps) =>
        CoordinatorCommandAction.Create(
            Organization, Command, position, kind, null, CoordinatorActionStep.Apply, ShopActionMode.Reminder, Now, steps, null, Owner, null, Now);

    private static CoordinatorReportLine Line(ShopActionKind kind, string steps) =>
        new(Guid.CreateVersion7(), kind, null, CoordinatorActionStep.Apply, ShopActionMode.Reminder, CoordinatorActionStatus.AwaitingManual, Now, steps,
            null, null, ShopScenario.CloseEarly, Monday, new TimeOnly(17, 0));

    private static CoordinatorReportContent Content(IReadOnlyList<CoordinatorReportLine> lines, CoordinatorReportReservations? reservations = null) =>
        new(Organization, "王記麵店", Taipei, ShopScenario.CloseEarly, Monday, new TimeOnly(17, 0), lines, "【王記麵店】11/2（一） 提早到 17:00 打烊。", Now,
            Reservations: reservations);

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
        Encoding.UTF8.GetByteCount(message.ToJsonString()).ShouldBeLessThan(30_000);
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
}
