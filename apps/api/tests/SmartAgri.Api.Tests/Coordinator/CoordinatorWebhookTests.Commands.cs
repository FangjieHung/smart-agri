using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Coordinator;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Coordinator;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// Commands and preview cards (store-coordinator design §5 Slice 3, #374) through the real webhook, processor,
/// PostgreSQL and the host's <c>Fake</c> chat model, whose tool call each message scripts with
/// <see cref="FakeChatDirectives.Query"/> — the model's reading is given, the server's checks, cards, usage and
/// compare-and-set are what is tested. 「Now」 is set to Monday 2026-11-02 10:00 in Taipei at the start of each test.
/// </summary>
public sealed partial class CoordinatorWebhookTests
{
    /// <summary>Open 11:00–14:00 and 17:00–21:00, Wednesday off, Saturday's dinner until 01:00; every scenario offered.</summary>
    private const string CommandShopFile = """
        {
          "storeName": "王記麵店",
          "timeZone": "Asia/Taipei",
          "weeklyHours": {
            "mon": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "tue": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "wed": [],
            "thu": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "fri": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }],
            "sat": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "01:00" }],
            "sun": [{ "open": "11:00", "close": "14:00" }, { "open": "17:00", "close": "21:00" }]
          },
          "scenarios": {
            "temporary-closure": {
              "enabled": true,
              "announcement": { "template": "【{店名}】{日期} 臨時店休，{恢復營業} 恢復營業。\n原因：{原因}", "imageUrl": "https://example.com/closed.jpg" },
              "actions": [
                { "kind": "line-broadcast", "mode": "auto", "steps": "群發公告。" },
                { "kind": "google-special-hours", "mode": "auto", "steps": "Google 設為休息。" },
                { "kind": "ordering", "name": "Xurf", "mode": "reminder", "pauseSteps": "關閉 Xurf 接單。", "resumeSteps": "打開 Xurf 接單。" }
              ]
            },
            "close-early": {
              "enabled": true,
              "announcement": { "template": "【{店名}】{日期} 提早到 {時間} 打烊。\n原因：{原因}" },
              "actions": [{ "kind": "line-broadcast", "mode": "auto", "steps": "群發公告。" }]
            },
            "open-late": {
              "enabled": true,
              "announcement": { "template": "【{店名}】{日期} 延後到 {時間} 開店。" },
              "actions": [{ "kind": "ordering", "name": "Xurf", "mode": "reminder", "pauseSteps": "關閉 Xurf 接單。", "resumeSteps": "打開 Xurf 接單。" }]
            },
            "reopen": { "enabled": true, "announcement": { "template": "【{店名}】{日期} 照常營業。" } }
          }
        }
        """;

    /// <summary>Monday 2026-11-02 10:00 in Taipei.</summary>
    private static readonly DateTimeOffset CommandNow = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task The_six_phrases_each_get_their_preview_card_and_each_model_call_is_charged_to_the_shop()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("六句店");
        (string Phrase, string Scenario, string Date, string? Time, string Header)[] phrases =
        [
            ("今天臨時店休", "temporary-closure", "2026-11-02", null, "六句店｜11/2（一）臨時店休"),
            ("明天休息", "temporary-closure", "2026-11-03", null, "六句店｜11/3（二）臨時店休"),
            ("11/15 店休", "temporary-closure", "2026-11-15", null, "六句店｜11/15（日）臨時店休"),
            ("下週一店休", "temporary-closure", "2026-11-09", null, "六句店｜11/9（一）臨時店休"),
            ("今天提早四點打烊", "close-early", "2026-11-02", "16:00", "六句店｜11/2（一）提早打烊 16:00"),
            ("明天延後到中午開", "open-late", "2026-11-03", "12:00", "六句店｜11/3（二）延後開店 12:00"),
        ];

        foreach (var (phrase, scenario, date, time, header) in phrases)
        {
            var card = Card(await SendForMessageAsync(owner, Scripted(phrase, scenario, date, time)));
            card.Header.ShouldBe(header, phrase);
            card.AltText.ShouldBe(header.Replace("｜", " ", StringComparison.Ordinal).Replace("）", "） ", StringComparison.Ordinal) + "預覽", phrase);
            card.Buttons.Select(button => button.Label).ShouldBe(["取消", "確認"], phrase);
            card.Body.ShouldContain("已接的訂單仍需處理", phrase);
            card.Body.ShouldContain("要修改公告，回覆「改成：新的內容」", phrase);
        }

        var closure = Card(await SendForMessageAsync(owner, Scripted("今天臨時店休，家裡有事", "temporary-closure", "2026-11-02", reason: "家裡有事")));
        closure.Body.ShouldBe(
        [
            "・LINE 群發公告（提醒）",
            "・Google 特殊營業時間：11/2（一） 休息（提醒）",
            "・Xurf 暫停接單（提醒）",
            "　11/2（一） 開店前提醒關閉；11/3（二）11:00 前提醒重新開啟",
            "公告",
            "【六句店】11/2（一） 臨時店休，11/3（二）11:00 恢復營業。\n原因：家裡有事",
            "已接的訂單仍需處理",
            "要修改公告，回覆「改成：新的內容」",
        ]);

        var commands = await CommandsAsync(shop);
        commands.Count.ShouldBe(7);
        commands.ShouldAllBe(command => command.Status == CoordinatorCommandStatus.Previewed && command.Version == 1
            && command.RequestedByLineUserId == owner && command.ExpiresAt == command.CreatedAt + TimeSpan.FromMinutes(30));
        commands.Single(command => command.Scenario == ShopScenario.CloseEarly).Time.ShouldBe(new TimeOnly(16, 0));
        commands.Last().AnnouncementText.ShouldBe("【六句店】11/2（一） 臨時店休，11/3（二）11:00 恢復營業。\n原因：家裡有事");

        var invocations = await InvocationsAsync(shop);
        invocations.Count.ShouldBe(7);
        invocations.ShouldAllBe(invocation => invocation.Succeeded && invocation.AccountId == null && invocation.AssistantId == null && invocation.InputTokens > 0);
    }

    [Fact]
    public async Task An_unclear_date_is_asked_for_and_answered_by_the_date_buttons_or_in_words()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("追問店");

        var question = await SendForMessageAsync(owner, Scripted("下週休一天，家裡有事", "temporary-closure", null, reason: "家裡有事", question: "下週要休哪一天？"));

        question.GetProperty("text").GetString().ShouldBe("下週要休哪一天？");
        var items = QuickReplyActions(question);
        items.Select(item => item.GetProperty("label").GetString()).ShouldBe(["今天", "明天", "選日期"]);
        var picker = items[2];
        picker.GetProperty("type").GetString().ShouldBe("datetimepicker");
        picker.GetProperty("min").GetString().ShouldBe("2026-11-02");
        picker.GetProperty("max").GetString().ShouldBe("2027-01-31");
        var waiting = (await CommandsAsync(shop)).ShouldHaveSingleItem();
        waiting.Status.ShouldBe(CoordinatorCommandStatus.AwaitingDetails);
        waiting.Date.ShouldBeNull();
        waiting.AnnouncementText.ShouldBeNull("the reason is kept in memory, not in the database");

        // The date picker answers it: the card keeps the reason given with the command.
        var card = Card(await PostbackForMessageAsync(owner, picker.GetProperty("data").GetString()!, """{"date":"2026-11-09"}"""));
        card.Header.ShouldBe("追問店｜11/9（一）臨時店休");
        card.Body.ShouldContain("【追問店】11/9（一） 臨時店休，11/10（二）11:00 恢復營業。\n原因：家裡有事");
        var previewed = (await CommandsAsync(shop)).ShouldHaveSingleItem();
        previewed.Id.ShouldBe(waiting.Id);
        previewed.Status.ShouldBe(CoordinatorCommandStatus.Previewed);
        previewed.Date.ShouldBe(new DateOnly(2026, 11, 9));

        // The same button again: already answered.
        (await PostbackTextAsync(owner, picker.GetProperty("data").GetString()!, """{"date":"2026-11-10"}""")).ShouldBe(CoordinatorCommandReplies.AlreadyAnswered);

        // Answered in words: the next text goes to the model with the pending command.
        var second = await SendForMessageAsync(owner, Scripted("下週休一天", "temporary-closure", null, question: "下週要休哪一天？"));
        second.GetProperty("text").GetString().ShouldBe("下週要休哪一天？");
        var typed = Card(await SendForMessageAsync(owner, Scripted("週四", "temporary-closure", "2026-11-12")));
        typed.Header.ShouldBe("追問店｜11/12（四）臨時店休");
        (await CommandsAsync(shop)).Count(command => command.Status == CoordinatorCommandStatus.Previewed).ShouldBe(2);

        // 「今天」/「明天」 carry their date; a time question has a time picker.
        var early = await SendForMessageAsync(owner, Scripted("明天提早打烊", "close-early", "2026-11-03", question: null));
        early.GetProperty("text").GetString().ShouldBe("要提早到幾點打烊？");
        var timePicker = QuickReplyActions(early).ShouldHaveSingleItem();
        timePicker.GetProperty("mode").GetString().ShouldBe("time");
        var earlyCard = Card(await PostbackForMessageAsync(owner, timePicker.GetProperty("data").GetString()!, """{"time":"16:30"}"""));
        earlyCard.Header.ShouldBe("追問店｜11/3（二）提早打烊 16:30");

        var someday = await SendForMessageAsync(owner, Scripted("找一天休息", "temporary-closure", null));
        var tomorrow = QuickReplyActions(someday)[1];
        tomorrow.GetProperty("label").GetString().ShouldBe("明天");
        Card(await PostbackForMessageAsync(owner, tomorrow.GetProperty("data").GetString()!)).Header.ShouldBe("追問店｜11/3（二）臨時店休");

        // An answer that is not one drops the question.
        await SendForMessageAsync(owner, Scripted("改天再休", "temporary-closure", null));
        (await SendAsync(owner, "算了")).ShouldBe($"{CoordinatorCommandReplies.PendingDropped}\n{CoordinatorReplies.Menu}");
        (await CommandsAsync(shop)).Count(command => command.Status == CoordinatorCommandStatus.Cancelled).ShouldBe(1);
        (await CommandsAsync(shop)).ShouldNotContain(command => command.Status == CoordinatorCommandStatus.AwaitingDetails);
    }

    [Fact]
    public async Task The_server_checks_the_date_the_hours_and_the_settings_whatever_the_model_says()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("檢查店");

        (await SendAsync(owner, Scripted("11/4 店休", "temporary-closure", "2026-11-04"))).ShouldBe("11/4（三） 本來就是公休日。");
        (await CommandsAsync(shop)).ShouldBeEmpty("no card for a regular day off");

        var past = await SendForMessageAsync(owner, Scripted("11/1 店休", "temporary-closure", "2026-11-01"));
        past.GetProperty("text").GetString().ShouldBe("11/1（日） 已經過了。要休哪一天？");
        var far = await SendForMessageAsync(owner, Scripted("2/1 店休", "temporary-closure", "2027-02-01"));
        far.GetProperty("text").GetString().ShouldBe("最遠只能排到 1/31（日）。要休哪一天？");
        (await CommandsAsync(shop)).Where(command => command.Status == CoordinatorCommandStatus.AwaitingDetails)
            .ShouldAllBe(command => command.Date == null);
        await DropWaitingAsync(owner);

        var outside = await SendForMessageAsync(owner, Scripted("明天十點打烊", "close-early", "2026-11-03", "22:00"));
        outside.GetProperty("text").GetString().ShouldBe(
            "11/3（二） 的營業時間是 11:00–14:00、17:00–21:00，新的打烊時間要在 11:00 之後、21:00 之前。要提早到幾點打烊？");
        var waiting = (await CommandsAsync(shop)).Single(command => command.Status == CoordinatorCommandStatus.AwaitingDetails);
        waiting.Date.ShouldBe(new DateOnly(2026, 11, 3));
        waiting.Time.ShouldBeNull();
        await DropWaitingAsync(owner);

        var tree = System.Text.Json.Nodes.JsonNode.Parse(CommandShopFile)!.AsObject();
        tree["storeName"] = "沒開延後店";
        tree["scenarios"]!["open-late"]!["enabled"] = false;
        var (_, other) = await CommandShopAsync("沒開延後店", tree.ToJsonString());
        (await SendAsync(other, Scripted("明天延後到中午開", "open-late", "2026-11-03", "12:00"))).ShouldBe("這家店沒有開啟「延後開店」。");

        // Not a command: the menu.
        (await SendAsync(owner, "你好")).ShouldBe(CoordinatorReplies.Menu);

        // A model failure is an apology; so is a shop over its monthly limit, without a call.
        (await SendAsync(owner, $"今天臨時店休 {FakeChatDirectives.FailMidway}")).ShouldBe(CoordinatorCommandReplies.ModelUnavailable);
        var before = (await InvocationsAsync(shop)).Count;
        await using (var dbContext = _host.Postgres.CreateDbContext(shop.Id))
        {
            var row = await dbContext.Organizations.SingleAsync(organization => organization.Id == shop.Id, CancellationToken);
            row.SetMonthlyTokenLimit(1);
            await dbContext.SaveChangesAsync(CancellationToken);
        }

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await SendAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02"))).ShouldBe(CoordinatorCommandReplies.QuotaExceeded);
        (await InvocationsAsync(shop)).Count.ShouldBe(before, "no call over the limit");

        // A shop without settings is told so, before any model call.
        var bare = await _host.CreateOrganizationAsync("沒設定的店");
        var bareOwner = NewUser();
        (await SendAsync(bareOwner, await OwnerCodeAsync(bare))).ShouldBe("已綁定 沒設定的店（老闆）。");
        (await SendAsync(bareOwner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02"))).ShouldBe(CoordinatorCommandReplies.NotConfigured);
        (await InvocationsAsync(bare)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_card_is_confirmed_once_by_an_owner_or_a_manager_of_its_shop_in_its_latest_version_within_30_minutes()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("確認店");
        var manager = NewUser();
        var managerCode = BindingCodes.Generate();
        await AddCodeAsync(shop, managerCode, CoordinatorRole.Manager, _host.Clock.GetUtcNow(), TimeSpan.FromHours(24));
        (await SendAsync(manager, BindingCodes.Format(managerCode))).ShouldBe("已綁定 確認店（店長）。");
        var (_, stranger) = await CommandShopAsync("別家店");

        // The manager confirms the owner's card; a second press is refused.
        var first = Card(await SendForMessageAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02")));
        var confirm = first.Button("確認");
        (await PostbackTextAsync(stranger, confirm)).ShouldBe(CoordinatorCommandReplies.NotAMember, "a member of another shop");
        (await PostbackTextAsync(NewUser(), confirm)).ShouldBe(CoordinatorReplies.AskForCode, "someone bound nowhere");
        (await PressConfirmAsync(manager, confirm)).ShouldBe("已確認：確認店 11/2（一） 臨時店休。");
        (await PostbackTextAsync(owner, confirm)).ShouldBe(CoordinatorCommandReplies.AlreadyConfirmed);
        var confirmed = (await CommandsAsync(shop)).ShouldHaveSingleItem();
        confirmed.Status.ShouldBe(CoordinatorCommandStatus.Confirmed);
        confirmed.ConfirmedByLineUserId.ShouldBe(manager);
        confirmed.ConfirmedAt.ShouldNotBeNull();
        (await PostbackTextAsync(owner, first.Button("取消"))).ShouldBe(CoordinatorCommandReplies.AlreadyConfirmed);

        // 「改成：」 makes version 2: the old card is refused, the new one confirms.
        var second = Card(await SendForMessageAsync(owner, Scripted("明天休息", "temporary-closure", "2026-11-03")));
        var edited = Card(await SendForMessageAsync(owner, "改成：明天公休一天，週四見！"));
        edited.Body.ShouldContain("明天公休一天，週四見！");
        (await PostbackTextAsync(owner, second.Button("確認"))).ShouldBe(CoordinatorCommandReplies.Outdated);
        (await PressConfirmAsync(owner, edited.Button("確認"))).ShouldBe("已確認：確認店 11/3（二） 臨時店休。");
        var secondRow = (await CommandsAsync(shop)).Single(command => command.Date == new DateOnly(2026, 11, 3));
        secondRow.Version.ShouldBe(2);
        secondRow.AnnouncementText.ShouldBe("明天公休一天，週四見！");

        // After 30 minutes: expired.
        var third = Card(await SendForMessageAsync(owner, Scripted("11/15 店休", "temporary-closure", "2026-11-15")));
        _host.Clock.Advance(TimeSpan.FromMinutes(31));
        (await PostbackTextAsync(owner, third.Button("確認"))).ShouldBe(CoordinatorCommandReplies.Expired);
        (await CommandsAsync(shop)).Single(command => command.Date == new DateOnly(2026, 11, 15)).Status.ShouldBe(CoordinatorCommandStatus.Previewed);

        // Cancel, then nothing more.
        var fourth = Card(await SendForMessageAsync(owner, Scripted("11/16 店休", "temporary-closure", "2026-11-16")));
        (await PostbackTextAsync(stranger, fourth.Button("取消"))).ShouldBe(CoordinatorCommandReplies.NotAMember);
        (await PostbackTextAsync(owner, fourth.Button("取消"))).ShouldBe(CoordinatorCommandReplies.Cancelled);
        (await PostbackTextAsync(owner, fourth.Button("確認"))).ShouldBe(CoordinatorCommandReplies.AlreadyCancelled);

        // Two presses at the same moment confirm it once.
        var fifth = Card(await SendForMessageAsync(owner, Scripted("11/17 店休", "temporary-closure", "2026-11-17")));
        var data = fifth.Button("確認");
        var posts = await Task.WhenAll(
            PostAsync(Body(Bot.UserId, Postback(owner, data, "rt-double-1")), sign: true),
            PostAsync(Body(Bot.UserId, Postback(manager, data, "rt-double-2")), sign: true));
        posts.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        var replies = new[] { Said((await ReplyMessagesAsync("rt-double-1"))[0]), Said((await ReplyMessagesAsync("rt-double-2"))[0]) };
        replies.Count(reply => reply == "已確認：確認店 11/17（二） 臨時店休。").ShouldBe(1);
        replies.Count(reply => reply == CoordinatorCommandReplies.AlreadyConfirmed).ShouldBe(1);
    }

    [Fact]
    public async Task Kai_cheng_edits_the_latest_open_card_and_bumps_its_version()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("改公告店");

        (await SendAsync(owner, "改成：今天休息")).ShouldBe(CoordinatorCommandReplies.NoPreviewToEdit);
        Card(await SendForMessageAsync(owner, Scripted("今天臨時店休", "temporary-closure", "2026-11-02")));
        (await SendAsync(owner, "改成：  ")).ShouldBe(CoordinatorCommandReplies.EmptyEdit);
        (await SendAsync(owner, "改成：" + new string('長', 1001))).ShouldBe("公告最多 1000 字，請縮短後再傳。");

        _host.Clock.Advance(TimeSpan.FromMinutes(20));
        var edited = Card(await SendForMessageAsync(owner, "改成:今天臨時休息一天\n明天照常營業"));
        edited.Body.ShouldContain("今天臨時休息一天\n明天照常營業");
        var row = (await CommandsAsync(shop)).ShouldHaveSingleItem();
        row.Version.ShouldBe(2);
        (row.ExpiresAt - _host.Clock.GetUtcNow()).ShouldBeInRange(TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30), "30 minutes from the latest card");

        _host.Clock.Advance(TimeSpan.FromMinutes(25));
        Card(await SendForMessageAsync(owner, "改成：第三版")).Body.ShouldContain("第三版");
        (await CommandsAsync(shop)).ShouldHaveSingleItem().Version.ShouldBe(3);
        _host.Clock.Advance(TimeSpan.FromMinutes(31));
        (await SendAsync(owner, "改成：太晚了")).ShouldBe(CoordinatorCommandReplies.NoPreviewToEdit);
    }

    [Fact]
    public async Task Reopen_undoes_one_confirmed_command_picks_among_several_and_toggles_the_correction()
    {
        SetNow(CommandNow);
        var (shop, owner) = await CommandShopAsync("恢復店");

        (await SendAsync(owner, Scripted("恢復營業", "reopen", null))).ShouldBe(CoordinatorCommandReplies.NothingToReopen);

        await ConfirmAsync(owner, Scripted("11/15 店休", "temporary-closure", "2026-11-15"));
        var reopen = Card(await SendForMessageAsync(owner, Scripted("11/15 恢復營業", "reopen", "2026-11-15")));
        reopen.Header.ShouldBe("恢復店｜11/15（日）恢復營業");
        reopen.Body.ShouldBe(
        [
            "取消 11/15（日） 臨時店休",
            "・LINE 群發：已發出的公告無法收回",
            "・Google 特殊營業時間：移除 11/15（日） 的設定（提醒）",
            "・Xurf 重新開啟接單（提醒）",
            "不加發更正公告",
        ]);
        reopen.Buttons.Select(button => button.Label).ShouldBe(["加發更正公告", "取消", "確認"]);

        var toggled = Card(await PostbackForMessageAsync(owner, reopen.Button("加發更正公告")));
        toggled.Body.ShouldContain("・LINE 群發更正公告（提醒）");
        toggled.Body.ShouldContain("【恢復店】11/15（日） 照常營業。");
        toggled.Buttons.Select(button => button.Label).ShouldBe(["不加發更正公告", "取消", "確認"]);
        (await PostbackTextAsync(owner, reopen.Button("確認"))).ShouldBe(CoordinatorCommandReplies.Outdated);
        (await PressConfirmAsync(owner, toggled.Button("確認"))).ShouldBe("已確認：恢復店 11/15（日） 恢復營業。");
        var row = (await CommandsAsync(shop)).Single(command => command.Scenario == ShopScenario.Reopen);
        row.CorrectionBroadcast.ShouldBeTrue();
        row.Version.ShouldBe(2);
        row.ReopensCommandId.ShouldBe((await CommandsAsync(shop)).Single(command => command.Scenario == ShopScenario.TemporaryClosure).Id);
        (await SendAsync(owner, Scripted("恢復營業", "reopen", null))).ShouldBe(CoordinatorCommandReplies.NothingToReopen, "already reopened");

        // Several: a choice.
        await ConfirmAsync(owner, Scripted("11/20 店休", "temporary-closure", "2026-11-20"));
        await ConfirmAsync(owner, Scripted("11/21 提早五點打烊", "close-early", "2026-11-21", "17:30"));
        var choice = await SendForMessageAsync(owner, Scripted("恢復營業", "reopen", null));
        choice.GetProperty("text").GetString().ShouldBe(CoordinatorCommandReplies.ChooseReopen);
        var options = QuickReplyActions(choice);
        options.Select(option => option.GetProperty("label").GetString()).ShouldBe(["11/20（五）臨時店休", "11/21（六）提早打烊 17:30"]);
        var picked = Card(await PostbackForMessageAsync(owner, options[1].GetProperty("data").GetString()!));
        picked.Body[0].ShouldBe("取消 11/21（六） 提早打烊 17:30");

        // Two reopen cards of one command: only the first confirmation counts.
        var other = Card(await PostbackForMessageAsync(owner, options[1].GetProperty("data").GetString()!));
        (await PressConfirmAsync(owner, picked.Button("確認"))).ShouldBe("已確認：恢復店 11/21（六） 恢復營業。");
        (await PostbackTextAsync(owner, other.Button("確認"))).ShouldBe(CoordinatorCommandReplies.AlreadyReopened);

        // Without a LINE 群發 to correct there is no toggle, and a forged one changes nothing.
        await ConfirmAsync(owner, Scripted("11/24 延後到中午開", "open-late", "2026-11-24", "12:00"));
        var noToggle = Card(await SendForMessageAsync(owner, Scripted("11/24 恢復營業", "reopen", "2026-11-24")));
        noToggle.Body.ShouldBe(["取消 11/24（二） 延後開店 12:00", "・Xurf 重新開啟接單（提醒）"]);
        noToggle.Buttons.Select(button => button.Label).ShouldBe(["取消", "確認"]);
        var lastReopen = (await CommandsAsync(shop)).Last();
        lastReopen.Scenario.ShouldBe(ShopScenario.Reopen);
        (await PostbackTextAsync(owner, CoordinatorMessageRouter.CorrectionPostback(shop.Id, lastReopen.Id, 1))).ShouldBe(CoordinatorCommandReplies.Outdated);
        (await CommandsAsync(shop)).Last().ShouldSatisfyAllConditions(
            command => command.CorrectionBroadcast.ShouldBeFalse(),
            command => command.Version.ShouldBe(1));
    }

    [Fact]
    public async Task A_member_of_several_shops_chooses_one_or_all_and_each_call_is_charged_to_its_shop()
    {
        SetNow(CommandNow);
        var (first, owner) = await CommandShopAsync("甲店");
        var tree = System.Text.Json.Nodes.JsonNode.Parse(CommandShopFile)!.AsObject();
        tree["storeName"] = "乙店";
        var second = await _host.CreateOrganizationAsync("乙店");
        (await RunShopWithFileAsync(tree.ToJsonString(), "import", "--organization", second.Code)).Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess);
        (await SendAsync(owner, await OwnerCodeAsync(second))).ShouldBe("已綁定 乙店（老闆）。");

        var command = Scripted("今天臨時店休", "temporary-closure", "2026-11-02");
        var choice = await SendForMessageAsync(owner, command);
        choice.GetProperty("text").GetString().ShouldBe("哪一家？");
        var options = QuickReplyActions(choice);
        options.Select(option => option.GetProperty("label").GetString()).ShouldBe(["乙店", "甲店", "全部"], "by store name, then 全部");
        string Option(string label) => options.Single(option => option.GetProperty("label").GetString() == label).GetProperty("data").GetString()!;
        (await InvocationsAsync(first)).ShouldBeEmpty("the shop is chosen before the model is called");
        (await InvocationsAsync(second)).ShouldBeEmpty("the shop is chosen before the model is called");

        Card(await PostbackForMessageAsync(owner, Option("乙店"))).Header.ShouldBe("乙店｜11/2（一）臨時店休");
        (await InvocationsAsync(first)).ShouldBeEmpty();
        (await InvocationsAsync(second)).Count.ShouldBe(1);
        (await PostbackTextAsync(owner, Option("甲店"))).ShouldBe(CoordinatorCommandReplies.RetypeCommand, "the held text was used");

        await SendForMessageAsync(owner, command);
        var all = await PostbackForMessagesAsync(owner, Option("全部"));
        all.Select(message => Card(message).Header).ShouldBe(["乙店｜11/2（一）臨時店休", "甲店｜11/2（一）臨時店休"], ignoreOrder: true);
        (await InvocationsAsync(first)).Count.ShouldBe(1);
        (await InvocationsAsync(second)).Count.ShouldBe(2);

        // A held command waits ten minutes.
        await SendForMessageAsync(owner, command);
        _host.Clock.Advance(TimeSpan.FromMinutes(11));
        (await PostbackTextAsync(owner, Option("甲店"))).ShouldBe(CoordinatorCommandReplies.RetypeCommand);

        // 「全部」 with a question: one per shop, named.
        await SendForMessageAsync(owner, Scripted("下週休一天", "temporary-closure", null, question: "下週要休哪一天？"));
        var questions = await PostbackForMessagesAsync(owner, Option("全部"));
        questions.Select(message => message.GetProperty("text").GetString()).ShouldBe(["【乙店】下週要休哪一天？", "【甲店】下週要休哪一天？"], ignoreOrder: true);
        var answers = await SendForMessagesAsync(owner, Scripted("週四", "temporary-closure", "2026-11-12"));
        answers.Select(message => Card(message).Header).ShouldBe(["甲店｜11/12（四）臨時店休", "乙店｜11/12（四）臨時店休"], ignoreOrder: true);
    }

    [Fact]
    public async Task No_log_line_holds_what_a_member_wrote()
    {
        SetNow(CommandNow);
        var (_, owner) = await CommandShopAsync("紀錄店");
        var secret = "秘密" + Guid.NewGuid().ToString("N")[..8];
        _host.Log.Start();
        try
        {
            Card(await SendForMessageAsync(owner, Scripted($"今天臨時店休 {secret}", "temporary-closure", "2026-11-02", reason: secret)));
            Card(await SendForMessageAsync(owner, $"改成：{secret} 的新公告"));
            (await SendAsync(owner, $"{secret} {FakeChatDirectives.FailMidway}")).ShouldBe(CoordinatorCommandReplies.ModelUnavailable);
            (await SendForMessageAsync(owner, Scripted(secret, "temporary-closure", null, reason: secret, question: secret)))
                .GetProperty("text").GetString().ShouldBe(secret);
            await DropWaitingAsync(owner);
        }
        finally
        {
            _host.Log.Stop();
        }

        var lines = _host.Log.Lines;
        lines.ShouldContain(line => line.Contains("could not be understood by the model", StringComparison.Ordinal), "the failure was logged");
        lines.ShouldNotContain(line => line.Contains(secret, StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.Contains(owner, StringComparison.Ordinal));
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private void SetNow(DateTimeOffset at) => _host.Clock.Advance(at - _host.Clock.GetUtcNow());

    /// <summary>A shop with <paramref name="file"/> (default <see cref="CommandShopFile"/>, named
    /// <paramref name="name"/>) and a newly bound owner.</summary>
    private async Task<(Organization Shop, string Owner)> CommandShopAsync(string name, string? file = null)
    {
        var shop = await _host.CreateOrganizationAsync(name);
        var tree = System.Text.Json.Nodes.JsonNode.Parse(file ?? CommandShopFile)!.AsObject();
        if (file is null)
        {
            tree["storeName"] = name;
        }

        var imported = await RunShopWithFileAsync(tree.ToJsonString(), "import", "--organization", shop.Code);
        imported.Exit.ShouldBe(CoordinatorShopCommand.ExitSuccess, imported.Error);
        var owner = NewUser();
        (await SendAsync(owner, await OwnerCodeAsync(shop))).ShouldBe($"已綁定 {name}（老闆）。");
        return (shop, owner);
    }

    /// <summary><paramref name="phrase"/> with the fake model's scripted tool call.</summary>
    private static string Scripted(
        string phrase, string scenario, string? date, string? time = null, string? reason = null, string? question = null) =>
        $"{phrase} {FakeChatDirectives.Query}" + JsonSerializer.Serialize(new
        {
            name = CoordinatorCommandTool.ToolName,
            arguments = new Dictionary<string, string?>
            {
                ["scenario"] = scenario,
                ["date"] = date,
                ["time"] = time,
                ["reason"] = reason,
                ["question"] = question,
            },
        });

    /// <summary>Sends the command and presses 〔確認〕 on its card.</summary>
    private async Task ConfirmAsync(string user, string text)
    {
        var card = Card(await SendForMessageAsync(user, text));
        (await PressConfirmAsync(user, card.Button("確認"))).ShouldStartWith("已確認：");
    }

    /// <summary>Drops any question waiting for <paramref name="user"/> (an answer that is not one).</summary>
    private async Task DropWaitingAsync(string user) =>
        (await SendAsync(user, "算了")).ShouldStartWith(CoordinatorCommandReplies.PendingDropped);

    private async Task<JsonElement> SendForMessageAsync(string user, string text) =>
        (await SendForMessagesAsync(user, text)).ShouldHaveSingleItem();

    private async Task<IReadOnlyList<JsonElement>> SendForMessagesAsync(string user, string text)
    {
        var replyToken = "rt-" + Guid.NewGuid().ToString("N");
        (await PostAsync(Body(Bot.UserId, Text(user, text, replyToken)), sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ReplyMessagesAsync(replyToken);
    }

    /// <summary>Presses 〔確認〕 and returns what the first message says: the report's alt text (「已確認：…」, #375), or a
    /// refusal's text.</summary>
    private async Task<string> PressConfirmAsync(string user, string data) => Said((await PostbackForMessagesAsync(user, data))[0]);

    /// <summary>A Flex message's alt text, or a text message's text.</summary>
    private static string Said(JsonElement message) =>
        message.GetProperty("type").GetString() == "flex" ? message.GetProperty("altText").GetString()! : message.GetProperty("text").GetString()!;

    private async Task<string> PostbackTextAsync(string user, string data, string? parameters = null) =>
        (await PostbackForMessageAsync(user, data, parameters)).GetProperty("text").GetString()!;

    private async Task<JsonElement> PostbackForMessageAsync(string user, string data, string? parameters = null) =>
        (await PostbackForMessagesAsync(user, data, parameters)).ShouldHaveSingleItem();

    private async Task<IReadOnlyList<JsonElement>> PostbackForMessagesAsync(string user, string data, string? parameters = null)
    {
        var replyToken = "rt-" + Guid.NewGuid().ToString("N");
        var postback = parameters is null
            ? Postback(user, data, replyToken)
            : Event("postback", UserSource(user), replyToken, extra: $$""","postback":{"data":{{JsonSerializer.Serialize(data)}},"params":{{parameters}}}""");
        (await PostAsync(Body(Bot.UserId, postback), sign: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ReplyMessagesAsync(replyToken);
    }

    private async Task<IReadOnlyList<JsonElement>> ReplyMessagesAsync(string replyToken)
    {
        await EventuallyAsync(() => Replies(replyToken).Count > 0, $"the reply to {replyToken}");
        var reply = Replies(replyToken).ShouldHaveSingleItem();
        return [.. reply.Json.GetProperty("messages").EnumerateArray()];
    }

    private static List<JsonElement> QuickReplyActions(JsonElement message) =>
        [.. message.GetProperty("quickReply").GetProperty("items").EnumerateArray().Select(item => item.GetProperty("action"))];

    private async Task<List<CoordinatorCommand>> CommandsAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.CoordinatorCommands.AsNoTracking().OrderBy(command => command.CreatedAt).ThenBy(command => command.Id).ToListAsync(CancellationToken);
    }

    private async Task<List<ModelInvocation>> InvocationsAsync(Organization shop)
    {
        await using var dbContext = _host.Postgres.CreateDbContext(shop.Id);
        return await dbContext.ModelInvocations.AsNoTracking()
            .Where(invocation => invocation.Purpose == ModelInvocationPurpose.CoordinatorCommand)
            .ToListAsync(CancellationToken);
    }

    private static PreviewCard Card(JsonElement message)
    {
        message.GetProperty("type").GetString().ShouldBe("flex");
        var contents = message.GetProperty("contents");
        return new PreviewCard(
            message.GetProperty("altText").GetString()!,
            Texts(contents.GetProperty("header")).ShouldHaveSingleItem(),
            Texts(contents.GetProperty("body")),
            [.. Elements(contents.GetProperty("footer"))
                .Where(element => element.TryGetProperty("type", out var type) && type.GetString() == "button")
                .Select(element => (element.GetProperty("action").GetProperty("label").GetString()!, element.GetProperty("action").GetProperty("data").GetString()!))]);
    }

    private static List<string> Texts(JsonElement node) =>
        [.. Elements(node)
            .Where(element => element.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(element => element.GetProperty("text").GetString()!)];

    private static IEnumerable<JsonElement> Elements(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            yield return node;
            foreach (var property in node.EnumerateObject())
            {
                foreach (var child in Elements(property.Value))
                {
                    yield return child;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                foreach (var child in Elements(item))
                {
                    yield return child;
                }
            }
        }
    }

    private sealed record PreviewCard(string AltText, string Header, List<string> Body, List<(string Label, string Data)> Buttons)
    {
        public string Button(string label) => Buttons.Single(button => button.Label == label).Data;
    }
}
