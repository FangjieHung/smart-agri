using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// A 臨時店休、提早打烊 or 延後開店 and the online reservations of its day (store-reservations design decision R6, Slice R4,
/// #392) through the real webhook, command flow, compare-and-set, job queue, <c>SmtpEmailSender</c> and PostgreSQL: the
/// preview card counts and lists exactly the reservations the confirmation cancels (an overnight slot and one exactly at
/// the new closing or opening included; declined ones and other days untouched) and is recomputed when it is rebuilt;
/// the confirmation cancels them once, Emails each customer with an address once (also when 〔確認〕 is pressed again, the
/// cancellation runs again or an Email is retried), and lists the others to call under a reminder with 〔已完成〕; a later
/// 〔接受〕 says why it was cancelled; a reopen restores none of them and says so; and no customer's details reach a log.
/// </summary>
public sealed partial class CoordinatorWebhookTests
{
    [Fact]
    public async Task A_closure_previews_its_reservations_cancels_exactly_them_and_tells_each_customer_once()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("店休麵店");
        (TimeOnly Time, string Name, string? Email)[] day =
        [
            (new(11, 0), "甲先生", Guest()), (new(11, 30), "乙小姐", null), (new(12, 0), "丙先生", Guest()), (new(17, 0), "丁小姐", Guest()),
            (new(18, 0), "戊先生", null), (new(18, 30), "己小姐", Guest()), (new(19, 0), "庚先生", null),
        ];
        var booked = new List<CoordinatorReservation>();
        for (var index = 0; index < day.Length; index++)
        {
            booked.Add(await SeedReservationAsync(shop, ReservationDay, day[index].Time, day[index].Name, $"091100000{index}", day[index].Email));
        }

        var declinedTo = Guest();
        var declined = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(12, 30), "辛小姐", "0922000000", declinedTo);
        var nextDay = await SeedReservationAsync(shop, ReservationDay.AddDays(1), new TimeOnly(18, 0), "壬先生", "0933000000", Guest());
        await RunJobsAsync();
        Said((await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, booked[0].Id)))[0]).ShouldStartWith("已接受：甲先生");
        Said((await PostbackForMessagesAsync(manager, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, booked[4].Id)))[0]).ShouldStartWith("已接受：戊先生");
        Said((await PostbackForMessagesAsync(manager, CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, declined.Id)))[0]).ShouldStartWith("已婉拒：辛小姐");
        await RunJobsAsync();
        Smtp.To(day[0].Email!).ShouldHaveSingleItem().Subject.ShouldBe("店休麵店：訂位已確認");

        var cardMessage = await SendForMessageAsync(owner, Scripted("11/12 臨時店休", "temporary-closure", "2026-11-12"));
        var card = Card(cardMessage);
        card.Header.ShouldBe("店休麵店｜11/12（四）臨時店休");
        card.AltText.ShouldBe("店休麵店 11/12（四） 臨時店休預覽", "no customer in the notification");
        var line = card.Body.IndexOf("・會取消 7 筆訂位（已接受 2 筆、待處理 5 筆）");
        line.ShouldBeGreaterThan(0);
        card.Body.Skip(line + 1).Take(6).ShouldBe(
            ["　11:00 甲先生", "　11:30 乙小姐", "　12:00 丙先生", "　17:00 丁小姐", "　18:00 戊先生", "　還有 2 筆"]);

        var messages = await PostbackForMessagesAsync(owner, card.Button("確認"));
        var report = Report(messages[0]);
        report.Body.ShouldBe(
        [
            "已確認，逐項結果：",
            "已取消 7 筆訂位，其中 4 筆會寄信通知顧客。",
            "⏳ LINE 群發公告", "　群發公告。", $"　圖片：{ClosedImage}",
            "⏳ Google 特殊營業時間：11/12（四） 休息", "　Google 設為休息。",
            "🕒 Xurf 暫停接單", "　11/12（四）10:30 會提醒你關閉接單",
            "🕒 Xurf 重新開啟接單", "　11/13（五）10:30 會提醒你重新開啟接單",
            "⏳ 打電話通知顧客訂位已取消", "　這些顧客沒有留 Email，請來電告知訂位已取消：",
            "　11:30 乙小姐 0911-000-001\n　18:00 戊先生 0911-000-004\n　19:00 庚先生 0911-000-006",
        ]);
        report.AltText.ShouldBe("已確認：店休麵店 11/12（四） 臨時店休。");

        // Exactly the previewed seven, from pending or accepted, with the scenario and the command.
        var command = (await CommandsAsync(shop)).Single(row => row.Status == CoordinatorCommandStatus.Confirmed);
        foreach (var reservation in booked)
        {
            var row = await ReservationOfAsync(shop, reservation.Id);
            (row.Status, row.CancelReason, row.CancelledByCommandId).ShouldBe(
                (CoordinatorReservationStatus.Cancelled, (ShopScenario?)ShopScenario.TemporaryClosure, (Guid?)command.Id), reservation.CustomerName);
            row.StatusChangedAt.ShouldNotBeNull();
        }

        (await ReservationOfAsync(shop, booked[0].Id)).ShouldSatisfyAllConditions(
            row => row.Version.ShouldBe(3, "accepted, then cancelled"),
            row => row.HandledByMemberId.ShouldNotBeNull("who accepted it is kept"));
        (await ReservationOfAsync(shop, booked[1].Id)).Version.ShouldBe(2);
        (await ReservationOfAsync(shop, declined.Id)).Status.ShouldBe(CoordinatorReservationStatus.Declined);
        (await ReservationOfAsync(shop, nextDay.Id)).Status.ShouldBe(CoordinatorReservationStatus.Pending);
        var calls = (await ActionsAsync(shop)).Last();
        (calls.Kind, calls.Step, calls.Mode, calls.Status, calls.Position, calls.RecipientLineUserId).ShouldBe(
            (ShopActionKind.ReservationCalls, CoordinatorActionStep.Apply, ShopActionMode.Reminder, CoordinatorActionStatus.AwaitingManual, 4, owner));
        calls.StepsText.ShouldBe(CoordinatorReservationClosures.CallsSteps, "whom to call is read from the reservations, never stored here");
        CancellationJobs(await EmailJobsAsync(shop)).Select(job => Payload(job).ReservationId)
            .ShouldBe([booked[0].Id, booked[2].Id, booked[3].Id, booked[5].Id], ignoreOrder: true);

        // 〔確認〕 again, and the cancellation run again (a retried start): nothing more.
        (await PressConfirmAsync(owner, card.Button("確認"))).ShouldBe(CoordinatorCommandReplies.AlreadyConfirmed);
        (await CancelAgainAsync(shop, command)).ShouldBeEmpty();
        CancellationJobs(await EmailJobsAsync(shop)).Count.ShouldBe(4);

        await RunJobsAsync();
        await RunJobsAsync();
        var lunch = Smtp.To(day[2].Email!).ShouldHaveSingleItem();
        lunch.FromHeader.ShouldBe($"店休麵店 <{CoordinatorHostFixture.SmtpFrom}>");
        lunch.Subject.ShouldBe("店休麵店：訂位已取消");
        lunch.Body.ShouldBe(
            "很抱歉，店休麵店因臨時店休，必須取消您 11/12（四） 12:00 的訂位，造成不便，敬請見諒。如有問題請來電 02-2345-6789。\n\n" + Footer("店休麵店"));
        Smtp.To(day[0].Email!).Select(message => message.Subject).ShouldBe(["店休麵店：訂位已確認", "店休麵店：訂位已取消"]);
        new[] { day[3].Email!, day[5].Email! }.ShouldAllBe(address => Smtp.To(address).Count == 1);
        Smtp.To(declinedTo).ShouldHaveSingleItem().Subject.ShouldBe("店休麵店：無法接受您的訂位");
        CancellationJobs(await EmailJobsAsync(shop)).ShouldAllBe(job => job.Status == BackgroundJobStatus.Succeeded && job.Attempts == 1);

        // 〔已完成〕 once the calls are made; a member's later 〔接受〕 says why.
        (await PostbackTextAsync(manager, report.Buttons[^1].Data)).ShouldBe("已記錄：打電話通知顧客訂位已取消 已完成。");
        (await ActionsAsync(shop)).Last().ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorActionStatus.DoneManually),
            row => row.CompletedByLineUserId.ShouldBe(manager));
        (await PostbackTextAsync(owner, report.Buttons[^1].Data)).ShouldBe(CoordinatorExecutionReport.AlreadyRecorded);
        (await PostbackTextAsync(manager, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, booked[1].Id))).ShouldBe("這筆訂位因臨時店休已取消。");

        // 恢復營業 restores none of them, says so on the card and in the report, and leaves the calls as they are.
        var reopen = Card(await SendForMessageAsync(owner, Scripted("11/12 恢復營業", "reopen", "2026-11-12")));
        reopen.Body.ShouldContain("・因店休取消的 7 筆訂位不會恢復，請視需要聯絡顧客");
        reopen.Body.ShouldNotContain("・打電話通知顧客訂位已取消（提醒）");
        var reopened = Report((await PostbackForMessagesAsync(owner, reopen.Button("確認")))[0]);
        reopened.Body.Take(3).ShouldBe(["已確認，逐項結果：", "取消 11/12（四） 臨時店休", "因店休取消的 7 筆訂位不會恢復，請視需要聯絡顧客。"]);
        foreach (var reservation in booked)
        {
            (await ReservationOfAsync(shop, reservation.Id)).Status.ShouldBe(CoordinatorReservationStatus.Cancelled);
        }

        (await ActionsAsync(shop)).Single(row => row.Kind == ShopActionKind.ReservationCalls).Status.ShouldBe(CoordinatorActionStatus.DoneManually);
        CancellationJobs(await EmailJobsAsync(shop)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task An_early_closing_takes_from_the_new_closing_on_across_midnight_and_its_card_is_recomputed()
    {
        SetNow(ReservationNow);
        var (shop, owner, manager, _) = await ReservationShopAsync("打烊麵店");
        var (onTimeTo, laterTo) = (Guest(), Guest());
        var early = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(22, 30), "早客", "0911000010", Guest());
        var onTime = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(23, 0), "準時", "0911000011", onTimeTo);
        var supper = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(0, 30), "宵夜", "0911000012", email: null);
        var declined = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(23, 30), "婉拒", "0911000013", Guest());
        await RunJobsAsync();
        await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.DeclineReservationPostback(shop.Id, declined.Id));

        var card = Card(await SendForMessageAsync(owner, Scripted("11/14 提早到 23:00 打烊", "close-early", "2026-11-14", "23:00")));
        card.Header.ShouldBe("打烊麵店｜11/14（六）提早打烊 23:00");
        var line = card.Body.IndexOf("・會取消 2 筆訂位（已接受 0 筆、待處理 2 筆）");
        card.Body.Skip(line + 1).Take(2).ShouldBe(["　23:00 準時", "　00:30（隔天） 宵夜"], "the one exactly at the new closing, and the small hours");

        // A reservation arrives meanwhile: the card rebuilt by 「改成：」 counts it, and only the newest card confirms.
        var later = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(0, 0), "後到", "0911000014", laterTo);
        var edited = Card(await SendForMessageAsync(owner, "改成：【打烊麵店】11/14 提早到 23:00 打烊，敬請見諒。"));
        var editedLine = edited.Body.IndexOf("・會取消 3 筆訂位（已接受 0 筆、待處理 3 筆）");
        edited.Body.Skip(editedLine + 1).Take(3).ShouldBe(["　23:00 準時", "　00:00（隔天） 後到", "　00:30（隔天） 宵夜"]);
        (await PressConfirmAsync(owner, card.Button("確認"))).ShouldBe(CoordinatorCommandReplies.Outdated);

        _host.Log.Start();
        try
        {
            var report = Report((await PostbackForMessagesAsync(owner, edited.Button("確認")))[0]);
            report.Body.ShouldContain("已取消 3 筆訂位，其中 2 筆會寄信通知顧客。");
            report.Body.ShouldContain("　00:30（隔天） 宵夜 0911-000-012");
            await RunJobsAsync();
        }
        finally
        {
            _host.Log.Stop();
        }

        (await ReservationOfAsync(shop, early.Id)).Status.ShouldBe(CoordinatorReservationStatus.Pending, "before the new closing");
        (await ReservationOfAsync(shop, declined.Id)).Status.ShouldBe(CoordinatorReservationStatus.Declined);
        foreach (var taken in new[] { onTime, later, supper })
        {
            (await ReservationOfAsync(shop, taken.Id)).CancelReason.ShouldBe(ShopScenario.CloseEarly, taken.CustomerName);
        }

        Smtp.To(onTimeTo).ShouldHaveSingleItem().Body.ShouldStartWith(
            "很抱歉，打烊麵店因當天提早在 23:00 打烊，必須取消您 11/14（六） 23:00 的訂位，造成不便，敬請見諒。如有問題請來電 02-2345-6789。\n");
        Smtp.To(laterTo).ShouldHaveSingleItem().Body.ShouldContain("必須取消您 11/14（六） 00:00（隔天） 的訂位");
        (await PostbackTextAsync(manager, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, supper.Id)))
            .ShouldBe("這筆訂位因當天提早在 23:00 打烊已取消。");

        var lines = _host.Log.Lines;
        lines.ShouldContain(line => line.Contains("3 reservations cancelled (2 to be told by Email)", StringComparison.Ordinal));
        lines.ShouldContain(line => line.Contains("Reservation Email (cancelled) for reservation", StringComparison.Ordinal) && line.Contains("sent (attempt 1)", StringComparison.Ordinal));
        foreach (var secret in new[] { "準時", "後到", "宵夜", "0911000011", "0911-000-012", "0911000014", onTimeTo, laterTo, owner, manager })
        {
            lines.ShouldNotContain(line => line.Contains(secret, StringComparison.Ordinal), secret);
        }
    }

    [Fact]
    public async Task A_late_opening_takes_what_is_before_the_new_opening_and_a_retried_Email_is_sent_once()
    {
        SetNow(ReservationNow);
        var (shop, owner, _, _) = await ReservationShopAsync("晚開麵店");
        var to = Guest();
        var first = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(11, 0), "早到", "0911000020", to);
        var second = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(11, 30), "也早", "0911000021", email: null);
        var atOpening = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(12, 0), "剛好", "0911000022", Guest());
        var dinner = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 0), "晚餐", "0911000023", Guest());
        await RunJobsAsync();

        var card = Card(await SendForMessageAsync(owner, Scripted("11/12 延後到中午開", "open-late", "2026-11-12", "12:00")));
        card.Body.ShouldContain("・會取消 2 筆訂位（已接受 0 筆、待處理 2 筆）");
        card.Body.ShouldContain("　11:00 早到");
        card.Body.ShouldNotContain("　12:00 剛好");

        Smtp.FailNext(1);
        try
        {
            var report = Report((await PostbackForMessagesAsync(owner, card.Button("確認")))[0]);
            report.Body.ShouldBe(
            [
                "已確認，逐項結果：",
                "已取消 2 筆訂位，其中 1 筆會寄信通知顧客。",
                "🕒 Xurf 暫停接單", "　11/12（四）10:30 會提醒你關閉接單",
                "🕒 Xurf 重新開啟接單", "　11/12（四）11:30 會提醒你重新開啟接單",
                "⏳ 打電話通知顧客訂位已取消", "　這些顧客沒有留 Email，請來電告知訂位已取消：", "　11:30 也早 0911-000-021",
            ]);
            await RunJobsAsync();
            var job = CancellationJobs(await EmailJobsAsync(shop)).ShouldHaveSingleItem();
            (job.Status, job.Attempts).ShouldBe((BackgroundJobStatus.Queued, 1), "the first try failed: retried later");
            _host.Clock.Advance(TimeSpan.FromSeconds(31));
            await RunJobsAsync();
            await RunJobsAsync();
        }
        finally
        {
            Smtp.FailNext(0);
        }

        Smtp.To(to).ShouldHaveSingleItem().Body.ShouldStartWith("很抱歉，晚開麵店因當天延後到 12:00 開店，必須取消您 11/12（四） 11:00 的訂位");
        CancellationJobs(await EmailJobsAsync(shop)).ShouldHaveSingleItem().Status.ShouldBe(BackgroundJobStatus.Succeeded);
        (await ReservationOfAsync(shop, first.Id)).Status.ShouldBe(CoordinatorReservationStatus.Cancelled);
        (await ReservationOfAsync(shop, second.Id)).Status.ShouldBe(CoordinatorReservationStatus.Cancelled);
        (await ReservationOfAsync(shop, atOpening.Id)).Status.ShouldBe(CoordinatorReservationStatus.Pending, "exactly at the new opening");
        (await ReservationOfAsync(shop, dinner.Id)).Status.ShouldBe(CoordinatorReservationStatus.Pending);
    }

    [Fact]
    public async Task Without_SMTP_everyone_is_to_be_called_without_reservations_nothing_changes_and_an_earlier_acceptance_is_not_mailed()
    {
        SetNow(ReservationNow);
        var (shop, owner, _, _) = await ReservationShopAsync("無信麵店");

        // No reservation that day: no line on the card, no summary, no reminder.
        var quiet = Card(await SendForMessageAsync(owner, Scripted("11/13 臨時店休", "temporary-closure", "2026-11-13")));
        quiet.Body.ShouldNotContain(text => text.Contains("訂位", StringComparison.Ordinal));
        var quietReport = Report((await PostbackForMessagesAsync(owner, quiet.Button("確認")))[0]);
        quietReport.Body.ShouldNotContain(text => text.Contains("訂位", StringComparison.Ordinal));
        (await ActionsAsync(shop)).ShouldNotContain(row => row.Kind == ShopActionKind.ReservationCalls);

        // Accepted with SMTP set (its Email queued, not sent yet), then cancelled: only the cancellation is sent.
        var acceptedTo = Guest();
        var accepted = await SeedReservationAsync(shop, ReservationDay, new TimeOnly(18, 0), "先接受", "0911000030", acceptedTo);
        await RunJobsAsync();
        Said((await PostbackForMessagesAsync(owner, CoordinatorMessageRouter.AcceptReservationPostback(shop.Id, accepted.Id)))[0])
            .ShouldEndWith("會寄確認信給顧客。");
        var closure = Card(await SendForMessageAsync(owner, Scripted("11/12 臨時店休", "temporary-closure", "2026-11-12")));
        closure.Body.ShouldContain("・會取消 1 筆訂位（已接受 1 筆、待處理 0 筆）");
        Report((await PostbackForMessagesAsync(owner, closure.Button("確認")))[0]).Body.ShouldContain("已取消 1 筆訂位，其中 1 筆會寄信通知顧客。");
        await RunJobsAsync();
        Smtp.To(acceptedTo).ShouldHaveSingleItem().Subject.ShouldBe("無信麵店：訂位已取消");
        (await EmailJobsAsync(shop)).Select(job => (Payload(job).Outcome, job.Status)).ShouldBe(
            [(CoordinatorReservationStatus.Accepted, BackgroundJobStatus.Succeeded), (CoordinatorReservationStatus.Cancelled, BackgroundJobStatus.Succeeded)],
            "the acceptance's job found it cancelled and sent nothing");

        // Without SMTP: nobody is mailed, everyone is listed to call.
        var withAddress = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(18, 0), "有信", "0911000031", Guest());
        var without = await SeedReservationAsync(shop, OvernightDay, new TimeOnly(0, 30), "沒信", "0911000032", email: null);
        await RunJobsAsync();
        _host.SmtpConfigured = false;
        try
        {
            var card = Card(await SendForMessageAsync(owner, Scripted("11/14 臨時店休", "temporary-closure", "2026-11-14")));
            card.Body.ShouldContain("・會取消 2 筆訂位（已接受 0 筆、待處理 2 筆）");
            var report = Report((await PostbackForMessagesAsync(owner, card.Button("確認")))[0]);
            report.Body.ShouldContain("已取消 2 筆訂位。");
            report.Body.ShouldContain("　平台沒有設定寄信，請來電告知這些顧客訂位已取消：");
            report.Body.ShouldContain("　18:00 有信 0911-000-031\n　00:30（隔天） 沒信 0911-000-032");
            await RunJobsAsync();
        }
        finally
        {
            _host.SmtpConfigured = true;
        }

        CancellationJobs(await EmailJobsAsync(shop)).Count.ShouldBe(1, "only the one queued while SMTP was set");
        (await ReservationOfAsync(shop, withAddress.Id)).Status.ShouldBe(CoordinatorReservationStatus.Cancelled);
        (await ReservationOfAsync(shop, without.Id)).Status.ShouldBe(CoordinatorReservationStatus.Cancelled);
    }

    // --- Forms checked before a closure was confirmed --------------------------------------------------

    [Fact]
    public async Task A_form_shown_before_a_closure_is_refused_with_the_reason_and_nothing_is_stored()
    {
        SetNow(ReservationNow);
        var (shop, owner, _, slug) = await ReservationShopAsync("先看麵店");
        var thursday = await _client.GetStringAsync($"/reserve/{slug}?date=2026-11-12", CancellationToken);
        var friday = await _client.GetStringAsync($"/reserve/{slug}?date=2026-11-13", CancellationToken);
        var fridayLater = await _client.GetStringAsync($"/reserve/{slug}?date=2026-11-13", CancellationToken);
        thursday.ShouldContain("2026-11-12T18:30");
        friday.ShouldContain("2026-11-13T20:00");

        await ConfirmAsync(owner, Scripted("11/12 臨時店休", "temporary-closure", "2026-11-12"));
        await ConfirmAsync(owner, Scripted("11/13 提早到 19:00 打烊", "close-early", "2026-11-13", "19:00"));

        var closed = await PostFormAsync(slug, KeyOf(thursday), "2026-11-12", "2026-11-12T18:30", "0911000040");
        closed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var closedPage = await closed.Content.ReadAsStringAsync(CancellationToken);
        closedPage.ShouldContain("這個時段已經無法訂位（店家剛宣布臨時店休），請改選其他時間。");
        closedPage.ShouldNotContain("value=\"2026-11-12\"", Case.Sensitive, "the times anew: that day is gone");
        var early = await PostFormAsync(slug, KeyOf(friday), "2026-11-13", "2026-11-13T20:00", "0911000041");
        early.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await early.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("這個時段已經無法訂位（店家剛宣布當天提早在 19:00 打烊），請改選其他時間。");
        (await ReservationsOfAsync(shop)).ShouldBeEmpty();
        (await ReservationJobsAsync(shop)).ShouldBeEmpty("no card job, no expiry job");

        // Before the new closing, still offered: stored.
        (await PostFormAsync(slug, KeyOf(fridayLater), "2026-11-13", "2026-11-13T17:30", "0911000042")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReservationsOfAsync(shop)).ShouldHaveSingleItem().ReservedAt.ShouldBe(new DateTimeOffset(2026, 11, 13, 17, 30, 0, TimeSpan.FromHours(8)));

        // The store's own check, under the lock: refused while the command is in effect, stored once a reopen undid it.
        await using (var scope = _host.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<CoordinatorReservations>();
            var settings = (await scope.ServiceProvider.GetRequiredService<CoordinatorShops>().FindAsync(shop.Id, CancellationToken))!;
            var gone = await store.SubmitAsync(Unchecked(shop, settings, new DateOnly(2026, 11, 13), new TimeOnly(20, 0), "0911000043"), 60, _host.Clock.GetUtcNow(), CancellationToken);
            (gone.Outcome, gone.Reservation, gone.TakenBy?.Scenario, gone.TakenBy?.Time)
                .ShouldBe((ReservationSubmitOutcome.SlotGone, (CoordinatorReservation?)null, (ShopScenario?)ShopScenario.CloseEarly, (TimeOnly?)new TimeOnly(19, 0)));

            var reopen = Card(await SendForMessageAsync(owner, Scripted("11/13 恢復營業", "reopen", "2026-11-13")));
            (await PressConfirmAsync(owner, reopen.Button("確認"))).ShouldStartWith("已確認：");
            (await store.SubmitAsync(Unchecked(shop, settings, new DateOnly(2026, 11, 13), new TimeOnly(20, 0), "0911000044"), 60, _host.Clock.GetUtcNow(), CancellationToken))
                .Outcome.ShouldBe(ReservationSubmitOutcome.Stored, "a confirmed reopen gives the time back");
        }
    }

    [Fact]
    public async Task A_form_checked_just_before_a_closure_is_confirmed_is_refused_when_it_is_stored()
    {
        SetNow(ReservationNow);
        var (shop, owner, _, slug) = await ReservationShopAsync("同時麵店");
        var page = await _client.GetStringAsync($"/reserve/{slug}?date=2026-11-12", CancellationToken);
        var card = Card(await SendForMessageAsync(owner, Scripted("11/12 臨時店休", "temporary-closure", "2026-11-12")));
        var replyToken = "rt-race-" + Guid.NewGuid().ToString("N");
        Task<HttpResponseMessage> submitting;

        _host.Log.Start();
        try
        {
            // Hold the shop's settings row as a slow transaction would: the form passes the page's check (nothing confirmed
            // yet) and waits for the lock; then the closure is confirmed, and its cancellation waits for the lock too.
            await using (var holder = _host.Postgres.CreateDbContext(shop.Id))
            {
                await using var transaction = await holder.Database.BeginTransactionAsync(CancellationToken);
                await holder.CoordinatorShopSettings.ExecuteUpdateAsync(
                    setters => setters.SetProperty(settings => settings.UpdatedAt, settings => settings.UpdatedAt), CancellationToken);
                submitting = PostFormAsync(slug, KeyOf(page), "2026-11-12", "2026-11-12T18:30", "0911000050");
                await LockWaitersAsync(shop, 1);
                (await PostAsync(Body(Bot.UserId, Postback(owner, card.Button("確認"), replyToken)), sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
                await LockWaitersAsync(shop, 2);
                (await CommandsAsync(shop)).ShouldHaveSingleItem().Status.ShouldBe(CoordinatorCommandStatus.Confirmed);
                await transaction.CommitAsync(CancellationToken);
            }

            var refused = await submitting;
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("這個時段已經無法訂位（店家剛宣布臨時店休），請改選其他時間。");
            Report((await ReplyMessagesAsync(replyToken))[0]).Body.ShouldNotContain(text => text.Contains("訂位", StringComparison.Ordinal));
        }
        finally
        {
            _host.Log.Stop();
        }

        (await ReservationsOfAsync(shop)).ShouldBeEmpty();
        (await ReservationJobsAsync(shop)).ShouldBeEmpty("no card job");
        _host.Log.Lines.ShouldContain(line => line.Contains("was refused: a temporary-closure was confirmed while it was being stored", StringComparison.Ordinal));
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static string KeyOf(string page) => Regex.Match(page, "name=\"key\" value=\"([^\"]+)\"").Groups[1].Value;

    private Task<HttpResponseMessage> PostFormAsync(string slug, string key, string date, string time, string phone) =>
        _client.PostAsync(
            $"/reserve/{slug}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["key"] = key, ["date"] = date, ["time"] = time, ["party"] = "2", ["name"] = "陳小姐", ["phone"] = phone, ["website"] = string.Empty,
            }),
            CancellationToken);

    /// <summary>A reservation at <paramref name="time"/> of <paramref name="date"/> as if the page had offered it.</summary>
    private CoordinatorReservation Unchecked(Organization shop, ShopSettingsDocument settings, DateOnly date, TimeOnly time, string phone)
    {
        var at = settings.Hours.OnBusinessDay(date, time);
        return CoordinatorReservation.Submit(
            shop.Id, new ReservationSlot(date, at, TimeZoneInfo.ConvertTime(at, settings.TimeZone).DateTime), 2, "陳小姐", phone, null, null,
            CoordinatorReservation.NewSubmissionKey(), _host.Clock.GetUtcNow());
    }

    /// <summary>Waits until <paramref name="count"/> sessions wait for a lock on the shop's settings row (each poll on its
    /// own connection: a transaction would keep its first snapshot of <c>pg_stat_activity</c>).</summary>
    private async Task LockWaitersAsync(Organization shop, int count)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
            var waiting = await dbContext.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() "
                    + "AND wait_event_type = 'Lock' AND query LIKE '%CoordinatorShopSettings%'")
                .SingleAsync(CancellationToken);
            if (waiting >= count)
            {
                return;
            }

            await Task.Delay(25, CancellationToken);
        }

        throw new TimeoutException($"{count} sessions never waited for the shop's settings row.");
    }


    private static List<BackgroundJob> CancellationJobs(IEnumerable<BackgroundJob> jobs) =>
        [.. jobs.Where(job => Payload(job).Outcome == CoordinatorReservationStatus.Cancelled)];

    /// <summary>The cancellation of a confirmed <paramref name="command"/> run once more, in its own transaction, as a
    /// retried start would.</summary>
    private async Task<IReadOnlyList<CoordinatorReservation>> CancelAgainAsync(Organization shop, CoordinatorCommand command)
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var settings = (await scope.ServiceProvider.GetRequiredService<CoordinatorShops>().FindAsync(shop.Id, CancellationToken))!;
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(CancellationToken);
        var cancelled = await scope.ServiceProvider.GetRequiredService<CoordinatorReservations>()
            .CancelTakenAwayAsync(dbContext, command, settings.Hours, _host.Clock.GetUtcNow(), CancellationToken);
        await dbContext.SaveChangesAsync(CancellationToken);
        await transaction.CommitAsync(CancellationToken);
        return cancelled;
    }
}
