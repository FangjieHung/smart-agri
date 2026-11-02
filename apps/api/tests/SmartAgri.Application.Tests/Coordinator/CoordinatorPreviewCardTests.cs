using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>
/// The preview card (#374, design §1, Q13/Q14/Q17/Q18/Q21): a LINE Flex message built purely from its content,
/// within LINE's limits, whose buttons route back to the command and version they were made for.
/// </summary>
public class CoordinatorPreviewCardTests
{
    private static readonly ShopSettingsDocument Shop = CoordinatorTestShop.Settings();
    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");
    private static readonly Guid Command = Guid.Parse("0194a100-0000-7000-8000-0000000000d1");

    [Fact]
    public void A_closure_card_lists_each_action_with_its_mode_the_announcement_and_the_two_buttons()
    {
        var date = new DateOnly(2026, 11, 2);
        var modes = Modes(ShopScenario.TemporaryClosure, ShopActionMode.Reminder);
        var content = new CoordinatorCardContent(
            Organization, Command, 1, "王記麵店", ShopScenario.TemporaryClosure, date, null,
            CoordinatorPreviewCard.Rows(Shop, ShopScenario.TemporaryClosure, date, null, modes),
            "【王記麵店】11/2（一） 臨時店休，11/3（二）11:00 恢復營業。");

        var card = CoordinatorPreviewCard.Build(content);

        card["type"]!.GetValue<string>().ShouldBe("flex");
        card["altText"]!.GetValue<string>().ShouldBe("王記麵店 11/2（一） 臨時店休預覽");
        Texts(card["contents"]!["header"]!).ShouldBe(["王記麵店｜11/2（一）臨時店休"]);
        Texts(card["contents"]!["body"]!).ShouldBe(
        [
            "・LINE 群發公告（提醒）",
            "・Google 特殊營業時間：11/2（一） 休息（提醒）",
            "・FB 粉專貼文（提醒）",
            "・Xurf 暫停接單（提醒）",
            "　11/2（一） 開店前提醒關閉；11/3（二）11:00 前提醒重新開啟",
            "・foodpanda 暫停接單（提醒）",
            "　11/2（一） 開店前提醒關閉；11/3（二）11:00 前提醒重新開啟",
            "公告",
            "【王記麵店】11/2（一） 臨時店休，11/3（二）11:00 恢復營業。",
            "已接的訂單仍需處理",
            "要修改公告，回覆「改成：新的內容」",
        ]);

        var buttons = Buttons(card);
        buttons.Select(button => button.Label).ShouldBe(["取消", "確認"]);
        Route(buttons[1].Data).ShouldBe(new CoordinatorIntent(CoordinatorIntentKind.Confirm, OrganizationId: Organization, CommandId: Command, Version: 1));
        Route(buttons[0].Data).ShouldBe(new CoordinatorIntent(CoordinatorIntentKind.Cancel, OrganizationId: Organization, CommandId: Command, Version: 1));
        WithinLimits(card);
    }

    [Fact]
    public void Automatic_actions_say_so_and_the_slice_5_note_shows_only_when_given()
    {
        var date = new DateOnly(2026, 11, 2);
        var rows = CoordinatorPreviewCard.Rows(Shop, ShopScenario.TemporaryClosure, date, null, Modes(ShopScenario.TemporaryClosure, ShopActionMode.Auto));
        rows[0].Note.ShouldBeNull("nothing fills the LINE 群發 quota line before Slice 5");
        var withNote = rows.Select((row, index) => index == 0 ? row with { Note = "將用掉 160 則、本月剩 200 則" } : row).ToList();

        var plain = CoordinatorPreviewCard.Build(Content(ShopScenario.TemporaryClosure, date, null, rows, "公告"));
        var noted = CoordinatorPreviewCard.Build(Content(ShopScenario.TemporaryClosure, date, null, withNote, "公告"));

        Texts(plain["contents"]!["body"]!).ShouldContain("・LINE 群發公告（自動）");
        Texts(plain["contents"]!["body"]!).ShouldNotContain(text => text.Contains("則", StringComparison.Ordinal));
        Texts(noted["contents"]!["body"]!).ShouldContain("　將用掉 160 則、本月剩 200 則");
    }

    [Fact]
    public void Close_early_and_open_late_show_their_time_and_when_ordering_is_reminded()
    {
        var date = new DateOnly(2026, 11, 2);
        var early = CoordinatorPreviewCard.Build(Content(
            ShopScenario.CloseEarly, date, new TimeOnly(16, 0),
            CoordinatorPreviewCard.Rows(Shop, ShopScenario.CloseEarly, date, new TimeOnly(16, 0), Modes(ShopScenario.CloseEarly, ShopActionMode.Reminder)),
            "提早打烊"));

        early["altText"]!.GetValue<string>().ShouldBe("王記麵店 11/2（一） 提早打烊 16:00預覽");
        Texts(early["contents"]!["header"]!).ShouldBe(["王記麵店｜11/2（一）提早打烊 16:00"]);
        Texts(early["contents"]!["body"]!).ShouldContain("・Google 特殊營業時間：11/2（一） 16:00 打烊（提醒）");
        Texts(early["contents"]!["body"]!).ShouldContain("　11/2（一） 16:00 前提醒關閉；11/3（二）11:00 前提醒重新開啟");

        var tuesday = new DateOnly(2026, 11, 3);
        var late = CoordinatorPreviewCard.Rows(Shop, ShopScenario.OpenLate, tuesday, new TimeOnly(12, 0), Modes(ShopScenario.OpenLate, ShopActionMode.Reminder));
        late.ShouldBe(
        [
            new CoordinatorCardRow("LINE 群發公告", ShopActionMode.Reminder),
            new CoordinatorCardRow("Xurf 暫停接單", ShopActionMode.Reminder, "11/3（二） 開店前提醒關閉；11/3（二）12:00 前提醒重新開啟"),
        ]);
    }

    [Fact]
    public void A_reopen_card_says_what_it_undoes_and_toggles_the_correction_broadcast()
    {
        var date = new DateOnly(2026, 11, 15);
        var modes = Modes(ShopScenario.TemporaryClosure, ShopActionMode.Reminder);
        CoordinatorCardContent Reopen(bool on) => new(
            Organization, Command, on ? 2 : 1, "王記麵店", ShopScenario.Reopen, date, null,
            CoordinatorPreviewCard.ReopenRows(Shop, ShopScenario.TemporaryClosure, date, modes, on),
            "【王記麵店】11/15（日） 照常營業。",
            new CoordinatorReopenDetails(ShopScenario.TemporaryClosure, date, null, CorrectionOffered: true, CorrectionBroadcast: on));

        var off = CoordinatorPreviewCard.Build(Reopen(false));

        off["altText"]!.GetValue<string>().ShouldBe("王記麵店 11/15（日） 恢復營業預覽");
        Texts(off["contents"]!["body"]!).ShouldBe(
        [
            "取消 11/15（日） 臨時店休",
            "・LINE 群發：已發出的公告無法收回",
            "・Google 特殊營業時間：移除 11/15（日） 的設定（提醒）",
            "・FB 粉專：刪除公告貼文（提醒）",
            "・Xurf 重新開啟接單（提醒）",
            "・foodpanda 重新開啟接單（提醒）",
            "不加發更正公告",
        ]);
        var toggle = Buttons(off)[0];
        toggle.Label.ShouldBe("加發更正公告");
        Route(toggle.Data).ShouldBe(new CoordinatorIntent(CoordinatorIntentKind.ToggleCorrection, OrganizationId: Organization, CommandId: Command, Version: 1));
        Buttons(off).Select(button => button.Label).ShouldBe(["加發更正公告", "取消", "確認"]);

        var on = CoordinatorPreviewCard.Build(Reopen(true));
        var body = Texts(on["contents"]!["body"]!);
        body.ShouldContain("・LINE 群發更正公告（提醒）");
        body.ShouldContain("更正公告");
        body.ShouldContain("【王記麵店】11/15（日） 照常營業。");
        body.ShouldContain("要修改公告，回覆「改成：新的內容」");
        body.ShouldNotContain("已接的訂單仍需處理");
        Buttons(on)[0].Label.ShouldBe("不加發更正公告");
        Route(Buttons(on)[2].Data).Version.ShouldBe(2);
        WithinLimits(off);
        WithinLimits(on);
    }

    [Fact]
    public void A_reopen_without_a_correction_has_no_toggle()
    {
        var date = new DateOnly(2026, 11, 15);
        var card = CoordinatorPreviewCard.Build(new CoordinatorCardContent(
            Organization, Command, 1, "王記麵店", ShopScenario.Reopen, date, null,
            CoordinatorPreviewCard.ReopenRows(Shop, ShopScenario.OpenLate, date, Modes(ShopScenario.OpenLate, ShopActionMode.Reminder), correction: false),
            null,
            new CoordinatorReopenDetails(ShopScenario.OpenLate, date, new TimeOnly(12, 0), CorrectionOffered: false, CorrectionBroadcast: false)));

        Texts(card["contents"]!["body"]!)[0].ShouldBe("取消 11/15（日） 延後開店 12:00");
        Texts(card["contents"]!["body"]!).ShouldNotContain("不加發更正公告");
        Buttons(card).Select(button => button.Label).ShouldBe(["取消", "確認"]);
    }

    [Fact]
    public void Long_names_and_a_1000_character_announcement_stay_within_lines_limits()
    {
        var date = new DateOnly(2026, 11, 2);
        var name = new string('店', 50);
        var card = CoordinatorPreviewCard.Build(new CoordinatorCardContent(
            Organization, Command, 99_999, name, ShopScenario.TemporaryClosure, date, null,
            CoordinatorPreviewCard.Rows(Shop, ShopScenario.TemporaryClosure, date, null, Modes(ShopScenario.TemporaryClosure, ShopActionMode.Auto)),
            new string('公', CoordinatorCommand.AnnouncementMaxLength)));

        WithinLimits(card);
        Encoding.UTF8.GetByteCount(card.ToJsonString()).ShouldBeLessThan(30_000, "LINE's limit on a Flex message");
    }

    [Fact]
    public void Rows_need_one_mode_per_action_and_only_a_reopen_card_has_reopen_details()
    {
        Should.Throw<ArgumentException>(() => CoordinatorPreviewCard.Rows(Shop, ShopScenario.CloseEarly, new DateOnly(2026, 11, 2), new TimeOnly(16, 0), [ShopActionMode.Auto]));
        Should.Throw<ArgumentException>(() => CoordinatorPreviewCard.Build(Content(ShopScenario.Reopen, new DateOnly(2026, 11, 2), null, [], null)));
    }

    [Fact]
    public void The_questions_offer_today_tomorrow_and_a_date_picker_or_a_time_picker()
    {
        var question = CoordinatorCommandReplies.DateQuestion("下週要休哪一天？", Organization, Command, new DateOnly(2026, 11, 2), new DateOnly(2027, 1, 31));

        question["text"]!.GetValue<string>().ShouldBe("下週要休哪一天？");
        var items = question["quickReply"]!["items"]!.AsArray().Select(item => item!["action"]!).ToList();
        items.Select(action => action["label"]!.GetValue<string>()).ShouldBe(["今天", "明天", "選日期"]);
        Route(items[1]["data"]!.GetValue<string>()).Date.ShouldBe(new DateOnly(2026, 11, 3));
        items[2]["type"]!.GetValue<string>().ShouldBe("datetimepicker");
        items[2]["mode"]!.GetValue<string>().ShouldBe("date");
        items[2]["min"]!.GetValue<string>().ShouldBe("2026-11-02");
        items[2]["max"]!.GetValue<string>().ShouldBe("2027-01-31");

        var time = CoordinatorCommandReplies.TimeQuestion("要提早到幾點打烊？", Organization, Command);
        var picker = time["quickReply"]!["items"]!.AsArray().ShouldHaveSingleItem()!["action"]!;
        picker["mode"]!.GetValue<string>().ShouldBe("time");
        Route(picker["data"]!.GetValue<string>(), ""","params":{"time":"16:00"}""").Time.ShouldBe(new TimeOnly(16, 0));
    }

    [Fact]
    public void The_shop_choice_offers_all_for_at_most_five_shops()
    {
        var five = Enumerable.Range(1, 5).Select(index => (Guid.CreateVersion7(), $"第{index}家店")).ToList();
        var choice = CoordinatorCommandReplies.ShopChoice(five);

        choice["text"]!.GetValue<string>().ShouldBe("哪一家？");
        var labels = choice["quickReply"]!["items"]!.AsArray().Select(item => item!["action"]!["label"]!.GetValue<string>()).ToList();
        labels.ShouldBe(["第1家店", "第2家店", "第3家店", "第4家店", "第5家店", "全部"]);
        Route(choice["quickReply"]!["items"]![5]!["action"]!["data"]!.GetValue<string>()).ShouldBe(new CoordinatorIntent(CoordinatorIntentKind.ChooseShop));

        var six = CoordinatorCommandReplies.ShopChoice([.. five, (Guid.CreateVersion7(), "第6家店")]);
        six["quickReply"]!["items"]!.AsArray().Select(item => item!["action"]!["label"]!.GetValue<string>()).ShouldNotContain("全部");

        var many = CoordinatorCommandReplies.ShopChoice([.. Enumerable.Range(1, 20).Select(index => (Guid.CreateVersion7(), new string('長', 30)))]);
        var manyItems = many["quickReply"]!["items"]!.AsArray();
        manyItems.Count.ShouldBe(13);
        manyItems.ShouldAllBe(item => item!["action"]!["label"]!.GetValue<string>().Length <= 20);
    }

    [Fact]
    public void The_fixed_texts_are_the_briefs()
    {
        CoordinatorCommandReplies.NotConfigured.ShouldBe("這家店還沒設定店務指令，請聯絡平台營運者。");
        CoordinatorCommandReplies.NoPreviewToEdit.ShouldBe("目前沒有待確認的預覽卡。");
        CoordinatorCommandReplies.RetypeCommand.ShouldBe("請重新輸入指令。");
        CoordinatorCommandReplies.Expired.ShouldBe("已失效，請重新下指令。");
        CoordinatorCommandReplies.AlreadyConfirmed.ShouldBe("這張預覽卡已經確認過了。");
        CoordinatorCommandReplies.Outdated.ShouldBe("預覽卡已更新，請按最新的那張。");
        CoordinatorCommandReplies.Cancelled.ShouldBe("已取消。");
        CoordinatorCommandReplies.NothingToReopen.ShouldBe("目前沒有可以恢復的店休或營業時間調整。");
        CoordinatorCommandReplies.Confirmed("王記麵店", ShopScenario.TemporaryClosure, new DateOnly(2026, 11, 2), null).ShouldBe("已確認：王記麵店 11/2（一） 臨時店休。");
        CoordinatorCommandReplies.Confirmed("王記麵店", ShopScenario.CloseEarly, new DateOnly(2026, 11, 2), new TimeOnly(16, 0))
            .ShouldBe("已確認：王記麵店 11/2（一） 提早打烊 16:00。");
    }

    private static IReadOnlyList<ShopActionMode> Modes(ShopScenario scenario, ShopActionMode mode) =>
        [.. Shop[scenario].Actions.Select(_ => mode)];

    private static CoordinatorCardContent Content(
        ShopScenario scenario, DateOnly date, TimeOnly? time, IReadOnlyList<CoordinatorCardRow> rows, string? announcement) =>
        new(Organization, Command, 1, "王記麵店", scenario, date, time, rows, announcement);

    /// <summary>Every text component's text, in order.</summary>
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

    private static List<(string Label, string Data)> Buttons(JsonObject card)
    {
        var buttons = new List<(string, string)>();
        Walk(card["contents"]!["footer"]!, element =>
        {
            if (element["type"]?.GetValue<string>() == "button")
            {
                buttons.Add((element["action"]!["label"]!.GetValue<string>(), element["action"]!["data"]!.GetValue<string>()));
            }
        });
        return buttons;
    }

    private static void WithinLimits(JsonObject card)
    {
        card["altText"]!.GetValue<string>().Length.ShouldBeInRange(1, CoordinatorPreviewCard.AltTextMaxLength);
        Walk(card["contents"]!, element =>
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

    private static CoordinatorIntent Route(string data, string parameters = "")
    {
        var body = $$$"""
            {"destination":"Ubot","events":[{"type":"postback","mode":"active","timestamp":1727000000000,
              "source":{"type":"user","userId":"Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"webhookEventId":"01J0000000000000000000000R",
              "replyToken":"reply","postback":{"data":{{{JsonSerializer.Serialize(data)}}}{{{parameters}}}}}]}
            """;
        return CoordinatorMessageRouter.Route(LineWebhookPayload.TryParse(Encoding.UTF8.GetBytes(body))!.Events.ShouldHaveSingleItem(), isBound: true);
    }
}
