using System.Text;
using System.Text.Json;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Application.Line;

namespace SmartAgri.Application.Tests.Coordinator;

/// <summary>What the 統籌助理 does with each kind of LINE event (store-coordinator design §5 Slice 1, #372).</summary>
public class CoordinatorMessageRouterTests
{
    private const string UserId = "Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid Organization = Guid.Parse("0194a100-0000-7000-8000-0000000000c1");

    [Theory]
    [InlineData(false, CoordinatorIntentKind.Welcome)]
    [InlineData(true, CoordinatorIntentKind.Menu)]
    public void Follow_welcomes_the_unbound_and_shows_members_the_menu(bool bound, CoordinatorIntentKind expected)
    {
        Route(Event("follow", UserSource()), bound).Kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_that_looks_like_a_code_is_a_bind_attempt_bound_or_not(bool bound)
    {
        var intent = Route(Text("ａｂｃｄ－２３４５"), bound);

        intent.Kind.ShouldBe(CoordinatorIntentKind.Bind);
        intent.Code.ShouldBe("ABCD2345");
        intent.ToString().ShouldNotContain("ABCD2345");
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("產生店長綁定碼")]
    [InlineData("今天臨時店休")]
    [InlineData("ABCD-234O")]
    public void The_unbound_are_asked_for_a_code_whatever_else_they_write(string text)
    {
        Route(Text(text), bound: false).Kind.ShouldBe(CoordinatorIntentKind.AskForCode);
    }

    [Theory]
    [InlineData("""{"id":"m1","type":"sticker","packageId":"446","stickerId":"1988"}""", false, CoordinatorIntentKind.AskForCode)]
    [InlineData("""{"id":"m1","type":"image"}""", false, CoordinatorIntentKind.AskForCode)]
    [InlineData("""{"id":"m1","type":"sticker","packageId":"446","stickerId":"1988"}""", true, CoordinatorIntentKind.Menu)]
    [InlineData("""{"id":"m1","type":"location","latitude":25,"longitude":121}""", true, CoordinatorIntentKind.Menu)]
    public void A_message_that_is_not_text_asks_for_a_code_or_shows_the_menu(string message, bool bound, CoordinatorIntentKind expected)
    {
        Route(Event("message", UserSource(), $$""","message":{{message}}"""), bound).Kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData("產生店長綁定碼")]
    [InlineData("  產生店長綁定碼\n")]
    public void A_member_asking_for_a_manager_code(string text)
    {
        Route(Text(text), bound: true).Kind.ShouldBe(CoordinatorIntentKind.IssueManagerCode);
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("今天臨時店休")]
    [InlineData("產生 店長綁定碼")]
    [InlineData("請問營業時間")]
    public void Any_other_text_from_a_member_is_a_command_kept_as_written(string text)
    {
        var intent = Route(Text($"  {text}\n"), bound: true);

        intent.Kind.ShouldBe(CoordinatorIntentKind.Command);
        intent.Text.ShouldBe(text);
        intent.ToString().ShouldNotContain(text);
    }

    [Fact]
    public void A_command_is_not_nfkc_normalized_because_it_may_end_up_in_an_announcement()
    {
        Route(Text("１１／１５店休！"), bound: true).Text.ShouldBe("１１／１５店休！");
    }

    [Theory]
    [InlineData("改成：今天休息，明天見！", "今天休息，明天見！")]
    [InlineData("改成:今天休息", "今天休息")]
    [InlineData("  改成：  第一行\n第二行  ", "第一行\n第二行")]
    [InlineData("改成：", "")]
    public void Text_starting_with_kai_cheng_edits_the_announcement(string text, string expected)
    {
        var intent = Route(Text(text), bound: true);

        intent.Kind.ShouldBe(CoordinatorIntentKind.EditAnnouncement);
        intent.Text.ShouldBe(expected);
    }

    [Fact]
    public void The_command_postbacks_round_trip_and_need_a_member()
    {
        var command = Guid.Parse("0194a100-0000-7000-8000-0000000000d1");
        var cases = new (string Data, CoordinatorIntent Expected, string? Params)[]
        {
            (CoordinatorMessageRouter.ShopPostback(Organization), new(CoordinatorIntentKind.ChooseShop, OrganizationId: Organization), null),
            (CoordinatorMessageRouter.ShopPostback(null), new(CoordinatorIntentKind.ChooseShop), null),
            (CoordinatorMessageRouter.DatePostback(Organization, command, new DateOnly(2026, 11, 3)),
                new(CoordinatorIntentKind.FillDate, OrganizationId: Organization, CommandId: command, Date: new DateOnly(2026, 11, 3)), null),
            (CoordinatorMessageRouter.DatePostback(Organization, command),
                new(CoordinatorIntentKind.FillDate, OrganizationId: Organization, CommandId: command, Date: new DateOnly(2026, 11, 9)),
                ""","params":{"date":"2026-11-09"}"""),
            (CoordinatorMessageRouter.DatePostback(Organization, command),
                new(CoordinatorIntentKind.FillDate, OrganizationId: Organization, CommandId: command), ""","params":{"date":"11/9"}"""),
            (CoordinatorMessageRouter.TimePostback(Organization, command),
                new(CoordinatorIntentKind.FillTime, OrganizationId: Organization, CommandId: command, Time: new TimeOnly(16, 0)),
                ""","params":{"time":"16:00"}"""),
            (CoordinatorMessageRouter.ConfirmPostback(Organization, command, 3),
                new(CoordinatorIntentKind.Confirm, OrganizationId: Organization, CommandId: command, Version: 3), null),
            (CoordinatorMessageRouter.CancelPostback(Organization, command, 1),
                new(CoordinatorIntentKind.Cancel, OrganizationId: Organization, CommandId: command, Version: 1), null),
            (CoordinatorMessageRouter.CorrectionPostback(Organization, command, 2),
                new(CoordinatorIntentKind.ToggleCorrection, OrganizationId: Organization, CommandId: command, Version: 2), null),
            (CoordinatorMessageRouter.ReopenPostback(Organization, command),
                new(CoordinatorIntentKind.ChooseReopen, OrganizationId: Organization, CommandId: command), null),
        };

        foreach (var (data, expected, parameters) in cases)
        {
            data.Length.ShouldBeLessThanOrEqualTo(300, "LINE's limit on postback data");
            var postback = Event("postback", UserSource(), $$""","postback":{"data":"{{data}}"{{parameters}}}""");
            Route(postback, bound: true).ShouldBe(expected, data);
            Route(postback, bound: false).Kind.ShouldBe(CoordinatorIntentKind.AskForCode, data);
        }
    }

    [Fact]
    public void The_store_choice_postback_names_the_organization_and_needs_a_member()
    {
        var data = CoordinatorMessageRouter.ManagerCodePostback(Organization);
        var postback = Event("postback", UserSource(), $$""","postback":{"data":"{{data}}"}""");

        var bound = Route(postback, bound: true);
        bound.Kind.ShouldBe(CoordinatorIntentKind.IssueManagerCodeFor);
        bound.OrganizationId.ShouldBe(Organization);
        Route(postback, bound: false).Kind.ShouldBe(CoordinatorIntentKind.AskForCode);
    }

    [Theory]
    [InlineData("coordinator:manager-code:")]
    [InlineData("coordinator:manager-code:not-a-guid")]
    [InlineData("coordinator:manager-code:00000000000000000000000000000000")]
    [InlineData("coordinator:confirm:0194a1000000700080000000000000c1:0194a1000000700080000000000000d1")]
    [InlineData("coordinator:confirm:0194a1000000700080000000000000c1:0194a1000000700080000000000000d1:0")]
    [InlineData("coordinator:confirm:0194a1000000700080000000000000c1:0194a1000000700080000000000000d1:-1")]
    [InlineData("coordinator:cancel:0194a1000000700080000000000000c1:not-a-guid:1")]
    [InlineData("coordinator:shop:everything")]
    [InlineData("coordinator:time:0194a1000000700080000000000000c1")]
    [InlineData("coordinator:date:0194a1000000700080000000000000c1:0194a1000000700080000000000000d1:2026-11-03:x")]
    [InlineData("action=buy&item=1")]
    public void Any_other_postback_is_ignored(string data)
    {
        Route(Event("postback", UserSource(), $$""","postback":{"data":"{{data}}"}"""), bound: true).Kind.ShouldBe(CoordinatorIntentKind.Ignore);
    }

    [Theory]
    [InlineData("""{"type":"group","groupId":"Cgroup","userId":"Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("""{"type":"room","roomId":"Rroom","userId":"Uaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("""{"type":"user"}""")]
    public void Groups_rooms_and_events_without_a_user_are_ignored(string source)
    {
        Route(Event("message", source, ""","message":{"id":"m","type":"text","text":"ABCD-2345"}"""), bound: true).Kind
            .ShouldBe(CoordinatorIntentKind.Ignore);
        Route(Event("follow", source), bound: false).Kind.ShouldBe(CoordinatorIntentKind.Ignore);
    }

    [Theory]
    [InlineData("unfollow")]
    [InlineData("unsend")]
    [InlineData("memberJoined")]
    [InlineData("videoPlayComplete")]
    public void Other_event_types_are_ignored(string type)
    {
        Route(Event(type, UserSource()), bound: true).Kind.ShouldBe(CoordinatorIntentKind.Ignore);
    }

    [Fact]
    public void Standby_mode_is_ignored()
    {
        Route(Event("message", UserSource(), ""","message":{"id":"m","type":"text","text":"你好"}""", mode: "standby"), bound: true).Kind
            .ShouldBe(CoordinatorIntentKind.Ignore);
    }

    [Fact]
    public void Text_is_read_as_nfkc_and_trimmed()
    {
        CoordinatorMessageRouter.NormalizeText("  ＡＢＣ　１２３ ").ShouldBe("ABC 123");
        CoordinatorMessageRouter.NormalizeText("bad\uD800").ShouldBe("bad\uD800");
    }

    private static CoordinatorIntent Route(LineWebhookEvent lineEvent, bool bound) => CoordinatorMessageRouter.Route(lineEvent, bound);

    private static string UserSource() => $$"""{"type":"user","userId":"{{UserId}}"}""";

    private static LineWebhookEvent Text(string text) =>
        Event("message", UserSource(), $$""","message":{"id":"m","type":"text","text":{{JsonSerializer.Serialize(text)}}}""");

    private static LineWebhookEvent Event(string type, string source, string extra = "", string mode = "active")
    {
        var body = $$"""
            {"destination":"Ubot","events":[{"type":"{{type}}","mode":"{{mode}}","timestamp":1727000000000,
              "source":{{source}},"webhookEventId":"01J0000000000000000000000R","replyToken":"reply"{{extra}}}]}
            """;
        return LineWebhookPayload.TryParse(Encoding.UTF8.GetBytes(body))!.Events.ShouldHaveSingleItem();
    }
}
