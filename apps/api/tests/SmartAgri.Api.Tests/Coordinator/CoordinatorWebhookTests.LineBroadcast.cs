using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Secrets;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// LINE 群發 carried out automatically (store-coordinator design §5 Slice 5, #376) through the real webhook, PostgreSQL,
/// background jobs, <c>coordinator-shop line-token</c> and the real <see cref="CoordinatorLineBroadcastExecutor"/>, with
/// each shop's own official account a bot of the host's fake LINE server: the token read from standard input and
/// never printed or logged, the card's quota line and its ten-minute cache, the quota checked before sending, LINE's
/// <c>429</c>s, retries that reuse the action id as the retry key (LINE's <c>409</c> counts as sent), a refused token
/// turning the broadcast into a reminder, and the reopen's correction broadcast. 「Now」 starts at Monday 2026-11-02
/// 10:00 in Taipei (11:00 in Tokyo: LINE's friend count of 11/1 is asked first).
/// </summary>
public sealed partial class CoordinatorWebhookTests
{
    private static readonly DateOnly InsightYesterday = new(2026, 11, 1);

    [Fact]
    public async Task The_line_token_is_read_from_stdin_checked_with_LINE_stored_encrypted_and_never_printed()
    {
        var shop = await _host.CreateOrganizationAsync("權杖設定店");
        var oa = Line.AddBot("@shop" + Guid.NewGuid().ToString("N")[..6], "王記麵店 LINE");
        // Another token of the 統籌助理's own channel (e.g. reissued): its broadcast would reach every shop's owner.
        var coordinatorToken = "fake-coordinator-reissued-" + Guid.NewGuid().ToString("N");
        Line.AddBot(Bot with { AccessToken = coordinatorToken });
        (int Exit, string Output, string Error) set, refused, clear, again;
        var usage = new List<(int Exit, string Output, string Error)>();
        _host.Log.Start();
        try
        {
            set = await RunLineTokenAsync($"  {oa.AccessToken}\n", "line-token", "--organization", shop.Code.ToUpperInvariant());

            // LINE refuses the token: nothing changes.
            refused = await RunLineTokenAsync("fake-line-token-" + Guid.NewGuid().ToString("N"), "line-token", "--organization", shop.Code);

            // Never from an argument, never two words, never empty, never the 統籌助理's own account.
            usage.Add(await RunLineTokenAsync(null, "line-token", "--organization", shop.Code, oa.AccessToken));
            usage.Add(await RunLineTokenAsync(null, "line-token", "--organization", shop.Code, "--token=" + oa.AccessToken));
            usage.Add(await RunLineTokenAsync($"{oa.AccessToken} {oa.AccessToken}", "line-token", "--organization", shop.Code));
            usage.Add(await RunLineTokenAsync("\n", "line-token", "--organization", shop.Code));
            usage.Add(await RunLineTokenAsync(new string('x', CoordinatorShopCommand.MaxTokenLength + 1), "line-token", "--organization", shop.Code));
            usage.Add(await RunLineTokenAsync(coordinatorToken, "line-token", "--organization", shop.Code));
            usage.Add(await RunLineTokenAsync(oa.AccessToken, "line-token", "--organization", "no-such-shop"));

            clear = await RunLineTokenAsync(null, "line-token", "--organization", shop.Code, "--clear");
            again = await RunLineTokenAsync(null, "line-token", "--organization", shop.Code, "--clear");
        }
        finally
        {
            _host.Log.Stop();
        }

        set.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, set.Error);
        set.Output.ShouldBe($"已設定組織 {shop.Code}（權杖設定店）的 LINE 官方帳號：王記麵店 LINE（{oa.BasicId}）。設為 auto 的 LINE 群發之後會自動群發。{Environment.NewLine}");
        Line.RequestsWith(oa.AccessToken).Select(request => request.Endpoint).ShouldBe([FakeLineServer.BotInfo], "checked once; refused input never reaches LINE");

        refused.Exit.ShouldBe(CoordinatorShopCommand.ExitFailed);
        refused.Error.ShouldStartWith("LINE 不接受這個權杖（HTTP 401）");
        usage.Select(result => result.Exit).ShouldBe(
        [
            CoordinatorShopCommand.ExitUsage, CoordinatorShopCommand.ExitUsage, CoordinatorShopCommand.ExitUsage, CoordinatorShopCommand.ExitUsage,
            CoordinatorShopCommand.ExitUsage, CoordinatorShopCommand.ExitFailed, CoordinatorShopCommand.ExitFailed,
        ]);
        usage[0].Error.ShouldStartWith("line-token 不接受權杖參數");
        usage[1].Error.ShouldStartWith("不認得的參數「--token」");
        usage[2].Error.ShouldStartWith("權杖只能是一行、不含空白");
        usage[3].Error.ShouldStartWith("沒有從標準輸入讀到權杖");
        usage[5].Error.ShouldStartWith("這是統籌助理自己的官方帳號");
        usage[6].Error.ShouldStartWith("找不到組織代碼");

        clear.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, clear.Error);
        clear.Output.ShouldStartWith($"已清除組織 {shop.Code}（權杖設定店）的 LINE 權杖");
        again.Output.ShouldStartWith($"組織 {shop.Code}（權杖設定店）沒有設定 LINE 權杖，沒有變更");

        // Nothing printed or logged holds the token.
        foreach (var text in new[] { set, refused, clear, again }.Concat(usage).SelectMany(result => new[] { result.Output, result.Error }))
        {
            text.ShouldNotContain(oa.AccessToken);
            text.ShouldNotContain(coordinatorToken);
        }

        _host.Log.Lines.ShouldNotContain(line => line.Contains(oa.AccessToken, StringComparison.Ordinal));

        // Set, cleared: two activities with the service and the change only; the second clear changed nothing.
        var activities = await ConnectionActivitiesAsync(shop);
        activities.Select(activity => JsonNode.Parse(activity.Detail!)!.AsObject().ToJsonString()).ShouldBe(
            ["""{"change":"set","service":"line"}""", """{"change":"cleared","service":"line"}"""], ignoreOrder: true, "jsonb orders the keys");
        activities.ShouldAllBe(activity => activity.ActorAccountId == null);
        (await ConnectionAsync(shop)).ShouldBeNull();

        // Set again: stored encrypted, with the account's id and name; only the secret protector reads it back.
        (await RunLineTokenAsync(oa.AccessToken, "line-token", "--organization", shop.Code)).Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess);
        var connection = (await ConnectionAsync(shop)).ShouldNotBeNull();
        connection.ShouldSatisfyAllConditions(
            row => row.Service.ShouldBe(CoordinatorService.Line),
            row => row.Status.ShouldBe(CoordinatorConnectionStatus.Active),
            row => row.ExternalId.ShouldBe(oa.UserId),
            row => row.DisplayName.ShouldBe($"王記麵店 LINE（{oa.BasicId}）"),
            row => row.Secret.Ciphertext.ShouldNotContain(oa.AccessToken),
            row => row.Secret.LastFour.ShouldBe(oa.AccessToken[^4..]),
            row => row.ToString().ShouldNotContain(row.Secret.Ciphertext));
        _host.Factory.Services.GetRequiredService<ISecretProtector>()
            .Unprotect(CoordinatorConnection.PurposeOf(CoordinatorService.Line), connection.Secret)
            .ShouldBe(oa.AccessToken);
        (await ConnectionActivitiesAsync(shop)).Count.ShouldBe(3);
        new CoordinatorLineAccess(connection, oa.AccessToken).ToString().ShouldNotContain(oa.AccessToken);
    }

    [Fact]
    public async Task An_automatic_broadcast_shows_its_quota_on_the_card_and_is_sent_once_with_the_action_as_its_retry_key()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("群發店");
        var oa = await ConnectLineAsync(shop, quota: 200, used: 30);
        _host.Log.Start();
        try
        {
            var card = Card(await SendForMessageAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02")));
            card.Body.Take(3).ShouldBe(["・LINE 群發公告（自動）", "　約 150 則（好友 160－封鎖 10），本月剩 170 則", "・Google 特殊營業時間：11/2（一） 休息（提醒）"]);
            var asked = LineCalls(oa);
            asked.Select(request => request.Endpoint).ShouldBe([FakeLineServer.Quota, FakeLineServer.QuotaConsumption, FakeLineServer.FollowersInsight]);
            asked[2].Uri.Query.ShouldBe("?date=20261101", "yesterday in UTC+9");

            var confirmed = await PostbackForMessagesAsync(owner, card.Button("確認"));
            Report(confirmed[0]).Body[1].ShouldBe("⌛ LINE 群發公告：執行中…");
            await RunJobsAsync();
        }
        finally
        {
            _host.Log.Stop();
        }

        var command = (await CommandsAsync(shop)).ShouldHaveSingleItem();
        var row = (await ActionsAsync(shop))[0];
        row.ShouldSatisfyAllConditions(
            action => action.Kind.ShouldBe(ShopActionKind.LineBroadcast),
            action => action.Mode.ShouldBe(ShopActionMode.Auto),
            action => action.Status.ShouldBe(CoordinatorActionStatus.Succeeded),
            action => action.FailureCode.ShouldBeNull());
        var broadcast = Line.BroadcastsOf(oa.AccessToken).ShouldHaveSingleItem();
        broadcast.Headers["X-Line-Retry-Key"].ShouldBe(row.Id.ToString());
        broadcast.Json.EnumerateObject().Select(property => property.Name).ShouldBe(["messages", "notificationDisabled"]);
        broadcast.Json.GetProperty("notificationDisabled").GetBoolean().ShouldBeFalse();
        var messages = broadcast.Json.GetProperty("messages").EnumerateArray().ToList();
        messages.Count.ShouldBe(2);
        (messages[0].GetProperty("type").GetString(), messages[0].GetProperty("text").GetString()).ShouldBe(("text", command.AnnouncementText));
        messages[1].GetProperty("type").GetString().ShouldBe("image");
        messages[1].GetProperty("originalContentUrl").GetString().ShouldBe(ClosedImage);
        messages[1].GetProperty("previewImageUrl").GetString().ShouldBe(ClosedImage);

        Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem()).Body.ShouldBe(["執行結果：", "✅ LINE 群發公告"]);
        var lines = _host.Log.Lines;
        lines.ShouldContain(line => line.Contains("LINE 群發 sent", StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.Contains(oa.AccessToken, StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.Contains(command.AnnouncementText!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_card_says_no_limit_and_unknown_counts_reuses_its_estimate_for_ten_minutes_and_has_no_note_without_a_token()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("不限店");
        var oa = await ConnectLineAsync(shop, quota: null);
        Line.SetFollowers(oa.AccessToken, InsightYesterday, "unready");
        Line.SetFollowers(oa.AccessToken, InsightYesterday.AddDays(-1), "ready", followers: 100, blocks: 5);

        var first = Card(await SendForMessageAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02")));
        first.Body.Take(2).ShouldBe(["・LINE 群發公告（自動）", "　約 95 則（好友 100－封鎖 5），本月不限則數"]);
        LineCalls(oa).Select(request => (request.Endpoint, request.Uri.Query)).ShouldBe(
        [
            (FakeLineServer.Quota, string.Empty),
            (FakeLineServer.FollowersInsight, "?date=20261101"),
            (FakeLineServer.FollowersInsight, "?date=20261031"),
        ]);

        // Within ten minutes another card uses the same estimate: LINE is not asked.
        _host.Clock.Advance(TimeSpan.FromMinutes(9));
        var second = Card(await SendForMessageAsync(owner, Scripted("明天提早打烊", "close-early", "2026-11-03", "20:00")));
        second.Body.Take(2).ShouldBe(["・LINE 群發公告（自動）", "　約 95 則（好友 100－封鎖 5），本月不限則數"]);
        LineCalls(oa).Count.ShouldBe(3);

        // After ten minutes it is asked again: a limit now, and no friend count yet.
        Line.SetQuota(oa.AccessToken, 200);
        Line.SetConsumption(oa.AccessToken, 30);
        Line.SetFollowers(oa.AccessToken, InsightYesterday.AddDays(-1), "out_of_service");
        _host.Clock.Advance(TimeSpan.FromMinutes(2));
        var third = Card(await SendForMessageAsync(owner, Scripted("11/5 店休", "temporary-closure", "2026-11-05")));
        third.Body.Take(2).ShouldBe(["・LINE 群發公告（自動）", "　則數無法估計（LINE 尚未提供好友數），本月剩 170 則"]);
        LineCalls(oa).Count(request => request.Endpoint == FakeLineServer.Quota).ShouldBe(2);

        // A shop without a token: 提醒, no note, nothing asked.
        var (plain, plainOwner) = await CommandShopAsync("沒權杖店");
        var bare = Card(await SendForMessageAsync(plainOwner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02")));
        bare.Body.Take(2).ShouldBe(["・LINE 群發公告（提醒）", "・Google 特殊營業時間：11/2（一） 休息（提醒）"]);
        (await ConnectionAsync(plain)).ShouldBeNull();
    }

    [Fact]
    public async Task Not_enough_quota_never_calls_broadcast_and_LINEs_monthly_limit_fails_with_its_reason()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("額度店");
        var oa = await ConnectLineAsync(shop, quota: 200, used: 100);
        var card = Card(await SendForMessageAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02")));
        card.Body.Take(2).ShouldBe(["・LINE 群發公告（自動）", "　額度不足，無法群發：約 150 則（好友 160－封鎖 10），本月剩 100 則"]);
        (await PressConfirmAsync(owner, card.Button("確認"))).ShouldStartWith("已確認：");
        await RunJobsAsync();

        LineCalls(oa).ShouldNotContain(request => request.Endpoint == FakeLineServer.Broadcast);
        (await ActionsAsync(shop))[0].ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorActionStatus.Failed),
            row => row.FailureCode.ShouldBe(CoordinatorFailureCodes.QuotaInsufficient));
        var report = Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem());
        report.Body.ShouldBe(["執行結果：", "❌ LINE 群發公告：LINE 本月剩餘額度不足，沒有群發"]);
        report.Buttons.Select(button => button.Label).ShouldBe(["重試"]);

        // LINE has no friend count, so it is tried; LINE refuses it with the monthly limit: nothing is sent.
        var (full, fullOwner) = await CommandShopAsync("月上限店");
        var fullOa = await ConnectLineAsync(full, quota: 200, used: 0);
        Line.SetFollowers(fullOa.AccessToken, null, "unready");
        Line.Script(fullOa.AccessToken, FakeLineServer.Broadcast, LineBehavior.MonthlyLimit);
        await ConfirmAsync(fullOwner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02"));
        await RunJobsAsync();

        LineCalls(fullOa).Count(request => request.Endpoint == FakeLineServer.Broadcast).ShouldBe(1);
        Line.BroadcastsOf(fullOa.AccessToken).ShouldBeEmpty();
        (await ActionsAsync(full))[0].FailureCode.ShouldBe(CoordinatorFailureCodes.QuotaExhausted);
        Report(PushedMessages(Pushes(fullOwner).ShouldHaveSingleItem()).ShouldHaveSingleItem()).Body
            .ShouldBe(["執行結果：", "❌ LINE 群發公告：LINE 本月訊息額度已用完"]);
    }

    [Fact]
    public async Task Retries_reuse_the_action_as_the_retry_key_so_a_broadcast_LINE_already_took_is_never_sent_twice()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("重送店");
        var oa = await ConnectLineAsync(shop, quota: 1000);
        Line.Script(oa.AccessToken, FakeLineServer.Broadcast, LineBehavior.RateLimited);
        await ConfirmAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02"));
        await RunJobsAsync();

        var row = (await ActionsAsync(shop))[0];
        (row.Status, row.FailureCode).ShouldBe((CoordinatorActionStatus.Failed, CoordinatorFailureCodes.RateLimited));
        Report(PushedMessages(Pushes(owner).ShouldHaveSingleItem()).ShouldHaveSingleItem()).Body
            .ShouldBe(["執行結果：", "❌ LINE 群發公告：服務忙碌中"]);
        var retry = CoordinatorMessageRouter.RetryPostback(shop.Id, row.Id);

        // 〔重試〕: LINE takes it this time, but its answer is lost.
        Line.Script(oa.AccessToken, FakeLineServer.Broadcast, LineBehavior.AcceptThenTimeOut);
        var lost = "rt-lost-" + Guid.NewGuid().ToString("N");
        await PressSilentlyAsync(owner, retry, lost);
        await RunJobsAsync();
        Report((await ReplyMessagesAsync(lost)).ShouldHaveSingleItem()).Body.ShouldBe(["執行結果：", "❌ LINE 群發公告：LINE 暫時無法使用"]);
        Line.BroadcastsOf(oa.AccessToken).Count.ShouldBe(1, "LINE did send it");

        // 〔重試〕 again: the same key, LINE's 409 — recorded as sent, nothing sent twice.
        Line.ResetScripts(oa.AccessToken);
        var sent = "rt-sent-" + Guid.NewGuid().ToString("N");
        await PressSilentlyAsync(owner, retry, sent);
        await RunJobsAsync();
        Report((await ReplyMessagesAsync(sent)).ShouldHaveSingleItem()).Body.ShouldBe(["執行結果：", "✅ LINE 群發公告"]);

        var tries = LineCalls(oa).Where(request => request.Endpoint == FakeLineServer.Broadcast).ToList();
        tries.Count.ShouldBe(3);
        tries.ShouldAllBe(request => request.Headers["X-Line-Retry-Key"] == row.Id.ToString());
        Line.BroadcastsOf(oa.AccessToken).Count.ShouldBe(1);
        (await ActionsAsync(shop))[0].ShouldSatisfyAllConditions(
            action => action.Status.ShouldBe(CoordinatorActionStatus.Succeeded),
            action => action.FailureCode.ShouldBeNull(),
            action => action.Attempts.ShouldBe(3));
    }

    [Fact]
    public async Task A_token_LINE_refuses_revokes_the_connection_and_the_broadcast_becomes_a_reminder_until_it_is_set_again()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("失效店");
        var oa = await ConnectLineAsync(shop);
        var card = Card(await SendForMessageAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02")));
        card.Body[0].ShouldBe("・LINE 群發公告（自動）");
        (await PressConfirmAsync(owner, card.Button("確認"))).ShouldStartWith("已確認：");

        Line.Script(oa.AccessToken, FakeLineServer.Broadcast, LineBehavior.Unauthorized);
        _host.Log.Start();
        try
        {
            await RunJobsAsync();
        }
        finally
        {
            _host.Log.Stop();
        }

        Line.BroadcastsOf(oa.AccessToken).ShouldBeEmpty();
        (await ActionsAsync(shop))[0].ShouldSatisfyAllConditions(
            row => row.Status.ShouldBe(CoordinatorActionStatus.AwaitingManual),
            row => row.Mode.ShouldBe(ShopActionMode.Reminder),
            row => row.FailureCode.ShouldBe(CoordinatorFailureCodes.TokenRevoked));
        (await ConnectionAsync(shop)).ShouldNotBeNull().Status.ShouldBe(CoordinatorConnectionStatus.Revoked);
        var followUp = PushedMessages(Pushes(owner).ShouldHaveSingleItem());
        followUp.Count.ShouldBe(2);
        var report = Report(followUp[0]);
        report.Body.ShouldBe(
        [
            "執行結果：",
            "⏳ LINE 群發公告", "　LINE 權杖失效，已改為手動；請平台營運者重新設定權杖", "　群發公告。", $"　圖片：{ClosedImage}",
        ]);
        report.Buttons.Select(button => button.Label).ShouldBe(["已完成"]);
        followUp[1].GetProperty("text").GetString().ShouldBe((await CommandsAsync(shop)).Single().AnnouncementText, "the announcement to copy");
        _host.Log.Lines.ShouldContain(line => line.Contains("LINE refused the token", StringComparison.Ordinal));
        _host.Log.Lines.ShouldNotContain(line => line.Contains(oa.AccessToken, StringComparison.Ordinal));

        // Revoked: the next card is a reminder and LINE is not asked.
        var asked = LineCalls(oa).Count;
        Card(await SendForMessageAsync(owner, Scripted("明天提早打烊", "close-early", "2026-11-03", "20:00"))).Body[0].ShouldBe("・LINE 群發公告（提醒）");
        LineCalls(oa).Count.ShouldBe(asked);

        // Set again: automatic again.
        Line.ResetScripts(oa.AccessToken);
        (await RunLineTokenAsync(oa.AccessToken, "line-token", "--organization", shop.Code)).Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess);
        Card(await SendForMessageAsync(owner, Scripted("11/5 店休", "temporary-closure", "2026-11-05"))).Body[0].ShouldBe("・LINE 群發公告（自動）");

        // Refused while a card is made: revoked there, and that card already says 提醒.
        _host.Clock.Advance(CoordinatorLineEstimateCache.Lifetime);
        Line.Script(oa.AccessToken, FakeLineServer.Quota, LineBehavior.Unauthorized);
        var refused = Card(await SendForMessageAsync(owner, Scripted("11/6 店休", "temporary-closure", "2026-11-06")));
        refused.Body.Take(2).ShouldBe(["・LINE 群發公告（提醒）", "・Google 特殊營業時間：11/6（五） 休息（提醒）"]);
        (await ConnectionAsync(shop)).ShouldNotBeNull().Status.ShouldBe(CoordinatorConnectionStatus.Revoked);
    }

    [Fact]
    public async Task A_reopen_broadcasts_the_correction_announcement_only_when_it_is_toggled()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("更正店");
        var oa = await ConnectLineAsync(shop, quota: 1000);
        await ConfirmAsync(owner, Scripted("11/15 店休", "temporary-closure", "2026-11-15"));
        await RunJobsAsync();
        Line.BroadcastsOf(oa.AccessToken).ShouldHaveSingleItem();

        var reopen = Card(await SendForMessageAsync(owner, Scripted("11/15 恢復營業", "reopen", "2026-11-15")));
        reopen.Body.ShouldContain("・LINE 群發：已發出的公告無法收回");
        reopen.Body.ShouldNotContain(text => text.Contains("本月剩", StringComparison.Ordinal));
        var toggled = Card(await PostbackForMessageAsync(owner, reopen.Button("加發更正公告")));
        var line = toggled.Body.IndexOf("・LINE 群發更正公告（自動）");
        line.ShouldBeGreaterThanOrEqualTo(0);
        toggled.Body[line + 1].ShouldBe("　約 150 則（好友 160－封鎖 10），本月剩 850 則", "the first broadcast used 150");
        (await PressConfirmAsync(owner, toggled.Button("確認"))).ShouldStartWith("已確認：");
        await RunJobsAsync();

        var broadcasts = Line.BroadcastsOf(oa.AccessToken);
        broadcasts.Count.ShouldBe(2);
        var correction = (await ActionsAsync(shop)).Where(row => row.Kind == ShopActionKind.LineBroadcast).ToList()[1];
        (correction.Kind, correction.Step, correction.Status).ShouldBe((ShopActionKind.LineBroadcast, CoordinatorActionStep.Apply, CoordinatorActionStatus.Succeeded));
        broadcasts[1].Headers["X-Line-Retry-Key"].ShouldBe(correction.Id.ToString());
        broadcasts[1].Json.GetProperty("messages").EnumerateArray()
            .Select(message => (message.GetProperty("type").GetString(), message.GetProperty("text").GetString()))
            .ShouldBe([("text", "【更正店】11/15（日） 照常營業。")]);

        // Not toggled: nothing more is broadcast.
        var (other, otherOwner) = await CommandShopAsync("不更正店");
        var otherOa = await ConnectLineAsync(other, quota: 1000);
        await ConfirmAsync(otherOwner, Scripted("11/15 店休", "temporary-closure", "2026-11-15"));
        await RunJobsAsync();
        await ConfirmAsync(otherOwner, Scripted("11/15 恢復營業", "reopen", "2026-11-15"));
        await RunJobsAsync();
        Line.BroadcastsOf(otherOa.AccessToken).ShouldHaveSingleItem();
        (await ActionsAsync(other)).Count(row => row.Kind == ShopActionKind.LineBroadcast).ShouldBe(1, "no correction row");
    }

    // --- Helpers --------------------------------------------------------------------------------------

    /// <summary>A bot of the fake LINE server as <paramref name="shop"/>'s own official account, with the month's quota
    /// (<see langword="null"/>: none) and consumption, its token set with <c>coordinator-shop line-token</c>.</summary>
    private async Task<LineBot> ConnectLineAsync(Organization shop, long? quota = 200, long used = 0)
    {
        var oa = Line.AddBot("@oa" + Guid.NewGuid().ToString("N")[..6], "店家帳號");
        Line.SetQuota(oa.AccessToken, quota);
        Line.SetConsumption(oa.AccessToken, used);
        var set = await RunLineTokenAsync(oa.AccessToken + "\n", "line-token", "--organization", shop.Code);
        set.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, set.Error);
        return oa;
    }

    /// <summary>The calls made with <paramref name="oa"/>'s token since it was set (the bot-info check left out).</summary>
    private List<LineRequest> LineCalls(LineBot oa) =>
        [.. Line.RequestsWith(oa.AccessToken).Where(request => request.Endpoint != FakeLineServer.BotInfo)];

    /// <summary>Runs <c>coordinator-shop</c> with <paramref name="stdin"/> as standard input (none when
    /// <see langword="null"/>).</summary>
    private async Task<(int Exit, string Output, string Error)> RunLineTokenAsync(string? stdin, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        using var input = new StringReader(stdin ?? string.Empty);
        var exit = await CoordinatorShopCommand.RunAsync(_host.Factory.Services, args, input, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<CoordinatorConnection?> ConnectionAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorConnections.AsNoTracking().SingleOrDefaultAsync(CancellationToken);
    }

    private async Task<List<OrganizationActivity>> ConnectionActivitiesAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.OrganizationActivities
            .AsNoTracking()
            .Where(activity => activity.Action == OrganizationActivityAction.CoordinatorConnectionUpdated)
            .OrderBy(activity => activity.At)
            .ThenBy(activity => activity.Id)
            .ToListAsync(CancellationToken);
    }
}
