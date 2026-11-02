using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// Carrying confirmed commands out (store-coordinator design §5 Slice 4, #375) through the real webhook, PostgreSQL,
/// background jobs (driven by <see cref="JobRunner"/>, at the host's settable clock) and the fake LINE server: the
/// report, reminders pushed when due, 〔已完成〕, executors (<see cref="FakeCoordinatorExecutors"/>) with failures,
/// fallbacks to reminders and 〔重試〕, the follow-up (reply while the 〔重試〕 token is fresh, else push), reopen, the
/// records and the <c>coordinator-commands</c> subcommand. 「Now」 starts at Monday 2026-11-02 10:00 in Taipei.
/// </summary>
public sealed partial class CoordinatorWebhookTests
{
    private const string ClosedImage = "https://example.com/closed.jpg";

    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    private FakeCoordinatorExecutors Executors => _host.Executors;

    [Fact]
    public async Task Without_executors_every_action_is_a_reminder_reported_at_once_and_pushed_when_due()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("提醒店");
        var manager = NewUser();
        var managerCode = BindingCodes.Generate();
        await AddCodeAsync(shop, managerCode, CoordinatorRole.Manager, _host.Clock.GetUtcNow(), TimeSpan.FromHours(24));
        (await SendAsync(manager, BindingCodes.Format(managerCode))).ShouldBe("已綁定 提醒店（店長）。");
        var (_, stranger) = await CommandShopAsync("別的提醒店");

        var card = Card(await SendForMessageAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02")));
        card.Body[0].ShouldBe("・LINE 群發公告（提醒）", "auto in the settings, but no integration");
        var messages = await PostbackForMessagesAsync(owner, card.Button("確認"));

        messages.Count.ShouldBe(2);
        var report = Report(messages[0]);
        report.AltText.ShouldBe("已確認：提醒店 11/2（一） 臨時店休。");
        report.Header.ShouldBe("提醒店｜11/2（一）臨時店休");
        report.Body.ShouldBe(
        [
            "已確認，逐項結果：",
            "⏳ LINE 群發公告", "　群發公告。", $"　圖片：{ClosedImage}",
            "⏳ Google 特殊營業時間：11/2（一） 休息", "　Google 設為休息。",
            "🕒 Xurf 暫停接單", "　11/2（一）10:30 會提醒你關閉接單",
            "🕒 Xurf 重新開啟接單", "　11/3（二）10:30 會提醒你重新開啟接單",
        ]);
        report.Buttons.Select(button => button.Label).ShouldBe(["已完成", "已完成"]);
        messages[1].GetProperty("text").GetString().ShouldBe("【提醒店】11/2（一） 臨時店休，11/3（二）11:00 恢復營業。", "the announcement to copy");

        var command = (await CommandsAsync(shop)).ShouldHaveSingleItem();
        var rows = await ActionsAsync(shop);
        rows.Select(row => (row.Kind, row.Name, row.Step, row.Mode, row.Status)).ShouldBe(
        [
            (ShopActionKind.LineBroadcast, null, CoordinatorActionStep.Apply, ShopActionMode.Reminder, CoordinatorActionStatus.AwaitingManual),
            (ShopActionKind.GoogleSpecialHours, null, CoordinatorActionStep.Apply, ShopActionMode.Reminder, CoordinatorActionStatus.AwaitingManual),
            (ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Pause, ShopActionMode.Reminder, CoordinatorActionStatus.Scheduled),
            (ShopActionKind.Ordering, "Xurf", CoordinatorActionStep.Resume, ShopActionMode.Reminder, CoordinatorActionStatus.Scheduled),
        ]);
        rows.ShouldAllBe(row => row.CommandId == command.Id && row.RecipientLineUserId == owner && row.ReportedAt != null);
        rows.Select(row => row.StepsText).ShouldBe(["群發公告。", "Google 設為休息。", "關閉 Xurf 接單。", "打開 Xurf 接單。"]);
        rows.Select(row => row.ImageUrl).ShouldBe([ClosedImage, null, null, null]);
        rows[2].DueAt.ShouldBe(new DateTimeOffset(2026, 11, 2, 10, 30, 0, Taipei));
        rows[3].DueAt.ShouldBe(new DateTimeOffset(2026, 11, 3, 10, 30, 0, Taipei));
        (await ActionJobsAsync(shop)).Select(job => job.RunAfter).ShouldBe([rows[2].DueAt, rows[3].DueAt], "one job per scheduled row, at its due time");

        // Not due yet: nothing is pushed, and the reminder's 〔已完成〕 cannot be pressed early.
        await RunJobsAsync();
        Pushes(owner).ShouldBeEmpty();
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.CompletePostback(shop.Id, rows[3].Id))).ShouldBe(CoordinatorExecutionReport.NotManual);

        // At 10:30 the pause reminder is pushed to the member who confirmed, once.
        SetNow(new DateTimeOffset(2026, 11, 2, 10, 30, 0, Taipei));
        await RunJobsAsync();
        await RunJobsAsync();
        var push = Pushes(owner).ShouldHaveSingleItem();
        push.Headers["X-Line-Retry-Key"].ShouldBe(rows[2].Id.ToString());
        var reminder = Reminder(PushedMessages(push).ShouldHaveSingleItem());
        reminder.AltText.ShouldBe("提醒：提醒店 Xurf 暫停接單");
        reminder.Header.ShouldBe("提醒店｜提醒");
        reminder.Body.ShouldBe(["11/2（一） 臨時店休", "⏳ Xurf 暫停接單", "關閉 Xurf 接單。"]);
        var paused = (await ActionsAsync(shop))[2];
        paused.Status.ShouldBe(CoordinatorActionStatus.AwaitingManual);
        paused.Attempts.ShouldBe(1);

        // 〔已完成〕: a member of the shop, once.
        var done = reminder.Button("已完成");
        (await PostbackTextAsync(stranger, done)).ShouldBe(CoordinatorExecutionReport.NotAMember, "a member of another shop");
        (await PostbackTextAsync(NewUser(), done)).ShouldBe(CoordinatorReplies.AskForCode, "someone bound nowhere");
        (await PostbackTextAsync(manager, done)).ShouldBe("已記錄：Xurf 暫停接單 已完成。");
        (await PostbackTextAsync(owner, done)).ShouldBe(CoordinatorExecutionReport.AlreadyRecorded);
        (await PostbackTextAsync(owner, report.Buttons[0].Data)).ShouldBe("已記錄：LINE 群發公告 已完成。");
        var recorded = await ActionsAsync(shop);
        recorded[2].ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorActionStatus.DoneManually),
            row => row.CompletedByLineUserId.ShouldBe(manager),
            row => (row.CompletedAt!.Value - _host.Clock.GetUtcNow()).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1)));
        recorded[0].CompletedByLineUserId.ShouldBe(owner);
        recorded[1].Status.ShouldBe(CoordinatorActionStatus.AwaitingManual);

        // The next opening day: the resume reminder.
        SetNow(new DateTimeOffset(2026, 11, 3, 10, 30, 0, Taipei));
        await RunJobsAsync();
        Pushes(owner).Count.ShouldBe(2);
        Reminder(PushedMessages(Pushes(owner)[1]).ShouldHaveSingleItem()).Body[1].ShouldBe("⏳ Xurf 重新開啟接單");
    }

    [Fact]
    public async Task Ordering_reminders_come_before_the_opening_and_the_jobs_run_then()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("排程店");

        // 延後開店 to 12:00 on Tuesday: pause before 11:00, resume before 12:00; nothing to copy.
        var late = Card(await SendForMessageAsync(owner, Scripted("明天延後到中午開", "open-late", "2026-11-03", "12:00")));
        var lateReport = await PostbackForMessagesAsync(owner, late.Button("確認"));
        lateReport.Count.ShouldBe(1, "no LINE 群發 or FB to post by hand");
        Report(lateReport[0]).Body.ShouldBe(
        [
            "已確認，逐項結果：",
            "🕒 Xurf 暫停接單", "　11/3（二）10:30 會提醒你關閉接單",
            "🕒 Xurf 重新開啟接單", "　11/3（二）11:30 會提醒你重新開啟接單",
        ]);

        // 臨時店休 on Tuesday, Wednesday a regular day off: resume before Thursday's opening.
        var closure = Card(await SendForMessageAsync(owner, Scripted("明天店休", "temporary-closure", "2026-11-03")));
        Report((await PostbackForMessagesAsync(owner, closure.Button("確認")))[0]).Body.ShouldContain("　11/5（四）10:30 會提醒你重新開啟接單");

        var rows = (await ActionsAsync(shop)).Where(row => row.Kind == ShopActionKind.Ordering).ToList();
        rows.Select(row => row.DueAt).ShouldBe(
        [
            new DateTimeOffset(2026, 11, 3, 10, 30, 0, Taipei), new DateTimeOffset(2026, 11, 3, 11, 30, 0, Taipei),
            new DateTimeOffset(2026, 11, 3, 10, 30, 0, Taipei), new DateTimeOffset(2026, 11, 5, 10, 30, 0, Taipei),
        ]);
        (await ActionJobsAsync(shop)).Select(job => job.RunAfter).ShouldBe(rows.Select(row => row.DueAt).Order(), ignoreOrder: false);

        SetNow(new DateTimeOffset(2026, 11, 3, 10, 29, 0, Taipei));
        await RunJobsAsync();
        Pushes(owner).ShouldBeEmpty();
        SetNow(new DateTimeOffset(2026, 11, 3, 10, 30, 0, Taipei));
        await RunJobsAsync();
        Pushes(owner).Count.ShouldBe(2, "both pauses of Tuesday");
        SetNow(new DateTimeOffset(2026, 11, 3, 11, 30, 0, Taipei));
        await RunJobsAsync();
        Pushes(owner).Count.ShouldBe(3, "the late opening's resume");
        SetNow(new DateTimeOffset(2026, 11, 5, 10, 30, 0, Taipei));
        await RunJobsAsync();
        Pushes(owner).Count.ShouldBe(4, "the closure's resume on Thursday");
    }

    [Fact]
    public async Task With_executors_one_failure_spares_the_others_and_retry_reruns_only_it_with_a_follow_up()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("自動店", WithFacebook("自動店"));
        Executors.Enroll(shop.Id, ShopActionKind.LineBroadcast, FakeCoordinatorExecutors.Unavailable());
        Executors.Enroll(
            shop.Id, ShopActionKind.GoogleSpecialHours,
            FakeCoordinatorExecutors.Fail(CoordinatorFailureCodes.RateLimited),
            FakeCoordinatorExecutors.Fail(CoordinatorFailureCodes.TimedOut),
            FakeCoordinatorExecutors.Succeed("special-hours-1"));
        Executors.Enroll(shop.Id, ShopActionKind.FacebookPost, FakeCoordinatorExecutors.Succeed("post-1"));

        var card = Card(await SendForMessageAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02")));
        card.Body.Take(3).ShouldBe(["・LINE 群發公告（自動）", "・Google 特殊營業時間：11/2（一） 休息（自動）", "・FB 粉專貼文（自動）"]);
        var confirmed = await PostbackForMessagesAsync(owner, card.Button("確認"));
        confirmed.Count.ShouldBe(1, "nothing to copy while the integrations run");
        Report(confirmed[0]).Body.ShouldBe(
        [
            "已確認，逐項結果：",
            "⌛ LINE 群發公告：執行中…",
            "⌛ Google 特殊營業時間：11/2（一） 休息：執行中…",
            "⌛ FB 粉專貼文：執行中…",
            "🕒 Xurf 暫停接單", "　11/2（一）10:30 會提醒你關閉接單",
            "🕒 Xurf 重新開啟接單", "　11/3（二）10:30 會提醒你重新開啟接單",
        ]);

        // The three run; one follow-up after the last of them (pushed: the 〔確認〕 token was used by the report).
        await RunJobsAsync();
        var followUp = PushedMessages(Pushes(owner).ShouldHaveSingleItem());
        followUp.Count.ShouldBe(2);
        var results = Report(followUp[0]);
        results.AltText.ShouldBe("執行結果：自動店 11/2（一） 臨時店休");
        results.Body.ShouldBe(
        [
            "執行結果：",
            "⏳ LINE 群發公告", "　群發公告。", $"　圖片：{ClosedImage}",
            "❌ Google 特殊營業時間：11/2（一） 休息：服務忙碌中",
            "✅ FB 粉專貼文",
        ]);
        results.Buttons.Select(button => button.Label).ShouldBe(["已完成", "重試"]);
        followUp[1].GetProperty("text").GetString().ShouldBe("【自動店】11/2（一） 臨時店休，11/3（二）11:00 恢復營業。");

        var calls = Executors.Calls(shop.Id);
        calls.Select(call => call.Kind).ShouldBe(FakeCoordinatorExecutors.Kinds, ignoreOrder: true);
        var post = calls.Single(call => call.Kind == ShopActionKind.FacebookPost);
        post.Step.ShouldBe(CoordinatorActionStep.Apply);
        post.Announcement.ShouldBe("【自動店】11/2（一） 臨時店休，11/3（二）11:00 恢復營業。");
        post.ImageUrl.ShouldBe(ClosedImage);
        var rows = await ActionsAsync(shop);
        var (broadcast, google, facebook) = (rows[0], rows[1], rows[2]);
        broadcast.ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorActionStatus.AwaitingManual),
            row => row.Mode.ShouldBe(ShopActionMode.Reminder, "the integration was unavailable when it ran"));
        google.ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorActionStatus.Failed),
            row => row.FailureCode.ShouldBe(CoordinatorFailureCodes.RateLimited),
            row => row.Mode.ShouldBe(ShopActionMode.Auto));
        facebook.ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorActionStatus.Succeeded),
            row => row.ExternalRef.ShouldBe("post-1"));

        // 〔重試〕 only for the failed one: a succeeded or waiting row is never re-run.
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.RetryPostback(shop.Id, facebook.Id))).ShouldBe(CoordinatorExecutionReport.AlreadySucceeded);
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.RetryPostback(shop.Id, broadcast.Id))).ShouldBe(CoordinatorExecutionReport.RetryManual);

        // A retry whose job runs after 50 seconds: the follow-up is pushed.
        var retry = results.Button("重試");
        await PressSilentlyAsync(owner, retry, "rt-retry-late-" + Guid.NewGuid().ToString("N"));
        (await PostbackTextAsync(owner, retry)).ShouldBe(CoordinatorExecutionReport.RetryRunning);
        _host.Clock.Advance(TimeSpan.FromSeconds(51));
        await RunJobsAsync();
        Pushes(owner).Count.ShouldBe(2);
        var pushedRetry = PushedMessages(Pushes(owner)[1]).ShouldHaveSingleItem();
        Report(pushedRetry).Body.ShouldBe(["執行結果：", "❌ Google 特殊營業時間：11/2（一） 休息：服務沒有回應"]);

        // A retry whose job runs at once: the follow-up replies with the press's token.
        var token = "rt-retry-" + Guid.NewGuid().ToString("N");
        await PressSilentlyAsync(owner, retry, token);
        await RunJobsAsync();
        Report((await ReplyMessagesAsync(token)).ShouldHaveSingleItem()).Body.ShouldBe(["執行結果：", "✅ Google 特殊營業時間：11/2（一） 休息"]);
        Pushes(owner).Count.ShouldBe(2, "replied, not pushed");

        calls = Executors.Calls(shop.Id);
        calls.Count(call => call.Kind == ShopActionKind.GoogleSpecialHours).ShouldBe(3);
        calls.Count(call => call.Kind == ShopActionKind.FacebookPost).ShouldBe(1, "a success is never repeated");
        calls.Count(call => call.Kind == ShopActionKind.LineBroadcast).ShouldBe(1);
        (await ActionsAsync(shop))[1].ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorActionStatus.Succeeded),
            row => row.ExternalRef.ShouldBe("special-hours-1"),
            row => row.FailureCode.ShouldBeNull(),
            row => row.Attempts.ShouldBe(3));
    }

    [Fact]
    public async Task Reopen_drops_what_has_not_happened_and_reminds_to_undo_what_has()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("恢復執行店");
        var closure = Card(await SendForMessageAsync(owner, Scripted("11/15 店休", "temporary-closure", "2026-11-15")));
        var report = Report((await PostbackForMessagesAsync(owner, closure.Button("確認")))[0]);
        (await PostbackTextAsync(owner, report.Buttons[1].Data)).ShouldBe("已記錄：Google 特殊營業時間：11/15（日） 休息 已完成。");
        var original = await ActionsAsync(shop);

        var reopen = Card(await SendForMessageAsync(owner, Scripted("11/15 恢復營業", "reopen", "2026-11-15")));
        var toggled = Card(await PostbackForMessageAsync(owner, reopen.Button("加發更正公告")));
        var messages = await PostbackForMessagesAsync(owner, toggled.Button("確認"));

        messages.Count.ShouldBe(2);
        var result = Report(messages[0]);
        result.AltText.ShouldBe("已確認：恢復執行店 11/15（日） 恢復營業。");
        result.Body.ShouldBe(
        [
            "已確認，逐項結果：",
            "取消 11/15（日） 臨時店休",
            "🚫 Xurf 暫停接單：已取消排程",
            "🚫 Xurf 重新開啟接單：已取消排程",
            "⏳ LINE 群發更正公告", "　群發公告。",
            "⏳ Google 特殊營業時間：移除 11/15（日） 的設定", "　到 Google 商家檔案刪除 11/15（日） 的特殊營業時間。",
        ]);
        messages[1].GetProperty("text").GetString().ShouldBe("【恢復執行店】11/15（日） 照常營業。", "the correction to copy");

        var rows = await ActionsAsync(shop);
        rows.Take(4).Select(row => row.Status).ShouldBe(
        [
            CoordinatorActionStatus.AwaitingManual, CoordinatorActionStatus.DoneManually,
            CoordinatorActionStatus.Cancelled, CoordinatorActionStatus.Cancelled,
        ]);
        var reopenRows = rows.Skip(4).ToList();
        reopenRows.Select(row => (row.Kind, row.Step, row.Status, row.UndoesActionId)).ShouldBe(
        [
            (ShopActionKind.LineBroadcast, CoordinatorActionStep.Apply, CoordinatorActionStatus.AwaitingManual, (Guid?)null),
            (ShopActionKind.GoogleSpecialHours, CoordinatorActionStep.Undo, CoordinatorActionStatus.AwaitingManual, original[1].Id),
        ]);

        // The dropped reminders' jobs do nothing when their time comes.
        SetNow(new DateTimeOffset(2026, 11, 16, 11, 0, 0, Taipei));
        await RunJobsAsync();
        Pushes(owner).ShouldBeEmpty();

        // A closure whose pause was already reminded: resume now; no correction unless toggled.
        SetNow(CommandNow);
        var (today, member) = await CommandShopAsync("恢復點餐店");
        await ConfirmAsync(member, Scripted("今天臨時店休", "temporary-closure", "2026-11-02"));
        SetNow(new DateTimeOffset(2026, 11, 2, 10, 30, 0, Taipei));
        await RunJobsAsync();
        Pushes(member).Count.ShouldBe(1);
        SetNow(new DateTimeOffset(2026, 11, 2, 10, 40, 0, Taipei));
        var again = Card(await SendForMessageAsync(member, Scripted("恢復營業", "reopen", "2026-11-02")));
        var reopened = await PostbackForMessagesAsync(member, again.Button("確認"));
        reopened.Count.ShouldBe(1, "no correction, nothing to copy");
        Report(reopened[0]).Body.ShouldBe(
        [
            "已確認，逐項結果：",
            "取消 11/2（一） 臨時店休",
            "🚫 Xurf 重新開啟接單：已取消排程",
            "⏳ Google 特殊營業時間：移除 11/2（一） 的設定", "　到 Google 商家檔案刪除 11/2（一） 的特殊營業時間。",
            "⏳ Xurf 重新開啟接單", "　打開 Xurf 接單。",
        ]);
        var todayRows = await ActionsAsync(today);
        todayRows.Last().ShouldSatisfyAllConditions(
            row => row.Step.ShouldBe(CoordinatorActionStep.Resume),
            row => row.UndoesActionId.ShouldBe(todayRows[2].Id),
            row => row.Status.ShouldBe(CoordinatorActionStatus.AwaitingManual));
    }

    [Fact]
    public async Task Reopen_undoes_an_automatic_success_through_its_executor_and_drops_a_failure()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("自動恢復店", WithFacebook("自動恢復店"));
        Executors.Enroll(shop.Id, ShopActionKind.LineBroadcast, FakeCoordinatorExecutors.Succeed());
        Executors.Enroll(shop.Id, ShopActionKind.GoogleSpecialHours, FakeCoordinatorExecutors.Throw());
        Executors.Enroll(shop.Id, ShopActionKind.FacebookPost, FakeCoordinatorExecutors.Succeed("post-9"), FakeCoordinatorExecutors.Succeed());

        await ConfirmAsync(owner, Scripted("11/15 店休", "temporary-closure", "2026-11-15"));
        await RunJobsAsync();
        Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem()).Body.ShouldBe(
        [
            "執行結果：",
            "✅ LINE 群發公告",
            "❌ Google 特殊營業時間：11/15（日） 休息：系統錯誤",
            "✅ FB 粉專貼文",
        ]);
        var google = (await ActionsAsync(shop))[1];

        var reopen = Card(await SendForMessageAsync(owner, Scripted("11/15 恢復營業", "reopen", "2026-11-15")));
        reopen.Body.ShouldContain("・FB 粉專：刪除公告貼文（自動）");
        var messages = await PostbackForMessagesAsync(owner, reopen.Button("確認"));
        Report(messages.ShouldHaveSingleItem()).Body.ShouldBe(
        [
            "已確認，逐項結果：",
            "取消 11/15（日） 臨時店休",
            "🚫 Google 特殊營業時間：11/15（日） 休息：已取消排程",
            "🚫 Xurf 暫停接單：已取消排程",
            "🚫 Xurf 重新開啟接單：已取消排程",
            "⌛ FB 粉專：刪除公告貼文：執行中…",
        ]);
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.RetryPostback(shop.Id, google.Id))).ShouldBe(CoordinatorExecutionReport.RetryCancelled);

        await RunJobsAsync();
        Report(PushedMessages(Pushes(owner)[1]).ShouldHaveSingleItem()).Body.ShouldBe(["執行結果：", "取消 11/15（日） 臨時店休", "✅ FB 粉專：刪除公告貼文"]);
        var undo = Executors.Calls(shop.Id).Last();
        undo.ShouldSatisfyAllConditions(
            call => call.Kind.ShouldBe(ShopActionKind.FacebookPost),
            call => call.Step.ShouldBe(CoordinatorActionStep.Undo),
            call => call.UndoneExternalRef.ShouldBe("post-9"),
            call => call.Scenario.ShouldBe(ShopScenario.TemporaryClosure),
            call => call.Date.ShouldBe(new DateOnly(2026, 11, 15)),
            call => call.Announcement.ShouldBeNull());
        Executors.Calls(shop.Id).Count(call => call.Kind == ShopActionKind.GoogleSpecialHours).ShouldBe(1, "a dropped failure is not retried");
    }

    [Fact]
    public async Task The_records_hold_no_user_text_but_the_announcement_and_the_subcommand_masks_LINE_ids()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("紀錄執行店");
        var secret = "秘密" + Guid.NewGuid().ToString("N")[..8];
        (int Exit, string Output, string Error) listed;
        _host.Log.Start();
        try
        {
            await ConfirmAsync(owner, Scripted($"今天臨時店休 {secret}", "temporary-closure", "2026-11-02", reason: secret));
            SetNow(new DateTimeOffset(2026, 11, 2, 10, 30, 0, Taipei));
            await RunJobsAsync();
            var reminder = Reminder(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem());
            (await PostbackTextAsync(owner, reminder.Button("已完成"))).ShouldStartWith("已記錄：");
            listed = await RunCommandsCliAsync("--organization", shop.Code.ToUpperInvariant());
        }
        finally
        {
            _host.Log.Stop();
        }

        var command = (await CommandsAsync(shop)).ShouldHaveSingleItem();
        command.AnnouncementText.ShouldNotBeNull().ShouldContain(secret);
        foreach (var row in await ActionsAsync(shop))
        {
            new[] { row.StepsText, row.ImageUrl, row.FailureCode, row.ExternalRef, row.Name }
                .ShouldAllBe(text => text == null || !text.Contains(secret, StringComparison.Ordinal));
        }

        listed.Exit.ShouldBe(CoordinatorCommandsCommand.ExitSuccess, listed.Error);
        listed.Output.ShouldStartWith($"組織 {shop.Code}（紀錄執行店）最近 1 筆確認的指令（時間為 Asia/Taipei）：");
        listed.Output.ShouldContain($"2026-11-02 10:00 確認｜11/2（一） 臨時店休｜確認者 …{owner[^4..]}｜指令 {command.Id}");
        listed.Output.ShouldContain("  1. line-broadcast｜apply｜reminder｜awaiting-manual｜預定 2026-11-02 10:00");
        listed.Output.ShouldContain(
            $"  3. ordering Xurf｜pause｜reminder｜done-manually｜預定 2026-11-02 10:30｜完成 2026-11-02 10:30（…{owner[^4..]}）");
        listed.Output.ShouldContain("  4. ordering Xurf｜resume｜reminder｜scheduled｜預定 2026-11-03 10:30");
        listed.Output.ShouldNotContain(owner);
        listed.Output.ShouldNotContain(secret);
        listed.Output.ShouldNotContain("恢復營業。");

        var lines = _host.Log.Lines;
        lines.ShouldContain(line => line.Contains("Reminder of 統籌助理 action", StringComparison.Ordinal), "the push was logged");
        lines.ShouldNotContain(line => line.Contains(secret, StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.Contains(owner, StringComparison.Ordinal));

        (await RunCommandsCliAsync("--organization", "no-such-shop")).Exit.ShouldBe(CoordinatorCommandsCommand.ExitFailed);
        (await RunCommandsCliAsync("--organization", shop.Code, "--limit", "0")).Exit.ShouldBe(CoordinatorCommandsCommand.ExitUsage);
        (await RunCommandsCliAsync()).Exit.ShouldBe(CoordinatorCommandsCommand.ExitUsage);
        var help = await RunCommandsCliAsync("--help");
        (help.Exit, help.Output.StartsWith("用法：coordinator-commands", StringComparison.Ordinal)).ShouldBe((CoordinatorCommandsCommand.ExitSuccess, true));
        var bare = await _host.CreateOrganizationAsync("沒指令的店");
        (await RunCommandsCliAsync("--organization", bare.Code)).Output.ShouldContain("還沒有確認過的指令");
    }

    // --- Helpers --------------------------------------------------------------------------------------

    /// <summary><see cref="CommandShopFile"/> named <paramref name="name"/>, with an automatic FB 貼文 after Google in
    /// its closure.</summary>
    private static string WithFacebook(string name)
    {
        var tree = JsonNode.Parse(CommandShopFile)!.AsObject();
        tree["storeName"] = name;
        var actions = tree["scenarios"]!["temporary-closure"]!["actions"]!.AsArray();
        actions.Insert(2, new JsonObject { ["kind"] = "facebook-post", ["mode"] = "auto", ["steps"] = "FB 發文。" });
        return tree.ToJsonString();
    }

    private Task RunJobsAsync() => _host.Factory.Services.GetRequiredService<JobRunner>().RunUntilIdleAsync(CancellationToken);

    /// <summary>The pushes to <paramref name="user"/>, in order.</summary>
    private List<LineRequest> Pushes(string user) =>
        [.. Line.RequestsWith(Bot.AccessToken)
            .Where(request => request.Endpoint == FakeLineServer.Push && request.Json.GetProperty("to").GetString() == user)];

    private static List<JsonElement> PushedMessages(LineRequest push) => [.. push.Json.GetProperty("messages").EnumerateArray()];

    /// <summary>Presses a button that answers nothing at once (〔重試〕) with <paramref name="replyToken"/>, and waits until
    /// it was handled (a sentinel after it in the same delivery).</summary>
    private async Task PressSilentlyAsync(string user, string data, string replyToken)
    {
        var sentinel = Sentinel();
        (await PostAsync(Body(Bot.UserId, Postback(user, data, replyToken), sentinel.Event), sign: true)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        await sentinel.HandledAsync(this);
        Replies(replyToken).ShouldBeEmpty();
    }

    private async Task<List<CoordinatorCommandAction>> ActionsAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorCommandActions.AsNoTracking()
            .OrderBy(action => action.CreatedAt)
            .ThenBy(action => action.CommandId)
            .ThenBy(action => action.Position)
            .ToListAsync(CancellationToken);
    }

    private async Task<List<BackgroundJob>> ActionJobsAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.BackgroundJobs.AsNoTracking()
            .Where(job => job.Kind == CoordinatorRunActionJob.Kind)
            .OrderBy(job => job.RunAfter)
            .ToListAsync(CancellationToken);
    }

    private async Task<(int Exit, string Output, string Error)> RunCommandsCliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CoordinatorCommandsCommand.RunAsync(_host.Factory.Services, args, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>A report or follow-up: its alt text, header, every text of its body, and its buttons.</summary>
    private static PreviewCard Report(JsonElement message)
    {
        message.GetProperty("type").GetString().ShouldBe("flex");
        var contents = message.GetProperty("contents");
        return new PreviewCard(
            message.GetProperty("altText").GetString()!,
            Texts(contents.GetProperty("header")).ShouldHaveSingleItem(),
            Texts(contents.GetProperty("body")),
            [.. Elements(contents)
                .Where(element => element.TryGetProperty("type", out var type) && type.GetString() == "button")
                .Select(element => (element.GetProperty("action").GetProperty("label").GetString()!, element.GetProperty("action").GetProperty("data").GetString()!))]);
    }

    /// <summary>A pushed reminder, read like a report.</summary>
    private static PreviewCard Reminder(JsonElement message) => Report(message);
}
